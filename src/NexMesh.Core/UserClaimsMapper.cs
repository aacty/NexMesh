namespace NexMesh.Core;

using System.Reflection;
using System.Security.Claims;
using System.Text.Json;

/// <summary>
/// TUser ↔ Claims 约定映射:
/// 写入——Id/UserId→NameIdentifier、Name/UserName→Name、Email→Email、
/// Roles/Role→ClaimTypes.Role(逐条展开,授权策略 IsInRole 依赖此形式)、
/// 其余属性→[ClaimType] 或属性名小写;集合属性以 JSON 数组承载(单条声明)。
/// 读取——上述约定的逆变换(属性名忽略大小写匹配)。
/// </summary>
public static class UserClaimsMapper
{
    public static List<Claim> ToClaims<TUser>(TUser user)
    {
        var claims = new List<Claim>();
        if (user is null)
        {
            return claims;
        }

        foreach (var prop in typeof(TUser).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead)
            {
                continue;
            }
            var value = prop.GetValue(user);
            if (value is null)
            {
                continue;
            }
            AddPropertyClaims(claims, prop, value);
        }
        return claims;
    }

    public static TUser? FromClaims<TUser>(IEnumerable<Claim> claims) where TUser : class, new()
    {
        var list = claims as IReadOnlyCollection<Claim> ?? claims.ToList();
        if (list.Count == 0)
        {
            return null;
        }
        var user = new TUser();
        var byType = list.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var prop in typeof(TUser).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite)
            {
                continue;
            }
            var claimType = ClaimTypeOf(prop);
            if (!byType.TryGetValue(claimType, out var values))
            {
                continue;
            }
            SetPropertyValue(user, prop, values);
        }
        return user;
    }

    private static void AddPropertyClaims(List<Claim> claims, PropertyInfo prop, object value)
    {
        var claimType = ClaimTypeOf(prop);
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        if (IsStringList(type))
        {
            // 集合:JSON 数组单条声明(角色属名例外——Roles/Role 展开为 ClaimTypes.Role 多条,授权策略直接可用)
            var items = ((System.Collections.IEnumerable)value).Cast<object?>().ToList();
            var json = JsonSerializer.Serialize(items);
            if (claimType == ClaimTypes.Role)
            {
                foreach (var item in items)
                {
                    if (item?.ToString() is { Length: > 0 } role)
                    {
                        claims.Add(new Claim(ClaimTypes.Role, role));
                    }
                }
                return;
            }
            claims.Add(new Claim(claimType, json));
            return;
        }

        claims.Add(new Claim(claimType, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
    }

    private static void SetPropertyValue<TUser>(TUser user, PropertyInfo prop, List<string> values) where TUser : class, new()
    {
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        try
        {
            if (IsStringList(type))
            {
                // 角色声明(C.Role 多条)直接合并;其他集合从 JSON 数组解析
                var items = values.Count == 1 && values[0].StartsWith('[')
                    ? JsonSerializer.Deserialize<List<string>>(values[0])
                    : values;
                var listType = typeof(List<string>);
                var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                foreach (var item in items ?? [])
                {
                    list.Add(item);
                }
                prop.SetValue(user, list);
                return;
            }
            prop.SetValue(user, Convert.ChangeType(values[0], type, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or JsonException)
        {
            // 声明与属性类型不符:跳过该属性(容错降级,不阻断请求)
        }
    }

    private static string ClaimTypeOf(PropertyInfo prop)
    {
        if (prop.GetCustomAttribute<ClaimTypeAttribute>() is { } attr)
        {
            return attr.ClaimType;
        }
        return prop.Name.ToLowerInvariant() switch
        {
            "id" or "userid" => ClaimTypes.NameIdentifier,
            "name" or "username" => ClaimTypes.Name,
            "email" => ClaimTypes.Email,
            "roles" or "role" => ClaimTypes.Role,
            _ => prop.Name.ToLowerInvariant(),
        };
    }

    private static bool IsStringList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
        && type.GetGenericArguments()[0] == typeof(string);
}
