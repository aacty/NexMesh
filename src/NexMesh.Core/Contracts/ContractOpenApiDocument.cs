namespace NexMesh;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

/// <summary>
/// 契约端点 OpenAPI 文档生成器:反射式 RequestDelegate 端点不被 .NET OpenAPI 生成器自动收录,
/// 由此直接产出文档 JSON(Scalar/Swagger UI 消费)——path+method+tags+summary(取程序集 XML 注释)
/// +path/query 参数+JSON 请求体占位+响应描述。
/// </summary>
public static class ContractOpenApiDocument
{
    /// <summary>生成 OpenAPI 3.1 文档(JSON DOM);title/version 可从 NexMesh:Scalar:Document 读取。</summary>
    public static JsonObject Build(Assembly[] assemblies, string? title = null, string? version = null)
    {
        var summaries = LoadXmlSummaries(assemblies);
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
                    ["schema"] = SimpleSchema(param.ParameterType),
                });
            }
            if (parameters.Count > 0)
            {
                operation["parameters"] = parameters;
            }

            if (endpoint.BodyParam is { } body)
            {
                operation["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = body.ParameterType.Name,
                            },
                        },
                    },
                };
            }

            operation["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = endpoint.DeclaredResult is null
                        ? "成功(空载荷信封 success/code=0)"
                        : $"成功(信封 data: {endpoint.DeclaredResult.PropertyType.Name})",
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

    private static JsonObject SimpleSchema(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var schemaType = underlying == typeof(string) || underlying.IsEnum
            || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
            || underlying == typeof(Guid) || underlying == typeof(char)
            ? "string"
            : underlying == typeof(bool) ? "boolean"
            : "number";
        return underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
            ? new JsonObject { ["type"] = schemaType, ["format"] = "date-time" }
            : new JsonObject { ["type"] = schemaType };
    }

    /// <summary>加载程序集 XML 注释:M:全限定.方法名 → summary;T:全限定 → 类型 summary。</summary>
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
                    name = name[2..]; // 去 M:/T:/P: 前缀,与 FullName/MethodId 对齐
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
