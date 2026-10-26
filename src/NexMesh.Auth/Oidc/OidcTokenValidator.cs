namespace NexMesh.Auth;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// 外部 IdP JWT 验证器(零外部依赖):HS256(HMAC)与 RS256(RSA)签名验证 + exp/nbf/iss/aud 声明校验。
/// 配置 NexMesh:Authentication:Oidc { Issuer, Audience, Secret(对称) 或 RsaPublicKeyPem(非对称), ClockSkewSeconds }。
/// 验证通过产出 LoginUser(经 token-exchange 换发本地不透明令牌,滑动续期/吊销语义完整保留)。
/// 在线 JWKS 发现/密钥轮换为后续版本;当前为静态配置密钥模式。
/// </summary>
public sealed class OidcTokenValidator
{
    private readonly OidcOptions _options;
    private IReadOnlyDictionary<string, RSA>? _jwksKeys;

    public OidcTokenValidator(Microsoft.Extensions.Options.IOptions<AuthenticationOptions> options)
    {
        _options = options.Value.Oidc ?? new OidcOptions();
    }

    /// <summary>注入 JWKS 公钥集(密钥轮换:重新拉取后整体替换;kid→RSA)。null=用静态 RsaPublicKeyPem。</summary>
    public void SetJwksKeys(IReadOnlyDictionary<string, RSA>? keys) => _jwksKeys = keys;

    /// <summary>启用条件:Issuer + (HS256 密钥≥32 字节 / 静态 RSA 公钥 / 已注入 JWKS 公钥集)。
    /// 弱对称密钥(可被字典攻击)不作为有效凭据;纯 JWKS 模式(Issuer+SetJwksKeys)同样启用。</summary>
    public bool Enabled => _options is { Issuer.Length: > 0 }
        && (IsStrongSecret() || !string.IsNullOrEmpty(_options.RsaPublicKeyPem) || _jwksKeys is { Count: > 0 });

    private bool IsStrongSecret()
        => !string.IsNullOrEmpty(_options.Secret) && System.Text.Encoding.UTF8.GetByteCount(_options.Secret) >= 32;

    /// <summary>验证 IdP JWT;通过返回映射后的 LoginUser(sub/preferred_username/tenantid/roles),失败返回 null。</summary>
    public LoginUser? Validate(string jwt)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        // 非法 base64url(模糊输入)一律拒绝而非抛出
        if (TryBase64UrlDecode(parts[0]) is not { } headerBytes || TryBase64UrlDecode(parts[1]) is not { } payloadBytes
            || TryBase64UrlDecode(parts[2]) is not { } signature)
        {
            return null;
        }
        var header = ParseJson(headerBytes);
        var payload = ParseJson(payloadBytes);
        if (header is null || payload is null)
        {
            return null;
        }

