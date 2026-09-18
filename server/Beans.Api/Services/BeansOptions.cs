namespace Beans.Api.Services;

public sealed class BeansOptions
{
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    public string JwtIssuer { get; set; } = "beans-account";
    public string JwtAudience { get; set; } = "beans-clients";
    public string JwtSigningKey { get; set; } = "";
    public bool ExposeVerificationCodes { get; set; }
    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "Beans Music <noreply@beans.local>";
    public bool UseSsl { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
