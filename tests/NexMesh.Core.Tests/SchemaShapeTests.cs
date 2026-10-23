namespace NexMesh.Core.Tests;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NexMesh;

/// <summary>
/// 字典六种声明形式的 schema 行为固化(二轮 code review 结论的机器断言):
/// Dictionary/IDictionary/IReadOnlyDictionary/ConcurrentDictionary/IEnumerable&lt;KeyValuePair&lt;,&gt;&gt;/KeyValuePair&lt;,&gt;[] 
/// → 全部收敛为 map schema(additionalProperties);KeyValuePair 本身 → key/value 对象。
/// 附:数值分型/required/空集合示例/DeepClone 独立性/路由约束映射。
/// </summary>
public class SchemaShapeTests
{
    private static ContractSchemaGenerator Gen() => new([]);

    private static JsonObject Of<T>() => Gen().Of(typeof(T), 0);

    [Theory]
    [InlineData(typeof(Dictionary<long, List<string>>))]
    [InlineData(typeof(IDictionary<long, string>))]
    [InlineData(typeof(IReadOnlyDictionary<long, string>))]
    [InlineData(typeof(ConcurrentDictionary<long, string>))]
    [InlineData(typeof(IEnumerable<KeyValuePair<long, string>>))]
    [InlineData(typeof(KeyValuePair<long, string>[]))]
    public void 六种字典声明_全部收敛为map_schema(Type type)
    {
        var schema = Gen().Of(type, 0);

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        var additional = schema["additionalProperties"] as JsonObject;
        Assert.NotNull(additional);
    }

    [Fact]
    public void Dictionary值类型为集合_additionalProperties为数组()
    {
        var schema = Of<Dictionary<long, List<string>>>();

        Assert.Equal("array", schema["additionalProperties"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void KeyValuePair本身_展开为key_value对象()
    {
        var schema = Of<KeyValuePair<long, string>>();

        var props = schema["properties"] as JsonObject;
        Assert.NotNull(props);
        Assert.Contains("key", props.Select(p => p.Key));
        Assert.Contains("value", props.Select(p => p.Key));
        Assert.Equal("integer", props["key"]!["type"]!.GetValue<string>());
        Assert.Equal("string", props["value"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(typeof(float), "number", "float")]
    [InlineData(typeof(double), "number", "double")]
    [InlineData(typeof(decimal), "number", "double")]
    [InlineData(typeof(long), "integer", "int64")]
    [InlineData(typeof(ulong), "integer", "int64")]
    [InlineData(typeof(uint), "integer", "int32")]
    [InlineData(typeof(nint), "integer", "int64")]
    public void 数值精确分型(Type type, string expectedType, string expectedFormat)
    {
        var schema = Gen().Simple(type);

        Assert.Equal(expectedType, schema["type"]!.GetValue<string>());
        Assert.Equal(expectedFormat, schema["format"]!.GetValue<string>());
    }

    [Fact]
    public void uint与ulong_minimum为0防溢出误读()
    {
        Assert.Equal(0, Gen().Simple(typeof(uint))["minimum"]!.GetValue<int>());
        Assert.Equal(0, Gen().Simple(typeof(ulong))["minimum"]!.GetValue<int>());
        Assert.Null(Gen().Simple(typeof(int))["minimum"]);
    }

    [Fact]
    public void Half_绑定不认_schema走对象分支_取舍A行为固化()
    {
        // 取舍 A:绑定规则为唯一真值源——Half 无 TypeDescriptor 转换器,绑定不认(不进 query),
        // schema 跟随走对象分支(Half 无公共属性 → 空对象),文档与绑定行为严格一致
        Assert.False(AutoApi.AutoApiDispatcher.IsSimple(typeof(Half)));

        var schema = Gen().Of(typeof(Half), 0);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void 非可空值类型属性进required_引用类型不进()
    {
        var schema = Of<RequiredSample>();

        var required = schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Contains("id", required);
        Assert.Contains("count", required);
        Assert.Contains("flag", required);
        Assert.DoesNotContain("name", required);
        Assert.DoesNotContain("remark", required);
    }

    [Fact]
    public void 集合示例为空数组_不输出误导性单元素()
    {
        var example = Gen().Example(typeof(RequiredSample), 0) as JsonObject;

        Assert.NotNull(example);
        var tags = example!["tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Empty(tags!);
    }

    [Fact]
    public void 同一类型两次生成_节点独立_修改一处不影响另处()
    {
        var first = Of<NestedSample>();
        var second = Of<NestedSample>();

        (first["properties"]!["items"] as JsonObject)!["description"] = "MUTATED";

        Assert.NotEqual("MUTATED",
            (second["properties"]!["items"] as JsonObject)!["description"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(typeof(char))]
    [InlineData(typeof(Half))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(DayOfWeek))] // 枚举
    [InlineData(typeof(string))]
    [InlineData(typeof(Dictionary<long, string>))] // 复杂:map
    [InlineData(typeof(List<long>))] // 复杂:数组
    [InlineData(typeof(KeyValuePair<long, string>))] // 复杂:key/value 对象
    public void 边界类型_绑定判定与schema形态同源一致(Type type)
    {
        var isSimple = AutoApi.AutoApiDispatcher.IsSimple(type);
        var schema = Gen().Of(type, 0);

        // 简单类型 ⟺ schema 为简单形态(string/integer/number/boolean)——
        // 判定与 schema 共用单一 IsSimple 实现,两边永远一致;此断言防再次分叉回退
        var isSimpleForm = schema["type"]?.GetValue<string>() is "string" or "integer" or "number" or "boolean";
        Assert.Equal(isSimple, isSimpleForm);
    }

    private sealed class RequiredSample
    {
        public long Id { get; set; }
        public int Count { get; set; }
        public bool Flag { get; set; }
        public string Name { get; set; } = "";
        public string? Remark { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    private sealed class NestedItem
    {
        public long Id { get; set; }
    }

    private sealed class NestedSample
    {
        public List<NestedItem> Items { get; set; } = [];
    }
}
