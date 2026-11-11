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

    /// <summary>[EnableRateLimiting("策略名")] 解析出的限流策略(方法级优先、接口级兜底)。</summary>
    public string? RateLimitPolicy { get; init; }

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
        var parameters = ContractInvokerCache.GetParameters(endpoint.Method);

        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            args[i] = await BindParameterAsync(context, parameters[i], endpoint.BodyParam, routeValues);
        }

        object? result;
        if (ContractInvokerCache.GetInvoker(endpoint.Method) is { } invoke)
        {
            // 编译委托直调:业务异常原样抛出(无 TargetInvocationException 包装)
            result = invoke(service, args);
        }
        else
        {
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
        }

        if (result is Task task)
        {
            await task;
            // void/Task → 空载荷成功信封(200);Task<T> → 取声明 Result 包装(编译委托,零反射)
            var value = endpoint.DeclaredResult is not null
                ? ContractInvokerCache.GetTaskResultGetter(endpoint.DeclaredResult)(task)
                : null;
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
            var form = await ReadFormSafeAsync(context, parameter.Name!);
            return form.Files.Count > 0 ? form.Files[0] : null;
        }
        if (parameter.ParameterType == typeof(IFormFileCollection))
        {
            var form = await ReadFormSafeAsync(context, parameter.Name!);
            return form.Files;
        }

        // 简单元素集合(Array/List/IEnumerable/HashSet):从 Query 逗号分隔或重复键绑定
        // (探测结论按 Type 缓存——MakeGenericType 每次 96B 分配)
        var collectionProbe = ContractInvokerCache.ProbeSimpleCollection(parameter.ParameterType);
        if (collectionProbe.Is)
        {
            return BindSimpleCollection(context, parameter, collectionProbe.Element!, collectionProbe.Concrete!);
        }

        if (!IsSimple(parameter.ParameterType))
        {
            // [FromForm] 复杂对象:从表单字段绑定(简单属性同名映射)
            // (探测按 ParameterInfo 缓存——GetCustomAttribute 每次 1595ns/528B 重新实例化特性)
            if (ContractInvokerCache.HasFromForm(parameter))
            {
                return await BindFromFormAsync(context, parameter.ParameterType);
            }
            // 复杂类型:JSON Body(每方法至多一个,映射期校验);
            // multipart 场景下从同名表单字段的 JSON 文本解析(对象与文件同请求混搭)
            // SSS+输入攻击审核:JSON 解析失败/Content-Type 谎称/表单垃圾体均为客户端错误(40001/400),不进 500
            try
            {
                if (context.Request.HasFormContentType)
                {
                    var form = await ReadFormSafeAsync(context, parameter.Name!);
                    if (!form.TryGetValue(parameter.Name!, out var json))
                    {
                        // SSS+输入攻击审核:multipart 缺少必需 Body 字段 → 40001
                        //(此前返回 null 进业务 NRE → 50000)
                        throw new MeshException(
                            $"缺少请求体字段 {parameter.Name}(multipart/form-data)", errorCode: 40001, statusCode: 400);
                    }
                    return System.Text.Json.JsonSerializer.Deserialize(json.ToString(), parameter.ParameterType,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                }
                if (!context.Request.HasJsonContentType())
                {
                    // Content-Type 谎称(text/plain 载 JSON)/缺失:此前 InvalidOperationException 逃逸 → 500
                    throw new MeshException(
                        $"请求体需要 application/json Content-Type(字段 {parameter.Name})", errorCode: 40001, statusCode: 400);
                }
                return await context.Request.ReadFromJsonAsync(parameter.ParameterType);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or System.IO.IOException)
            {
                throw new MeshException(
                    $"请求体解析失败(字段 {parameter.Name}):请确认 JSON 合法且 Content-Type 为 application/json", errorCode: 40001, statusCode: 400);
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
            // SSS+输入攻击审核:标量参数重复键取首值(MVC 语义)——此前 StringValues.ToString()
            // 逗号连接 × decimal AllowThousands → "1,25" 静默变 125(数据损坏)
            return ConvertSimple(queryValue.Count > 1 ? queryValue[0] : queryValue.ToString(), parameter);
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
            // 快路径直接 Parse(实测 int 175ns/24B→18ns/0B);枚举等冷类型回退 TypeDescriptor
            if (SimpleConverter.TryConvert(raw, underlying, out var fast))
            {
                return fast;
            }
            return TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(raw);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or NotSupportedException)
        {
            throw new MeshException(
                $"参数 {parameter.Name} 格式错误:\"{raw}\" 无法转换为 {underlying.Name}",
                errorCode: 40001, statusCode: 400);
        }
    }

    internal static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        // 简单类型判定 = 唯一真值源(schema 生成与 query/Body 判定共用)。
        // 注意:不含 Half——TypeDescriptor 无 Half 转换器,query 绑定会 400;
        // schema 侧跟随本判定(Half 按对象分支展开,取舍:文档与绑定行为严格一致)
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid);
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
        // SSS+输入攻击审核:混合形态(重复键×逗号值)统一拍平(此前 decimal "1,2" 经 AllowThousands
        // 静默变 12);HashSet/ISet 不实现 IList,统一以 List 填充后按需转换具体类型(此前 InvalidCast → 500)
        var raw = values.Count > 1
            ? values.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : (IEnumerable<string>)(values[0] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var list = new List<object?>();
        foreach (var item in raw)
        {
            list.Add(ConvertElement(item ?? "", elementType, parameter));
        }
        if (parameter.ParameterType.IsArray)
        {
            // Array 参数:List → ToArray(编译委托,零反射)
            var typedList = (System.Collections.IList)Activator.CreateInstance(concreteListType)!;
            foreach (var item in list)
            {
                typedList.Add(item);
            }
            return ContractInvokerCache.GetCollectionToArray(elementType)(typedList);
        }
        if (concreteListType == typeof(HashSet<>) || typeof(System.Collections.IEnumerable).IsAssignableFrom(concreteListType) && concreteListType != typeof(List<>))
        {
            // HashSet<T>/ISet<T>:经 IEnumerable<T> 构造器转换(去重语义归类型本身)
            var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
            var typedList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            foreach (var item in list)
            {
                typedList.Add(item);
            }
            return Activator.CreateInstance(concreteListType, enumerableType.IsAssignableFrom(typedList.GetType()) ? typedList : null)!;
        }
        var resultList = (System.Collections.IList)Activator.CreateInstance(concreteListType)!;
        foreach (var item in list)
        {
            resultList.Add(item);
        }
        return resultList;
    }

    private static object? ConvertElement(string raw, Type elementType, ParameterInfo parameter)
    {
        var underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;
        try
        {
            if (SimpleConverter.TryConvert(raw, underlying, out var fast))
            {
                return fast;
            }
            return TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(raw);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or NotSupportedException)
        {
            throw new MeshException(
                $"参数 {parameter.Name} 的集合元素 \"{raw}\" 无法转换为 {underlying.Name}",
                errorCode: 40001, statusCode: 400);
        }
    }

    /// <summary>[FromForm] 复杂对象:公共可写简单属性按表单字段名(忽略大小写)填充,嵌套/复杂属性忽略。</summary>
    private static async Task<object?> BindFromFormAsync(HttpContext context, Type targetType)
    {
        var form = await ReadFormSafeAsync(context, targetType.Name);
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
                prop.SetValue(instance, SimpleConverter.TryConvert(field.ToString(), underlying, out var fast)
                    ? fast
                    : TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(field.ToString()));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or NotSupportedException)
            {
                throw new MeshException($"表单字段 {prop.Name} 格式错误", 40001, 400);
            }
        }
        return instance;
    }

    // 装箱默认值缓存:值类型参数在调用点 unbox 即拷贝,共享同一装箱实例安全(替代每请求 Activator.CreateInstance)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> BoxedDefaults = new();

    /// <summary>表单读取安全封装:伪 multipart/垃圾体等解析异常统一 40001(此前逃逸 → 500)。</summary>
    private static async Task<IFormCollection> ReadFormSafeAsync(HttpContext context, string field)
    {
        try
        {
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 表单解析任何失败(伪 multipart/缺 boundary/InvalidDataException 族)均为客户端错误
            throw new MeshException($"表单解析失败(字段 {field}):请确认 multipart/form-data 编码合法", errorCode: 40001, statusCode: 400);
        }
    }

    internal static object? DefaultOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? BoxedDefaults.GetOrAdd(type, static t => Activator.CreateInstance(t)!)
            : null;

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
        await context.Response.WriteAsJsonAsync(envelope, context.RequestAborted);
    }
}
