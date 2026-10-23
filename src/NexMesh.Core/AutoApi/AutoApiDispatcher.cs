namespace NexMesh.Core.AutoApi;

using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>契约端点执行元数据(字面量端点与虚拟网关共用)。</summary>
public sealed record AutoApiEndpoint(
    Type ServiceType,
    MethodInfo Method,
    string Verb,
    string Route,
    ParameterInfo? BodyParam,
    PropertyInfo? DeclaredResult)
{
    /// <summary>显式 [AllowAnonymous]:任何模式下放行。</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>显式 [Authorize] 标注存在(与默认拒绝配置共同决定 RequireAuthorization)。</summary>
    public bool ExplicitAuthorize { get; init; }

    /// <summary>[Authorize(Roles)] 解析出的角色集合(逗号分隔)。</summary>
    public string[]? Roles { get; init; }

    /// <summary>OpenAPI 分组标签(契约接口名)。</summary>
    public string? Tags { get; init; }
}

/// <summary>
/// 共享调度器:有界参数绑定(路由值/Query/单 Body)+ TargetInvocation 还原 + 按声明返回类型解包。
/// 字面量端点(MapAutoApis)与虚拟网关(MapAutoApiGateway)走同一执行路径,行为完全一致。
/// </summary>
internal static class AutoApiDispatcher
{
    public static async Task InvokeAsync(
        HttpContext context,
        AutoApiEndpoint endpoint,
        IReadOnlyDictionary<string, string>? routeValues = null)
    {
        var service = context.RequestServices.GetRequiredService(endpoint.ServiceType);
        var parameters = endpoint.Method.GetParameters();

        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            args[i] = await BindParameterAsync(context, parameters[i], endpoint.BodyParam, routeValues);
        }

        object? result;
        try
        {
            result = endpoint.Method.Invoke(service, args);
        }
        catch (TargetInvocationException ex)
        {
            // 还原业务原始异常,交给全局异常中间件按类型分诊
            ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
            return;
        }

