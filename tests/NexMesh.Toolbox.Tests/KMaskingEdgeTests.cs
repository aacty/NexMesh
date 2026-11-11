using NexMesh.Toolbox;

namespace NexMesh.Toolbox.Tests;

/// <summary>
/// K(第三轮交叉测试):脱敏边界。类文档契约:"仅处理常见形态,非规范输入原样返回"。
/// </summary>
public class KMaskingEdgeTests
{
    private static bool 含孤立代理项(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsSurrogate(s[i]))
            {
                continue;
            }
            if (i + 1 >= s.Length || !char.IsSurrogatePair(s[i], s[i + 1]))
            {
                return true;
            }
            i++; // 合法代理对成对跳过
        }
        return false;
    }

    // ── 手机号 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("13812345678", "138****5678")]       // 标准 11 位
    [InlineData("1381234567", "1381234567")]         // 10 位短号:非规范原样
    [InlineData("138123456789", "138123456789")]     // 12 位:非规范原样
    [InlineData(" 13812345678", " 13812345678")]     // 含前导空格:非规范原样
    [InlineData("+8613801234567", "+8613801234567")] // 带国码:非规范原样
    [InlineData("-3812345678", "-3812345678")]       // 负号开头可被 long.TryParse 通过,但不是手机号
    [InlineData("１３８１２３４５６７８", "１３８１２３４５６７８")] // 全角数字 11 位:非 ASCII 数字,不应按手机号脱敏
    [InlineData(null, null)]
    [InlineData("", "")]
    public void K_手机号_边界(string? input, string? expected) => Assert.Equal(expected, Masking.Phone(input));

    // ── 身份证 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("110101199001011234", "1101**********1234")]  // 18 位
    [InlineData("11010119900101123X", "1101**********123X")]  // 18 位 X 结尾
    [InlineData("110101001011234", "1101*******1234")]        // 15 位:7 星(星数 = 被遮位数)
    [InlineData("1234567890123456789", "1234567890123456789")] // 19 位超长:非规范原样
    [InlineData("123456789", "123456789")]                     // 9 位:原样
    [InlineData("abcdefghij", "abcdefghij")]                   // 10 位字母:非规范原样
    [InlineData(null, null)]
    [InlineData("", "")]
    public void K_身份证_边界(string? input, string? expected) => Assert.Equal(expected, Masking.IdCard(input));

    // ── 邮箱 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("alice@example.com", "a**@example.com")]
    [InlineData("ab@example.com", "a**@example.com")]  // 2 字符前缀也遮到 1 星 2 星
    [InlineData("a@x.com", "*@x.com")]                 // 单字符前缀
    [InlineData("@x.com", "@x.com")]                   // @ 开头:原样
    [InlineData("user@", "u**@")]                      // @ 结尾:仍遮蔽前缀
    [InlineData("张三@example.com", "张**@example.com")] // 中文前缀
    [InlineData("not-an-email", "not-an-email")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void K_邮箱_边界(string? input, string? expected) => Assert.Equal(expected, Masking.Email(input));

    [Fact]
    public void K_邮箱_Emoji首字符不产生孤立代理项()
    {
        var masked = Masking.Email("😀@x.com");   // emoji 按 1 个 code point 计:单字符前缀 → "*"
        Assert.Equal("*@x.com", masked);
        Assert.False(含孤立代理项(masked!));       // 修复实证:AsSpan(0,1) 曾劈开 emoji 代理对

        var multi = Masking.Email("😀💃@x.com");  // 多字符前缀:保留首 code point,遮蔽其余
        Assert.Equal("😀**@x.com", multi);
        Assert.False(含孤立代理项(multi!));
    }

    // ── 姓名 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("张三丰", "张**")]
    [InlineData("李四", "李*")]
    [InlineData("欧阳文德", "欧***")]
    [InlineData("乌兰其其格", "乌****")]
    [InlineData("张", "张")]  // 单字原样
    [InlineData(null, null)]
    [InlineData("", "")]
    public void K_姓名_边界(string? input, string? expected) => Assert.Equal(expected, Masking.Name(input));

    [Fact]
    public void K_姓名_非中文姓名串原样返回不损坏()
    {
        // emoji 不是汉字:整体原样返回(不劈代理对、不误打码)
        Assert.Equal("😀子", Masking.Name("😀子"));
        Assert.False(含孤立代理项(Masking.Name("😀子")!));
        // 拼音/编号等非中文姓名:原样返回
        Assert.Equal("Tom", Masking.Name("Tom"));
        Assert.Equal("C-1101", Masking.Name("C-1101"));
    }

    // ── 嵌套文本:契约钉住 ──────────────────────────────────────────

    [Fact]
    public void K_嵌套文本不处理_整串非规范输入原样返回()
    {
        const string text = "联系人是张三丰,电话13812345678,身份证110101199001011234";
        Assert.Equal(text, Masking.Name(text));
        Assert.Equal(text, Masking.Phone(text));
        Assert.Equal(text, Masking.IdCard(text));
        Assert.Equal(text, Masking.Email(text));
    }
}
