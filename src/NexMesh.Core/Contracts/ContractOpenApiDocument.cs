namespace NexMesh;

using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;

/// <summary>
/// 契约端点 OpenAPI 文档生成器:反射式 RequestDelegate 端点不被 .NET OpenAPI 生成器自动收录,
/// 由此直接产出文档 JSON(Scalar/Swagger UI 消费)——path+method+tags+summary(取程序集 XML 注释)
/// +path/query 参数+**字段级请求体/响应 schema**(属性递归展开+字段中文注释+示例值)。
/// </summary>
public static class ContractOpenApiDocument
{
    private const int MaxDepth = 4;

    /// <summary>生成 OpenAPI 3.2 文档(JSON DOM);title/version 可从 NexMesh:Scalar:Document 读取。</summary>
    public static JsonObject Build(Assembly[] assemblies, string? title = null, string? version = null, string? description = null)
    {
        // 闭包扫描会排除 NexMesh.* 程序集;文档需要框架类型(PagedResult 等)的字段注释,显式追加(不动入参)
        var scanned = assemblies.Contains(typeof(ContractOpenApiDocument).Assembly)
            ? assemblies
            : [.. assemblies, typeof(ContractOpenApiDocument).Assembly];
        var summaries = LoadXmlSummaries(scanned);
        var schemaGen = new ContractSchemaGenerator(summaries);
        var paths = new JsonObject();
        var tags = new JsonObject();

        foreach (var endpoint in ContractApiExtensions.ScanContractEndpoints(scanned))
        {
            var methodId = $"{endpoint.ServiceType.FullName}.{endpoint.Method.Name}";
            var summary = summaries.TryGetValue(methodId, out var xml)
                ? xml
                : endpoint.Method.Name.EndsWith("Async", StringComparison.Ordinal)
                    ? endpoint.Method.Name[..^5]
                    : endpoint.Method.Name;
            var tagName = endpoint.Tags ?? endpoint.ServiceType.Name;

            var operation = new JsonObject
            {
                ["summary"] = summary,
                ["operationId"] = methodId,
                ["tags"] = new JsonArray(tagName),
            };

            var parameters = new JsonArray();
            foreach (var segment in endpoint.Route.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!segment.StartsWith('{') || !segment.EndsWith('}'))
                {
                    continue;
                }
                var name = segment.Trim('{', '}').Split(':')[0];
                parameters.Add(new JsonObject
                {
                    ["name"] = name,
                    ["in"] = "path",
                    ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                });
            }
            foreach (var param in endpoint.Method.GetParameters())
            {
                if (param.ParameterType == typeof(CancellationToken)
                    || endpoint.BodyParam == param
                    || param.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromBodyAttribute>() is not null
                    || param.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromRouteAttribute>() is not null)
                {
                    continue;
                }
                var inRoute = endpoint.Route.Contains($"{{{param.Name}", StringComparison.Ordinal);
                if (inRoute || !NexMesh.Core.AutoApi.AutoApiDispatcher.IsSimple(param.ParameterType))
                {
                    continue; // 路由参数已收集;复杂类型走请求体
                }
                parameters.Add(new JsonObject
                {
                    ["name"] = param.Name,
                    ["in"] = "query",
                    ["required"] = !param.HasDefaultValue,
                    ["schema"] = schemaGen.Simple(param.ParameterType),
                });
            }
            if (parameters.Count > 0)
            {
                operation["parameters"] = parameters;
            }

            // 请求体:字段级 schema + 默认值示例(Scalar 据此渲染 curl --data 完整字段)
            if (endpoint.BodyParam is { } body)
            {
                var bodySchema = schemaGen.Of(body.ParameterType, 0);
                operation["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = bodySchema,
                            ["example"] = schemaGen.Example(body.ParameterType, 0),
                        },
                    },
                };
            }

            // 响应:200 成功信封(success=true/code=0/data 字段级) + 常见失败状态码(错误信封,与异常族映射一致)
            var responseSchema = endpoint.DeclaredResult is { } result
                ? EnvelopeSchema(schemaGen.Of(result.PropertyType, 0))
                : EnvelopeSchema(null);
            var errorContent = new JsonObject
            {
                ["application/json"] = new JsonObject { ["schema"] = ErrorEnvelopeSchema() },
            };
            operation["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = endpoint.DeclaredResult is null
                        ? "成功(空载荷信封)"
                        : "成功(ApiResult 信封:data 为业务载荷)",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = responseSchema },
                    },
                },
                ["400"] = new JsonObject { ["description"] = "业务/参数错误(信封 success=false, code=400)", ["content"] = errorContent.DeepClone() },
                ["401"] = new JsonObject { ["description"] = "未认证或令牌无效(code=401)", ["content"] = errorContent.DeepClone() },
                ["404"] = new JsonObject { ["description"] = "资源不存在(code=404)", ["content"] = errorContent.DeepClone() },
                ["500"] = new JsonObject { ["description"] = "服务器内部错误(code=500,消息不暴露细节)", ["content"] = errorContent },
            };

            var pathKey = "/" + endpoint.Route.TrimStart('/');
            var verb = endpoint.Verb.ToLowerInvariant();
            var pathItem = paths[pathKey] as JsonObject ?? new JsonObject();
            pathItem[verb] = operation;
            paths[pathKey] = pathItem;

            // 首次出现即写入(空串占位):避免"先无注释后有"时分组描述丢失
            tags[tagName] = endpoint.ServiceType.FullName is { Length: > 0 } typeName
                && summaries.TryGetValue(typeName, out var typeSummary)
                    ? typeSummary
                    : "";
        }

        return new JsonObject
        {
            ["openapi"] = "3.2.0",
            ["info"] = new JsonObject
            {
                ["title"] = string.IsNullOrWhiteSpace(title) ? "NexMesh API" : title,
                ["version"] = string.IsNullOrWhiteSpace(version) ? "v1" : version,
                ["description"] = string.IsNullOrWhiteSpace(description) ? null : description,
            },
            ["paths"] = paths,
            ["tags"] = new JsonArray(
                tags.Select(kv => (JsonNode)new JsonObject
                {
                    ["name"] = kv.Key,
                    ["description"] = kv.Value?.GetValue<string>() is { Length: > 0 } d ? d : null,
                }).ToArray()),
        };
    }

    /// <summary>统一响应信封 schema。</summary>
    private static JsonObject EnvelopeSchema(JsonObject? dataSchema) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["success"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否成功" },
            ["code"] = new JsonObject { ["type"] = "integer", ["description"] = "0=成功;失败为 HTTP 状态码" },
            ["message"] = new JsonObject { ["type"] = "string" },
            ["data"] = dataSchema is not null
                ? dataSchema
                : new JsonObject { ["type"] = "object", ["nullable"] = true, ["description"] = "空载荷" }, // 带 type:无类型 schema 部分 UI 渲染不稳定
            ["traceId"] = new JsonObject { ["type"] = "string", ["nullable"] = true },
            ["timestamp"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
        },
    };

    /// <summary>失败响应信封(与异常族→HTTP 状态码映射一致):success=false/code=状态码/message/errors。</summary>
    private static JsonObject ErrorEnvelopeSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["success"] = new JsonObject { ["type"] = "boolean", ["description"] = "false" },
            ["code"] = new JsonObject { ["type"] = "integer", ["description"] = "HTTP 状态码" },
            ["message"] = new JsonObject { ["type"] = "string", ["description"] = "错误消息(500 不暴露内部细节)" },
            ["traceId"] = new JsonObject { ["type"] = "string", ["nullable"] = true },
            ["timestamp"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["errors"] = new JsonObject
            {
                ["type"] = "object",
                ["nullable"] = true,
                ["description"] = "字段级校验错误(ValidationException 专用):字段名→消息数组",
                ["additionalProperties"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            },
        },
    };

    /// <summary>加载程序集 XML 注释:M:全限定.方法名 与 P:全限定.属性 → summary;T:全限定 → 类型 summary。</summary>
    private static Dictionary<string, string> LoadXmlSummaries(Assembly[] assemblies)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asm in assemblies)
        {
            string? xmlPath;
            try
            {
                xmlPath = Path.ChangeExtension(asm.Location, ".xml");
            }
            catch (NotSupportedException)
            {
                continue; // 动态程序集无 Location
            }
            if (xmlPath is null || !File.Exists(xmlPath))
            {
                continue;
            }
            try
            {
                var doc = XDocument.Load(xmlPath);
                foreach (var member in doc.Descendants("member"))
                {
                    var name = member.Attribute("name")?.Value;
                    if (name is null || name.Length < 3 || name[1] != ':')
                    {
                        continue;
                    }
                    name = name[2..]; // 去 M:/T:/P: 前缀,与 FullName/MethodId/属性路径对齐
                    var paren = name.IndexOf('(');
                    if (paren > 0)
                    {
                        name = name[..paren];
                    }
                    var summary = member.Element("summary")?.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        result.TryAdd(name, summary);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                // XML 注释损坏不阻断文档生成
            }
        }
        return result;
    }
}

