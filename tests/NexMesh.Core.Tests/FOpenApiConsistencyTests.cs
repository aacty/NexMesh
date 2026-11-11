namespace NexMesh.Core.Tests;

using System.Text.Json.Nodes;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Tests.Fixtures;
using Xunit.Sdk;

/// <summary>
/// OpenAPI 文档生成一致性(防漂移):schema 输出与绑定行为必须共用同一真值源
/// (AutoApiDispatcher.IsSimple / ContractSchemaGenerator.Simple)——枚举/可空/Guid/DateTime/集合参数逐项锚定。
/// </summary>
public class FOpenApiConsistencyTests
{
    /// <summary>文档构建一次(Test 程序集含全部测试夹具契约)。</summary>
    private static readonly Lazy<JsonObject> Document = new(() =>
        ContractOpenApiDocument.Build([typeof(DemoApiService).Assembly]));

    private static JsonObject Operation(string path, string verb)
        => Document.Value["paths"]![path]![verb]!.AsObject();

    private static JsonObject Parameter(string path, string verb, string name)
    {
        foreach (var node in Operation(path, verb)["parameters"]!.AsArray())
        {
            var p = node!.AsObject();
            if (p["name"]!.GetValue<string>() == name)
            {
                return p;
            }
        }
        throw new XunitException($"参数 {name} 未在 {verb} {path} 的 OpenAPI 文档中登记(参数静默消失)");
    }

