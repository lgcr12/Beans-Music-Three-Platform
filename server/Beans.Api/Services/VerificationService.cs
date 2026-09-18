using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Beans.Api.Services;

public sealed class VerificationService(IOptions<BeansOptions> options, IDistributedCache cache, ILogger<VerificationService> logger)
{
    private readonly BeansOptions _options = options.Value;

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
    public static bool Matches(string code, string expectedHash) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(HashCode(code)), Convert.FromHexString(expectedHash));

    public async Task<bool> ReserveSendAsync(string normalizedEmail, string purpose, CancellationToken cancellationToken)
    {
        var key = $"verify-send:{purpose}:{normalizedEmail}";
        if (await cache.GetStringAsync(key, cancellationToken) is not null) return false;
        await cache.SetStringAsync(key, "1", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) }, cancellationToken);
        return true;
    }

    public async Task SendAsync(string email, string code, string purpose, CancellationToken cancellationToken)
    {
        using var message = new MailMessage { From = new MailAddress(ParseAddress(_options.Smtp.From)), Subject = purpose == "register" ? "Beans Music 注册验证码" : "Beans Music 密码重置验证码" };
        message.To.Add(email);
        message.Body = $"你的 Beans Music 验证码是：{code}\n\n10 分钟内有效。若非本人操作，请忽略。";
        using var smtp = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port) { EnableSsl = _options.Smtp.UseSsl };
        if (!string.IsNullOrEmpty(_options.Smtp.Username)) smtp.Credentials = new NetworkCredential(_options.Smtp.Username, _options.Smtp.Password);
        await smtp.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Verification email sent for {Purpose} to redacted recipient", purpose);
    }

    private static string ParseAddress(string value)
    {
        try { return new MailAddress(value).Address; }
        catch { return "noreply@beans.local"; }
    }
}