/// <summary>字段级 schema/示例生成:属性递归展开 + XML 字段注释 + 类型推断示例值;限深防循环。</summary>
internal sealed class ContractSchemaGenerator(Dictionary<string, string> summaries)
{
    private const int MaxDepth = 4;

    private readonly Dictionary<Type, JsonObject> _cache = [];

    public JsonObject Simple(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var isNullable = Nullable.GetUnderlyingType(type) is not null || !type.IsValueType;

        JsonObject With(JsonObject node)
        {
            if (isNullable)
            {
                node["nullable"] = true;
            }
            return node;
        }

        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            return With(new JsonObject { ["type"] = "string", ["format"] = "date-time" });
        }
        if (underlying == typeof(Guid))
        {
            return With(new JsonObject { ["type"] = "string", ["format"] = "uuid" });
        }
        if (underlying.IsEnum)
        {
            return With(new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames(underlying).Select(n => (JsonNode)n!).ToArray()),
            });
        }
        // 数值精确分型:浮点→number(+format);64 位整→integer+int64(JS Number 精度提示);其余整型→integer
        if (underlying == typeof(float))
        {
            return With(new JsonObject { ["type"] = "number", ["format"] = "float" });
        }
        if (underlying == typeof(double) || underlying == typeof(decimal))
        {
            return With(new JsonObject { ["type"] = "number", ["format"] = "double" });
        }
        if (underlying == typeof(Half))
        {
            return With(new JsonObject { ["type"] = "number", ["format"] = "float" });
        }
        if (underlying == typeof(long) || underlying == typeof(nint))
        {
            return With(new JsonObject { ["type"] = "integer", ["format"] = "int64" });
        }
        if (underlying == typeof(ulong) || underlying == typeof(nuint))
        {
            return With(new JsonObject { ["type"] = "integer", ["format"] = "int64", ["minimum"] = 0 });
        }
        // 显式表:bool/string/char 与 uint(32 位无符号,防消费方按有符号解析溢出)/ushort 同理;其余小整型 integer
        return underlying switch
        {
            _ when underlying == typeof(bool) => With(new JsonObject { ["type"] = "boolean" }),
            _ when underlying == typeof(string) || underlying == typeof(char) => With(new JsonObject { ["type"] = "string" }),
            _ when underlying == typeof(uint) || underlying == typeof(ushort) => With(new JsonObject { ["type"] = "integer", ["format"] = "int32", ["minimum"] = 0 }),
            _ => With(new JsonObject { ["type"] = "integer" }), // byte/sbyte/short/int
        };
    }

    /// <summary>CLR 类型 → schema:字典(含 KeyValuePair 序列)→additionalProperties;集合→array;复杂对象→属性递归(字段中文注释+required);基元/枚举/日期→简单 schema。</summary>
    public JsonObject Of(Type type, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (IsSimple(underlying))
        {
            return Simple(type); // 含枚举/日期/Guid/基元/Half
        }
        if (depth >= MaxDepth)
        {
            return new JsonObject { ["type"] = "object", ["description"] = TypeName(underlying) };
        }
        // 字典(IDictionary<,>/IReadOnlyDictionary<,> 及其实现:Dictionary/ConcurrentDictionary 等)→ additionalProperties
        if (GetDictionaryValueType(underlying) is { } dictValue)
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = Of(dictValue, depth + 1),
                ["description"] = "字典",
                ["nullable"] = true,
            };
        }
        // IEnumerable<KeyValuePair<,>> / KeyValuePair<,>[] 形式的字典序列:同样收敛为 map schema(而非 key/value 对象数组)
        if (IsKeyValuePairEnumerable(underlying))
        {
            var valueType = GetKeyValuePairType(underlying)!.GetGenericArguments()[1];
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = Of(valueType, depth + 1),
                ["description"] = "字典(KeyValuePair 序列)",
                ["nullable"] = true,
            };
        }
        // KeyValuePair<,> 本身:显式 key/value 对象(不落属性递归,避免误导)
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            var args = underlying.GetGenericArguments();
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = Of(args[0], depth + 1),
                    ["value"] = Of(args[1], depth + 1),
                },
            };
        }
        if (underlying.IsArray || IsEnumerable(underlying))
        {
            var element = GetElementType(underlying);
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = IsSimple(element) ? Simple(element) : Of(element, depth + 1),
                ["nullable"] = true,
            };
        }
        if (underlying == typeof(object) || underlying == typeof(System.Text.Json.JsonElement))
        {
            return new JsonObject { ["description"] = underlying == typeof(object) ? "任意结构" : "JSON 结构" };
        }

        if (_cache.TryGetValue(underlying, out var cached))
        {
            return cached is JsonObject shallow ? DeepClone(shallow) : cached; // 深拷贝防止共享节点跨 paths 变异
        }
        // 先占位防循环:循环引用命中时消费方拿到此占位——描述标注"有意截断"
        _cache[underlying] = new JsonObject { ["type"] = "object", ["description"] = $"{TypeName(underlying)}(循环引用截断)" };

        var properties = new JsonObject();
        var required = new List<string>();
        // XML 注释键用泛型定义名(NexMesh.PagedResult`1)——构造泛型的 FullName 带程序集限定,无法直配
        var typeKey = underlying.IsGenericType
            ? underlying.GetGenericTypeDefinition().FullName
            : underlying.FullName;
        foreach (var prop in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propSchema = Of(prop.PropertyType, depth + 1);
            if (typeKey is not null
                && summaries.TryGetValue($"{typeKey}.{prop.Name}", out var propSummary))
            {
                propSchema["description"] = propSummary; // 字段中文注释(XML P: 注释)
            }
            properties[ToCamelCase(prop.Name)] = propSchema;
            // required:非可空值类型(引用类型的可空性运行时不可知,保守不标)
            if (prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null)
            {
                required.Add(ToCamelCase(prop.Name));
            }
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = typeKey is not null && summaries.TryGetValue(typeKey, out var typeSummary)
                ? typeSummary
                : TypeName(underlying),
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        }
        _cache[underlying] = schema;
        return DeepClone(schema);
    }

    /// <summary>类型默认示例值(Scalar 生成 curl --data 用):对象按属性递归;数组取单项。</summary>
    public JsonNode? Example(Type type, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string))
        {
            return "";
        }
        if (underlying == typeof(bool))
        {
            return false;
        }
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            return "2026-01-01T00:00:00Z";
        }
        if (underlying == typeof(Guid))
        {
            return "00000000-0000-0000-0000-000000000000";
        }
        if (underlying.IsEnum)
        {
            return Enum.GetNames(underlying).FirstOrDefault();
        }
        if (underlying.IsPrimitive || underlying == typeof(decimal))
        {
            return 0;
        }
        if (depth >= MaxDepth)
        {
            return new JsonObject();
        }
        if (underlying.IsArray || IsEnumerable(underlying))
        {
            // 空数组示例:体现"集合可能为空"(不再给 [示例值] 误导必有元素);字段名仍保留
            return new JsonArray();
        }
        if (GetDictionaryValueType(underlying) is { } dictValue)
        {
            return new JsonObject { ["示例键"] = Example(dictValue, depth + 1) };
        }
        var obj = new JsonObject();
        foreach (var prop in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            obj[ToCamelCase(prop.Name)] = Example(prop.PropertyType, depth + 1);
        }
        return obj;
    }

    private static string TypeName(Type type) =>
        type.IsGenericType
            ? $"{type.Name.Split('`')[0]}<{string.Join(",", type.GetGenericArguments().Select(a => TypeName(a)))}>"
            : type.Name;

    private static string ToCamelCase(string name) =>
        name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(char)
        || type == typeof(Half); // Half 非 IsPrimitive,漏收会掉对象分支按属性展开

    /// <summary>KeyValuePair 序列识别:IEnumerable&lt;KeyValuePair&lt;,&gt;&gt; 或 KeyValuePair&lt;,&gt;[](不含 IDictionary——已被上一分支接管)。</summary>
    private static bool IsKeyValuePairEnumerable(Type type) => GetKeyValuePairType(type) is not null;

    private static Type? GetKeyValuePairType(Type type)
    {
        if (type.IsArray && type.GetElementType() is { } arrayElement
            && arrayElement.IsGenericType
            && arrayElement.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return arrayElement;
        }
        if (!type.IsGenericType)
        {
            return null;
        }
        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && i.GetGenericArguments()[0] is { IsGenericType: true } element
                && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>字典接口值类型:实现 IDictionary&lt;,&gt;/IReadOnlyDictionary&lt;,&gt; 视为字典(含 Dictionary/ConcurrentDictionary 等)。</summary>
    private static Type? GetDictionaryValueType(Type type)
    {
        foreach (var i in type.GetInterfaces().Append(type))
        {
            if (!i.IsGenericType)
            {
                continue;
            }
            var def = i.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                return i.GetGenericArguments()[1];
            }
        }
        return null;
    }

    private static bool IsEnumerable(Type type) =>
        type.IsGenericType && type.GetInterfaces().Append(type).Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    private static Type GetElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType()!;
        }
        var enumerable = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? typeof(object);
    }

    private static JsonObject DeepClone(JsonObject source)
    {
        var clone = new JsonObject();
        foreach (var (key, value) in source)
        {
            clone[key] = value is JsonObject nested
                ? DeepClone(nested)
                : value?.DeepClone() is { } copy ? copy : null;
        }
        return clone;
    }
}