    [Fact]
    public void 枚举参数_schema为字符串枚举名()
    {
        var p = Parameter("/api/FuzzQuery/Level", "post", "day");
        Assert.Equal("query", p["in"]!.GetValue<string>());
        var schema = p["schema"]!.AsObject();
        Assert.Equal("string", schema["type"]!.GetValue<string>());
        Assert.Equal(Enum.GetNames(typeof(DayOfWeek)),
            schema["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.False(schema.ContainsKey("nullable"), "非可空枚举不应标 nullable");
    }

    [Fact]
    public void 可空Long_schema整型64位_nullable()
    {
        var p = Parameter("/api/FuzzQuery/Zip", "post", "zip");
        Assert.Equal("query", p["in"]!.GetValue<string>());
        var schema = p["schema"]!.AsObject();
        Assert.Equal("integer", schema["type"]!.GetValue<string>());
        Assert.Equal("int64", schema["format"]!.GetValue<string>());
        Assert.True(schema["nullable"]!.GetValue<bool>(), "long? 应标 nullable");
    }

    [Fact]
    public void Guid_DateTime_decimal_格式与绑定一致()
    {
        var guid = Parameter("/api/FuzzQuery/Code", "post", "code")["schema"]!.AsObject();
        Assert.Equal("string", guid["type"]!.GetValue<string>());
        Assert.Equal("uuid", guid["format"]!.GetValue<string>());

        var when = Parameter("/api/FuzzQuery/When", "post", "when")["schema"]!.AsObject();
        Assert.Equal("string", when["type"]!.GetValue<string>());
        Assert.Equal("date-time", when["format"]!.GetValue<string>());

        var price = Parameter("/api/FuzzQuery/Price", "post", "price")["schema"]!.AsObject();
        Assert.Equal("number", price["type"]!.GetValue<string>());
        Assert.Equal("double", price["format"]!.GetValue<string>());
    }

    [Fact]
    public void 默认值参数_required_false_无默认_required_true()
    {
        Assert.False(Parameter("/api/FuzzQuery/DefaultQty", "post", "qty")["required"]!.GetValue<bool>());
        Assert.True(Parameter("/api/FuzzQuery/Get", "post", "id")["required"]!.GetValue<bool>());
    }

    [Fact]
    public void 路由约束long_映射int64_schema()
    {
        var p = Parameter("/api/FuzzQuery/{id:long}", "get", "id");
        Assert.Equal("path", p["in"]!.GetValue<string>());
        Assert.True(p["required"]!.GetValue<bool>());
        var schema = p["schema"]!.AsObject();
        Assert.Equal("integer", schema["type"]!.GetValue<string>());
        Assert.Equal("int64", schema["format"]!.GetValue<string>());
    }

    [Fact]
    public void 无约束路由参数_按string_已知P3漂移锁定现状()
    {
        // 已知漂移(P3 记录):/api/Crud/{id} 的 CLR 参数是 long(绑定按 long 转换,"abc" 400),
        // 但文档在无路由约束时输出 string —— 锁定现状,修复(CLR 类型优先)后应改断言 int64
        var schema = Parameter("/api/Crud/{id}", "get", "id")["schema"]!.AsObject();
        Assert.Equal("string", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void 简单类型参数_文档登记完整性()
    {
        // IsSimple 真值源锚定:凡绑定按 Query 简单类型处理的参数,文档必须逐个登记(防"参数静默消失"回归)
        (string Path, string Verb, string Param)[] expected =
        [
            ("/api/FuzzQuery/Get", "post", "id"),
            ("/api/FuzzQuery/Price", "post", "price"),
            ("/api/FuzzQuery/Rate", "post", "rate"),
            ("/api/FuzzQuery/Flag", "post", "flag"),
            ("/api/FuzzQuery/When", "post", "when"),
            ("/api/FuzzQuery/Code", "post", "code"),
            ("/api/FuzzQuery/Zip", "post", "zip"),
            ("/api/FuzzQuery/Level", "post", "day"),
            ("/api/FuzzQuery/Initial", "post", "initial"),
            ("/api/FuzzQuery/DefaultQty", "post", "qty"),
            ("/api/FuzzQuery/Name", "post", "name"),
        ];
        Assert.All(expected, e =>
        {
            var p = Parameter(e.Path, e.Verb, e.Param);
            Assert.Equal("query", p["in"]!.GetValue<string>());
            Assert.NotNull(p["schema"]!["type"]);
        });
    }

    [Fact]
    public void 简单集合参数_期望登记query数组参数且无伪造requestBody()
    {
        // 期望(与绑定一致):ids 按 Query 数组登记,且不得伪造 requestBody
        var op = Operation("/api/FuzzCollection/Sum", "post");
        Assert.Null(op["requestBody"]);

        var p = Parameter("/api/FuzzCollection/Sum", "post", "ids");
        Assert.Equal("query", p["in"]!.GetValue<string>());
        var schema = p["schema"]!.AsObject();
        Assert.Equal("array", schema["type"]!.GetValue<string>());
        Assert.Equal("integer", schema["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void 成功信封data_schema_与返回类型一致()
    {
        // Task<int> → data:integer;Task<List<int>> → data:array(integer)
        var dataOfGet = Operation("/api/Demo/Get", "post")["responses"]!["200"]!["content"]!
            ["application/json"]!["schema"]!.AsObject()["properties"]!.AsObject()["data"]!.AsObject();
        Assert.Equal("integer", dataOfGet["type"]!.GetValue<string>());

        var dataOfList = Operation("/api/Demo/GetList", "post")["responses"]!["200"]!["content"]!
            ["application/json"]!["schema"]!.AsObject()["properties"]!.AsObject()["data"]!.AsObject();
        Assert.Equal("array", dataOfList["type"]!.GetValue<string>());
        Assert.Equal("integer", dataOfList["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void 生成器与IsSimple判定_单元级一致()
    {
        // 唯一真值源语义:凡 IsSimple=true 的类型,生成器必须给出带 type 的简单 schema(而非对象/数组分支);
        // 凡 IsSimple=false 的集合/复杂类型,生成器按 array/object 展开
        var generator = new ContractSchemaGenerator([]);
        Type[] simpleTypes =
        [
            typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte),
            typeof(ushort), typeof(uint), typeof(ulong), typeof(nint), typeof(nuint),
            typeof(float), typeof(double), typeof(decimal), typeof(bool),
            typeof(char), typeof(string), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset),
            typeof(DayOfWeek),
            typeof(int?), typeof(Guid?), typeof(char?), typeof(DayOfWeek?),
        ];
        Assert.All(simpleTypes, t =>
        {
            Assert.True(AutoApiDispatcher.IsSimple(t), $"{t.Name} 应为 IsSimple");
            Assert.NotNull(generator.Of(t, 0)["type"]);
        });

        Type[] collectionTypes = [typeof(int[]), typeof(List<string>), typeof(IEnumerable<int>)];
        Assert.All(collectionTypes, t =>
        {
            Assert.False(AutoApiDispatcher.IsSimple(t), $"{t.Name} 不应为 IsSimple");
            Assert.Equal("array", generator.Of(t, 0)["type"]!.GetValue<string>());
        });
        Assert.Equal("object", generator.Of(typeof(FuzzEchoDto), 0)["type"]!.GetValue<string>());
    }
}
