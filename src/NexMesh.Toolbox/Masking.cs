namespace NexMesh.Toolbox;

/// <summary>数据脱敏(展示/日志场景):手机/身份证/邮箱/姓名。仅处理常见形态,非规范输入原样返回。</summary>
public static class Masking
{
    public static string? Phone(string? phone) =>
        phone is { Length: 11 } && IsAsciiDigits(phone)
            ? string.Concat(phone.AsSpan(0, 3), "****", phone.AsSpan(7))
            : phone;

    public static string? IdCard(string? id) =>
        (id is { Length: 15 } && IsAsciiDigits(id)) ||
        (id is { Length: 18 } && IsAsciiDigits(id[..17]) && char.IsAsciiLetterOrDigit(id[17]))
            ? string.Concat(id.AsSpan(0, 4), new string('*', id.Length - 8), id.AsSpan(id.Length - 4))
            : id;

    public static string? Email(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return email;
        }
        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return email; // 无@或@开头:原样返回
        }
        // 首字符按完整 code point 取:emoji 等增补平面字符占 2 个 char,不能劈开代理对
        var firstLen = char.IsHighSurrogate(email[0]) && email.Length > 1 && char.IsLowSurrogate(email[1]) ? 2 : 1;
        var prefix = at == firstLen ? "*" : string.Concat(email.AsSpan(0, firstLen), "**");
        return prefix + email.AsSpan(at).ToString();
    }

    /// <summary>中文姓名:首字保留其余打星。仅处理 2~6 个汉字(含间隔号·)的连续串——
    /// 嵌套文本/编号等非姓名串原样返回,避免长文本被整段打星毁掉。</summary>
    public static string? Name(string? name) =>
        name is { Length: >= 2 and <= 6 } && LooksLikeChineseName(name)
            ? string.Concat(name.AsSpan(0, 1), new string('*', name.Length - 1))
            : name;

    private static bool IsAsciiDigits(string s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static bool LooksLikeChineseName(string s)
    {
        foreach (var c in s)
        {
            if (c is not ((>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF') or '·'))
            {
                return false;
            }
        }
        return true;
    }
}
