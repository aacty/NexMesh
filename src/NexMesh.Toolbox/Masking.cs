namespace NexMesh.Toolbox;

/// <summary>数据脱敏(展示/日志场景):手机/身份证/邮箱/姓名。仅处理常见形态,非规范输入原样返回。</summary>
public static class Masking
{
    public static string? Phone(string? phone) =>
        phone is { Length: 11 } && long.TryParse(phone, out _)
            ? string.Concat(phone.AsSpan(0, 3), "****", phone.AsSpan(7))
            : phone;

    public static string? IdCard(string? id) =>
        id is { Length: >= 10 }
            ? string.Concat(id.AsSpan(0, 4), "**********", id.AsSpan(id.Length - 4))
            : id;

    public static string? Email(string? email)
    {
        var at = email?.IndexOf('@') ?? -1;
        if (at <= 0)
        {
            return email; // 空或无@或@开头:原样返回
        }
        var prefix = at == 1 ? "*" : string.Concat(email!.AsSpan(0, 1), "**");
        return prefix + email!.AsSpan(at).ToString();
    }

    /// <summary>中文姓名:首字保留其余打星(两字名打一星,多字全星)。</summary>
    public static string? Name(string? name) =>
        name is { Length: >= 2 }
            ? string.Concat(name.AsSpan(0, 1), new string('*', name.Length - 1))
            : name;
}