        // 签名验证(HS256/RS256;alg 不认识=拒)
        var alg = header.TryGetValue("alg", out var a) && a is JsonElement je ? je.GetString() : null;
        var signedContent = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        if (alg == "HS256")
        {
            if (string.IsNullOrEmpty(_options.Secret))
            {
                return null;
            }
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.Secret), signedContent);
            if (!CryptographicOperations.FixedTimeEquals(expected, signature))
            {
                return null;
            }
        }
        else if (alg == "RS256")
        {
            // 优先 JWKS(按 header.kid 匹配,支持密钥轮换);无 JWKS 回退静态 PEM
            var keyId = header.TryGetValue("kid", out var kidEl) && kidEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? kidEl.GetString() : null;
            RSA? rsa = null;
            var owned = false;
            if (_jwksKeys is { Count: > 0 } && keyId is not null && _jwksKeys.TryGetValue(keyId, out var jwks))
            {
                rsa = jwks;
            }
            else if (!string.IsNullOrEmpty(_options.RsaPublicKeyPem))
            {
                rsa = RSA.Create();
                owned = true;
                try
                {
                    rsa.ImportFromPem(_options.RsaPublicKeyPem);
                }
                catch (ArgumentException)
                {
                    rsa.Dispose();
                    return null; // 公钥配置非法
                }
            }
            if (rsa is null)
            {
                return null;
            }
            try
            {
                if (!rsa.VerifyData(signedContent, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    return null;
                }
            }
            finally
            {
                if (owned)
                {
                    rsa.Dispose();
                }
            }
        }
        else
        {
            return null;
        }

        // 声明校验:exp/nbf(带时钟偏移容忍)、iss、aud
        var skew = TimeSpan.FromSeconds(Math.Max(_options.ClockSkewSeconds, 0));
        var now = DateTimeOffset.UtcNow;
        if (payload.TryGetValue("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number
            && SafeFromUnix(expEl.GetInt64()) is { } exp)
        {
            if (now - skew > exp) // 减法方向:exp 为 MaxValue 时加法(skew+)会溢出
            {
                return null;
            }
        }
        else
        {
            return null; // 无 exp(或非法数值)视为无效——不允许永生令牌
        }
        if (payload.TryGetValue("nbf", out var nbfEl) && nbfEl.ValueKind == JsonValueKind.Number
            && SafeFromUnix(nbfEl.GetInt64()) is { } nbf && nbf - skew > now) // nbf 为 MaxValue 时 now+skew 会溢出
        {
            return null;
        }
        if (!string.IsNullOrEmpty(_options.Issuer)
            && (!payload.TryGetValue("iss", out var issEl) || issEl.GetString() != _options.Issuer))
        {
            return null;
        }
        if (!string.IsNullOrEmpty(_options.Audience)
            && (!payload.TryGetValue("aud", out var audEl)
                || (audEl.ValueKind == JsonValueKind.String && audEl.GetString() != _options.Audience)
                || (audEl.ValueKind == JsonValueKind.Array && !audEl.EnumerateArray().Any(x => x.GetString() == _options.Audience))))
        {
            return null;
        }

        // 映射 LoginUser:sub(必需)/preferred_username→UserName/tenantid/roles→ClaimList(Role)
        if (!payload.TryGetValue("sub", out var subEl) || subEl.GetString() is not { Length: > 0 } sub)
        {
            return null;
        }
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.TryGetValue("tenantid", out var tEl) && tEl.GetString() is { Length: > 0 } tenant)
        {
            claims["tenantid"] = tenant;
        }
        var claimList = new List<TokenClaim>();
        if (payload.TryGetValue("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
        {
            claimList.AddRange(rolesEl.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => new TokenClaim(ClaimTypesRole, r.GetString()!)));
        }
        return new LoginUser(sub, GetString("preferred_username"), GetString("tenantid"), claims.Count > 0 ? claims : null)
        {
            ClaimList = claimList.Count > 0 ? claimList : null,
        };

        string? GetString(string name)
            => payload.TryGetValue(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>越界时间戳安全解析:超出 DateTimeOffset 范围按"极远未来/过去"处理,不抛异常。</summary>
    private static DateTimeOffset? SafeFromUnix(long seconds) =>
        seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() ? DateTimeOffset.MinValue
        : seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.MaxValue
        : DateTimeOffset.FromUnixTimeSeconds(seconds);

    private const string ClaimTypesRole = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    private static Dictionary<string, JsonElement>? ParseJson(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? TryBase64UrlDecode(string value)
    {
        try
        {
            var padding = (4 - value.Length % 4) % 4;
            if (padding == 3)
            {
                return null; // 非法长度(单字符段)
            }
            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>OIDC RP 配置(NexMesh:Authentication:Oidc;非空 Issuer+密钥即启用)。</summary>
public sealed class OidcOptions
{
    /// <summary>IdP 签发方标识(iss 校验);配置即启用 OIDC 验证。</summary>
    public string Issuer { get; set; } = "";

    /// <summary>受众(aud 校验);空则不校验。</summary>
    public string Audience { get; set; } = "";

    /// <summary>HS256 对称密钥(与 RsaPublicKeyPem 二选一)。</summary>
    public string Secret { get; set; } = "";

    /// <summary>RS256 公钥(PEM;与 Secret 二选一)。</summary>
    public string RsaPublicKeyPem { get; set; } = "";

    /// <summary>时钟偏移容忍(秒)。</summary>
    public int ClockSkewSeconds { get; set; } = 60;
}


/// <summary>
/// JWKS 在线发现:从 IdP 的 {Issuer}/.well-known/openid-configuration → jwks_uri 拉取公钥集,
/// 按 kid 构建 RSA 字典(OIDC JWKS 标准:modulus(n)/exponent(e) Base64Url)。
/// 调用方(业务启动或定时轮换)拉取后 OidcTokenValidator.SetJwksKeys 整体替换——原子轮换。
/// </summary>
public static class OidcJwksFetcher
{
    /// <summary>拉取并解析 JWKS;失败抛出(调用方决定重试/降级静态 PEM)。</summary>
    public static async Task<IReadOnlyDictionary<string, RSA>> FetchAsync(
        string issuer, System.Net.Http.HttpClient? http = null, CancellationToken ct = default)
    {
        var owned = http is null;
        if (http is null)
        {
            http = new System.Net.Http.HttpClient();
        }
        try
        {
            var discoveryUrl = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
            using var discovery = await http.GetAsync(discoveryUrl, ct);
            discovery.EnsureSuccessStatusCode();
            using var discoveryDoc = System.Text.Json.JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(ct));
            var jwksUri = discoveryDoc.RootElement.GetProperty("jwks_uri").GetString()
                ?? throw new InvalidOperationException("发现文档缺 jwks_uri");

            using var jwks = await http.GetAsync(jwksUri, ct);
            jwks.EnsureSuccessStatusCode();
            using var jwksDoc = System.Text.Json.JsonDocument.Parse(await jwks.Content.ReadAsStringAsync(ct));
            var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
            foreach (var key in jwksDoc.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (!key.TryGetProperty("kty", out var kty) || kty.GetString() != "RSA"
                    || !key.TryGetProperty("n", out var nEl) || !key.TryGetProperty("e", out var eEl))
                {
                    continue;
                }
                var kid = key.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;
                if (string.IsNullOrEmpty(kid))
                {
                    continue;
                }
                var rsa = RSA.Create();
                try
                {
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = Base64UrlDecode(nEl.GetString()),
                        Exponent = Base64UrlDecode(eEl.GetString()),
                    });
                    keys[kid!] = rsa;
                }
                catch (FormatException)
                {
                    rsa.Dispose(); // 非法参数的键跳过
                }
            }
            return keys;
        }
        finally
        {
            if (owned)
            {
                http?.Dispose();
            }
        }
    }

    private static byte[] Base64UrlDecode(string? value)
    {
        var padding = (4 - (value?.Length ?? 0) % 4) % 4;
        return Convert.FromBase64String((value ?? "").Replace('-', '+').Replace('_', '/') + new string('=', padding));
    }
}