        if (result is Task task)
        {
            await task;
            // void/Task → 空载荷成功信封(200);Task<T> → 取声明 Result 包装
            var value = endpoint.DeclaredResult != null ? endpoint.DeclaredResult.GetValue(task) : null;
            await WriteResultAsync(context, value);
            return;
        }
        await WriteResultAsync(context, result);
    }

    private static async Task<object?> BindParameterAsync(
        HttpContext context,
        ParameterInfo parameter,
        ParameterInfo? bodyParam,
        IReadOnlyDictionary<string, string>? routeValues)
    {
        if (parameter.ParameterType == typeof(CancellationToken))
        {
            return context.RequestAborted;
        }

        // 文件直绑:IFormFile / IFormFileCollection(需 multipart/form-data 请求)
        if (parameter.ParameterType == typeof(IFormFile))
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return form.Files.Count > 0 ? form.Files[0] : null;
        }
        if (parameter.ParameterType == typeof(IFormFileCollection))
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return form.Files;
        }

        // 简单元素集合(Array/List/IEnumerable/HashSet):从 Query 逗号分隔或重复键绑定
        if (IsSimpleCollection(parameter.ParameterType, out var elementType, out var concreteType))
        {
            return BindSimpleCollection(context, parameter, elementType, concreteType);
        }

        if (!IsSimple(parameter.ParameterType))
        {
            // [FromForm] 复杂对象:从表单字段绑定(简单属性同名映射)
            if (parameter.GetCustomAttribute<FromFormAttribute>() is not null)
            {
                return await BindFromFormAsync(context, parameter.ParameterType);
            }
            // 复杂类型:JSON Body(每方法至多一个,映射期校验);
            // multipart 场景下从同名表单字段的 JSON 文本解析(对象与文件同请求混搭)
            try
            {
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    if (!form.TryGetValue(parameter.Name!, out var json))
                    {
                        return null;
                    }
                    return System.Text.Json.JsonSerializer.Deserialize(json.ToString(), parameter.ParameterType,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                }
                return await context.Request.ReadFromJsonAsync(parameter.ParameterType);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new MeshException(
                    $"请求体 JSON 解析失败(字段 {parameter.Name})", errorCode: 40001, statusCode: 400);
            }
        }

        // 简单类型:优先路由值(网关解析值或字面量路由值),其次 Query(同名忽略大小写)
        var name = parameter.Name!;
        if (routeValues is not null && routeValues.TryGetValue(name, out var gatewayValue))
        {
            return ConvertSimple(gatewayValue, parameter);
        }
        if (context.Request.RouteValues.TryGetValue(name, out var routeValue) && routeValue is not null)
        {
            return ConvertSimple(routeValue.ToString(), parameter);
        }
        if (context.Request.Query.TryGetValue(name, out var queryValue))
        {
            return ConvertSimple(queryValue.ToString(), parameter);
        }

        // 缺省:参数默认值 → 类型默认值
        return parameter.HasDefaultValue
            ? parameter.DefaultValue
            : DefaultOf(parameter.ParameterType);
    }

    internal static object? ConvertSimple(string? raw, ParameterInfo parameter)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return parameter.HasDefaultValue ? parameter.DefaultValue : DefaultOf(parameter.ParameterType);
        }

        var targetType = parameter.ParameterType;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            return underlying == typeof(string)
                ? raw
                : TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(raw);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            throw new MeshException(
                $"参数 {parameter.Name} 格式错误:\"{raw}\" 无法转换为 {underlying.Name}",
                errorCode: 40001, statusCode: 400);
        }
    }

    internal static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        // 与 ContractSchemaGenerator.IsSimple 保持同集(含 Half):两处判定不一致会导致参数
        // 在"query 收集/Body 判定/文档 schema"间错位——一边认一边不认时参数从文档静默消失
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid)
            || t == typeof(Half);
    }

    /// <summary>简单元素集合识别:T[]/List&lt;T&gt;/IList&lt;T&gt;/IEnumerable&lt;T&gt;/HashSet&lt;T&gt;(元素为简单类型)。</summary>
    internal static bool IsSimpleCollection(Type type, out Type elementType, out Type concreteListType)
    {
        elementType = null!;
        concreteListType = null!;
        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            if (!IsSimple(element))
            {
                return false;
            }
            elementType = element;
            concreteListType = typeof(List<>).MakeGenericType(element);
            return true;
        }
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def != typeof(List<>) && def != typeof(IList<>) && def != typeof(IEnumerable<>) && def != typeof(HashSet<>) && def != typeof(ISet<>))
            {
                return false;
            }
            var element = type.GetGenericArguments()[0];
            if (!IsSimple(element))
            {
                return false;
            }
            elementType = element;
            concreteListType = def == typeof(HashSet<>) || def == typeof(ISet<>)
                ? typeof(HashSet<>).MakeGenericType(element)
                : typeof(List<>).MakeGenericType(element);
            return true;
        }
        return false;
    }

    /// <summary>集合绑定:优先 Query 重复键(ids=1&amp;ids=2),单值时尝试逗号分隔(ids=1,2,3)。</summary>
    private static object? BindSimpleCollection(
        HttpContext context, ParameterInfo parameter, Type elementType, Type concreteListType)
    {
        if (!context.Request.Query.TryGetValue(parameter.Name!, out var values) || values.Count == 0)
        {
            return null; // 未提供 → null(引用类型集合),业务自行判空
        }
        var raw = values.Count > 1
            ? values.ToArray()
            : (values[0] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var list = (System.Collections.IList)Activator.CreateInstance(concreteListType)!;
        foreach (var item in raw)
        {
            list.Add(ConvertElement(item ?? "", elementType, parameter));
        }
        // Array 参数:List → ToArray
        return parameter.ParameterType.IsArray
            ? concreteListType.GetMethod("ToArray")!.Invoke(list, null)
            : list;
    }

    private static object? ConvertElement(string raw, Type elementType, ParameterInfo parameter)
    {
        var underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;
        try
        {
            return underlying == typeof(string) ? raw
                : TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(raw);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            throw new MeshException(
                $"参数 {parameter.Name} 的集合元素 \"{raw}\" 无法转换为 {underlying.Name}",
                errorCode: 40001, statusCode: 400);
        }
    }

    /// <summary>[FromForm] 复杂对象:公共可写简单属性按表单字段名(忽略大小写)填充,嵌套/复杂属性忽略。</summary>
    private static async Task<object?> BindFromFormAsync(HttpContext context, Type targetType)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form.Count == 0 && form.Files.Count == 0)
        {
            return null;
        }
        var instance = Activator.CreateInstance(targetType)
            ?? throw new MeshException($"无法实例化表单类型 {targetType.Name}", 50001, 500);

        foreach (var prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite)
            {
                continue;
            }
            // IFormFile 属性:按属性名匹配文件
            if (typeof(IFormFile).IsAssignableFrom(prop.PropertyType))
            {
                var file = form.Files.FirstOrDefault(f =>
                    string.Equals(f.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
                if (file is not null)
                {
                    prop.SetValue(instance, file);
                }
                continue;
            }
            if (!IsSimple(prop.PropertyType) || !form.TryGetValue(prop.Name, out var field))
            {
                continue;
            }
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            try
            {
                prop.SetValue(instance, underlying == typeof(string)
                    ? field.ToString()
                    : TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(field.ToString()));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
            {
                throw new MeshException($"表单字段 {prop.Name} 格式错误", 40001, 400);
            }
        }
        return instance;
    }

    internal static object? DefaultOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

    private static async Task WriteResultAsync(HttpContext context, object? result)
    {
        // IActionResult(文件流/重定向等)按 MVC 语义执行,而非信封包装
        if (result is Microsoft.AspNetCore.Mvc.IActionResult action)
        {
            await action.ExecuteResultAsync(new Microsoft.AspNetCore.Mvc.ActionContext(
                context, context.GetRouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
            return;
        }
        // 统一 ApiResult 信封:Success=true/Code=0/Data(含 null)/TraceId(经宿主 JsonOptions 序列化)
        var envelope = ApiResult<object?>.Ok(result);
        envelope.TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
        await context.Response.WriteAsJsonAsync(envelope);
    }
}
