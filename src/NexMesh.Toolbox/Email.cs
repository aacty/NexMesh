using NexMesh.Core;

namespace NexMesh.Toolbox;

using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>邮件发送(NexMesh:Email:Smtp 配置节;接口化便于测试与替换 MailKit)。</summary>
public interface IEmailSender
{
    Task SendAsync(string subject, string body, IEnumerable<string> to, bool isBodyHtml = false, CancellationToken ct = default);
}

public sealed class NexMeshEmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}

public sealed class SmtpEmailSender(NexMeshEmailOptions options) : IEmailSender
{
    public async Task SendAsync(string subject, string body, IEnumerable<string> to, bool isBodyHtml = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(options.Host) || string.IsNullOrEmpty(options.From))
        {
            throw new InvalidOperationException("邮件未配置(NexMesh:Email:Smtp 的 Host/From)");
        }
        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.EnableSsl };
        if (!string.IsNullOrEmpty(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        }
        using var mail = new MailMessage
        {
            From = new MailAddress(options.From),
            Subject = subject,
            Body = body,
            IsBodyHtml = isBodyHtml,
        };
        foreach (var address in to)
        {
            mail.To.Add(address);
        }
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Toolbox 注册扩展。</summary>
public static class NexMeshToolboxExtensions
{
    /// <summary>
    /// 文件存储:NexMesh:Toolbox:FileStorage:Storage = Db(默认,事务库 Blob,需已 AddNexMeshDb)| Local(磁盘目录)。
    /// </summary>
    public static IServiceCollection AddNexMeshFileStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        var storage = configuration["NexMesh:Toolbox:FileStorage:Storage"] ?? "Db";
        if (storage.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            var directory = configuration["NexMesh:Toolbox:FileStorage:Directory"] ?? "./files";
            services.AddSingleton<IFileStorage>(new LocalFileStorage(directory));
        }
        else
        {
            services.AddScoped<IFileStorage>(sp =>
                new DbFileStorage(
                    sp.GetRequiredService<NexMesh.Data.IDbFactory>().Get(),
                    sp.GetService<ICurrentUser>()));
        }
        return services;
    }

    /// <summary>邮件:NexMesh:Email:Smtp 配置节。</summary>
    public static IServiceCollection AddNexMeshEmail(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IEmailSender>(sp =>
        {
            var options = new NexMeshEmailOptions();
            configuration.GetSection("NexMesh:Email:Smtp").Bind(options);
            return new SmtpEmailSender(options);
        });
        return services;
    }
}
