using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Beans.Api.Contracts;
using Beans.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Beans.Api.Services;

public sealed class TokenService(IOptions<BeansOptions> options)
{
    private readonly BeansOptions _options = options.Value;

    public static string HashOpaqueToken(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(token)));
    public static string NewOpaqueToken(int bytes = 48) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    public async Task<TokenPair> IssueAsync(BeansDbContext db, UserEntity user, DeviceEntity device, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.AddMinutes(15);
        var refreshExpires = now.AddDays(30);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSigningKey));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("device_id", device.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var jwt = new JwtSecurityToken(
            _options.JwtIssuer,
            _options.JwtAudience,
            claims,
            now.UtcDateTime,
            accessExpires.UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var access = new JwtSecurityTokenHandler().WriteToken(jwt);
        var refresh = NewOpaqueToken();
        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.NewGuid(), UserId = user.Id, DeviceId = device.Id,
            TokenHash = HashOpaqueToken(refresh), CreatedAt = now, ExpiresAt = refreshExpires
        });
        device.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new TokenPair(access, accessExpires, refresh, refreshExpires);
    }

    public async Task<(UserEntity User, DeviceEntity Device, RefreshTokenEntity OldToken)?> ValidateRefreshAsync(BeansDbContext db, string raw, CancellationToken cancellationToken)
    {
        if (!ContractValidation.IsBase64Bytes(raw, 32)) return null;
        var hash = HashOpaqueToken(raw);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var user = await db.Users.FindAsync([token.UserId], cancellationToken);
        var device = await db.Devices.FindAsync([token.UserId, token.DeviceId], cancellationToken);
        if (user is null || device is null || device.RevokedAt is not null) return null;
        return (user, device, token);
    }
}
