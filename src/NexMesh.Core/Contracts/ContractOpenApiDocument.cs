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
    public static JsonObject Build(Assembly[] assemblies, string? title = null, string? version = null)
    {
        var summaries = LoadXmlSummaries(assemblies);
        var schemaGen = new ContractSchemaGenerator(summaries);
        var paths = new JsonObject();
        var tags = new JsonObject();

        foreach (var endpoint in ContractApiExtensions.ScanContractEndpoints(assemblies))
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

            // 响应:信封 schema(success/code/message/data{字段级}/traceId/timestamp)+ 示例
            var responseSchema = endpoint.DeclaredResult is { } result
                ? EnvelopeSchema(schemaGen.Of(result.PropertyType, 0))
                : EnvelopeSchema(null);
            operation["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = endpoint.DeclaredResult is null
                        ? "成功(空载荷信封)"
                        : "成功(ApiResult 信封:data 为业务载荷)",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = responseSchema,
                        },
                    },
                },
            };

            var pathKey = "/" + endpoint.Route;
            var verb = endpoint.Verb.ToLowerInvariant();
            var pathItem = paths[pathKey] as JsonObject ?? new JsonObject();
            pathItem[verb] = operation;
            paths[pathKey] = pathItem;

            if (!tags.ContainsKey(tagName)
                && endpoint.ServiceType.FullName is { Length: > 0 } typeName
                && summaries.TryGetValue(typeName, out var typeSummary))
            {
                tags[tagName] = typeSummary;
            }
        }

        return new JsonObject
        {
            ["openapi"] = "3.2.0",
            ["info"] = new JsonObject
            {
                ["title"] = string.IsNullOrWhiteSpace(title) ? "NexMesh API" : title,
                ["version"] = string.IsNullOrWhiteSpace(version) ? "v1" : version,
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
                : new JsonObject { ["type"] = "null", ["nullable"] = true },
            ["traceId"] = new JsonObject { ["type"] = "string", ["nullable"] = true },
            ["timestamp"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
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
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time", ["nullable"] = Nullable.GetUnderlyingType(type) is not null };
        }
        if (underlying == typeof(Guid))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        }
        if (underlying.IsEnum)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames(underlying).Select(n => (JsonNode)n!).ToArray()),
            };
        }
        var node = new JsonObject
        {
            ["type"] = underlying == typeof(bool) ? "boolean"
                : underlying == typeof(string) || underlying == typeof(char) ? "string"
                : "integer",
        };
        if (Nullable.GetUnderlyingType(type) is not null || !type.IsValueType)
        {
            node["nullable"] = true;
        }
        return node;
    }

    /// <summary>CLR 类型 → schema:集合→array;复杂对象→属性递归(字段中文注释);基元/枚举/日期→简单 schema。</summary>
    public JsonObject Of(Type type, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (IsSimple(underlying))
        {
            return Simple(type);
        }
        if (depth >= MaxDepth)
        {
            return new JsonObject { ["type"] = "object", ["description"] = TypeName(underlying) };
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
        if (underlying.IsEnum)
        {
            return Simple(underlying);
        }
        if (underlying == typeof(object) || underlying == typeof(System.Text.Json.JsonElement))
        {
            return new JsonObject { ["description"] = underlying == typeof(object) ? "任意结构" : "JSON 结构" };
        }
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = Of(underlying.GetGenericArguments()[1], depth + 1),
                ["description"] = "字典",
            };
        }

        if (_cache.TryGetValue(underlying, out var cached))
        {
            return cached is JsonObject shallow ? DeepClone(shallow) : cached; // 深拷贝防止共享节点跨 paths 变异
        }
        _cache[underlying] = new JsonObject { ["type"] = "object", ["description"] = TypeName(underlying) }; // 先占位防循环

        var properties = new JsonObject();
        foreach (var prop in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propSchema = Of(prop.PropertyType, depth + 1);
            if (summaries.TryGetValue($"{underlying.FullName}.{prop.Name}", out var propSummary))
            {
                propSchema["description"] = propSummary; // 字段中文注释(XML P: 注释)
            }
            properties[ToCamelCase(prop.Name)] = propSchema;
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = summaries.TryGetValue(underlying.FullName ?? "", out var typeSummary)
                ? typeSummary
                : TypeName(underlying),
            ["properties"] = properties,
        };
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
            var element = GetElementType(underlying);
            return IsSimple(element)
                ? new JsonArray(Example(element, depth + 1))
                : new JsonArray(Example(element, depth + 1));
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
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(char);

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
