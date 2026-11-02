namespace NexMesh.Auth.Tests;

using NexMesh.Auth.Oidc;

/// <summary>
/// IdP 令牌服务安全语义(SSS+):授权码 90 秒过期(时钟可注入)/并发兑换同一码恰好一人成功/
/// 常量时间 secret 比较的正确性面(错凭据拒绝由端点测试覆盖)。
/// </summary>
public class OidcIdpTokenServiceTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (OidcIdpTokenService Service, FixedTimeProvider Clock) Build()
    {
        var clock = new FixedTimeProvider();
        return (new OidcIdpTokenService(new OidcIdpOptions { Issuer = "http://localhost" }, clock), clock);
    }

    [Fact]
    public void 授权码超90秒_兑换拒绝_有效期内成功()
    {
        var (service, clock) = Build();
        const string verifier = "correct-verifier-01234567890123456789012345678901";
        // challenge 形态 = 标准 S256:base64url(SHA256(ASCII(verifier))),与兑换侧 B64Url 比较一致
        var challenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var code = service.IssueAuthorizationCode("rp-app", "http://localhost/cb", challenge, "u1");

        Assert.Equal("u1", service.RedeemAuthorizationCode(code, verifier, "rp-app")); // 有效期内 + PKCE 匹配 → 成功

        var (service2, clock2) = Build();
        var code2 = service2.IssueAuthorizationCode("rp-app", "http://localhost/cb", challenge, "u1");
        clock2.Now = clock2.Now.AddSeconds(91); // 拨过 90 秒有效期
        Assert.Null(service2.RedeemAuthorizationCode(code2, verifier, "rp-app")); // 过期:即便 PKCE 匹配也拒绝
    }

    [Fact]
    public async Task 并发兑换同一授权码_恰好一人成功_其余判负()
    {
        var (service, _) = Build();
        const string verifier = "correct-verifier-01234567890123456789012345678901";
        var challenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'); // 标准 S256 base64url(与兑换侧比较一致)
        var code = service.IssueAuthorizationCode("rp-app", "http://localhost/cb", challenge, "u1");

        var winners = 0;
        await Parallel.ForAsync(0, 16, async (_, _) =>
        {
            if (service.RedeemAuthorizationCode(code, verifier, "rp-app") is not null)
            {
                Interlocked.Increment(ref winners); // 一次性:TryRemove 原子,并发下恰好一人
            }
            await Task.CompletedTask;
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public async Task 并发轮换密钥_最新kid与导出JWKS一致_签名可验()
    {
        // 轮换与签发交错后锁定不变量:导出的 JWKS 公钥 = 当前密钥;最新令牌的 header.kid 与之一致且验签通过
        var (service, _) = Build();
        var user = new OidcIdpUser("u1", "门户用户", "tenant-x", ["manager"]);

        await Parallel.ForAsync(0, 8, async (_, _) =>
        {
            service.RotateKey();
            service.IssueUserToken(user, "rp-app"); // 轮换与签发交错执行
            await Task.CompletedTask;
        });

        var jwk = System.Text.Json.JsonSerializer.SerializeToElement(service.ExportPublicJwk());
        var latest = service.IssueUserToken(user, "rp-app"); // 轮换尘埃落定后再签,必须可验

        var header = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Encoding.UTF8.GetString(Base64UrlDecode(latest.Split('.')[0])));
        Assert.Equal(jwk.GetProperty("kid").GetString(), header.GetProperty("kid").GetString());

        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportParameters(new System.Security.Cryptography.RSAParameters
        {
            Modulus = Base64UrlDecode(jwk.GetProperty("n").GetString()!),
            Exponent = Base64UrlDecode(jwk.GetProperty("e").GetString()!),
        });
        Assert.True(rsa.VerifyData(
            System.Text.Encoding.UTF8.GetBytes($"{latest.Split('.')[0]}.{latest.Split('.')[1]}"),
            Base64UrlDecode(latest.Split('.')[2]),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padding = (4 - value.Length % 4) % 4;
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
    }
}
