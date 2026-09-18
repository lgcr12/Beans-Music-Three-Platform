using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Beans.Api.Contracts;
using Beans.Api.Data;
using Beans.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<BeansOptions>(builder.Configuration.GetSection("Beans"));
var beansOptions = builder.Configuration.GetSection("Beans").Get<BeansOptions>() ?? new BeansOptions();
if (Encoding.UTF8.GetByteCount(beansOptions.JwtSigningKey) < 32)
    throw new InvalidOperationException("Beans:JwtSigningKey must contain at least 32 UTF-8 bytes.");

builder.Services.AddDbContext<BeansDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration.GetConnectionString("Redis"));
builder.Services.AddSingleton<IPasswordHasher<UserEntity>, PasswordHasher<UserEntity>>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<VerificationService>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = beansOptions.JwtIssuer,
        ValidateAudience = true,
        ValidAudience = beansOptions.JwtAudience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(beansOptions.JwtSigningKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BeansDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGet("/v1/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow })).AllowAnonymous();

var auth = app.MapGroup("/v1/auth").RequireRateLimiting("auth");

auth.MapPost("/register/start", async (
    RegisterStartRequest request,
    BeansDbContext db,
    IPasswordHasher<UserEntity> passwordHasher,
    VerificationService verification,
    IOptions<BeansOptions> options,
    CancellationToken cancellationToken) =>
{
    var validation = ValidateRegistration(request);
    if (validation is not null) return validation;
    var email = request.Email.Trim();
    var normalized = NormalizeEmail(email);
    if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken))
        return Problem(StatusCodes.Status409Conflict, "该邮箱已注册");
    if (!await verification.ReserveSendAsync(normalized, "register", cancellationToken))
        return Problem(StatusCodes.Status429TooManyRequests, "验证码发送过于频繁，请稍后重试");

    var code = VerificationService.NewCode();
    var pending = new PendingRegistrationEntity
    {
        Id = Guid.NewGuid(), Email = email, NormalizedEmail = normalized,
        Nickname = request.Nickname.Trim(),
        AuthSecretHash = passwordHasher.HashPassword(new UserEntity
        {
            Id = Guid.Empty, Email = email, NormalizedEmail = normalized, Nickname = request.Nickname.Trim(),
            AuthSecretHash = "", CryptoProfileJson = "", WrappedVaultKey = "", CreatedAt = DateTimeOffset.UtcNow
        }, request.AuthSecret),
        CryptoProfileJson = JsonSerializer.Serialize(request.CryptoProfile),
        WrappedVaultKey = request.WrappedVaultKey,
        DeviceId = request.Device.Id, DeviceName = request.Device.Name.Trim(), DevicePlatform = request.Device.Platform,
        VerificationCodeHash = VerificationService.HashCode(code), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
    };
    var oldPending = await db.PendingRegistrations.Where(x => x.NormalizedEmail == normalized).ToListAsync(cancellationToken);
    db.PendingRegistrations.RemoveRange(oldPending);
    db.PendingRegistrations.Add(pending);
    await db.SaveChangesAsync(cancellationToken);
    await verification.SendAsync(email, code, "register", cancellationToken);
    var developmentCode = options.Value.ExposeVerificationCodes ? code : null;
    return Results.Accepted(value: new PendingRegistration(pending.Id, pending.ExpiresAt, developmentCode));
}).AllowAnonymous();

auth.MapPost("/verify-email", async (
    VerifyEmailRequest request,
    BeansDbContext db,
    TokenService tokens,
    CancellationToken cancellationToken) =>
{
    var pending = await db.PendingRegistrations.FindAsync([request.RegistrationId], cancellationToken);
    if (pending is null || pending.ExpiresAt <= DateTimeOffset.UtcNow || pending.FailedAttempts >= 5)
        return Problem(StatusCodes.Status410Gone, "验证码已过期，请重新注册");
    if (!VerificationService.Matches(request.Code, pending.VerificationCodeHash))
    {
        pending.FailedAttempts++;
        await db.SaveChangesAsync(cancellationToken);
        return Problem(StatusCodes.Status400BadRequest, "验证码不正确");
    }
    if (await db.Users.AnyAsync(x => x.NormalizedEmail == pending.NormalizedEmail, cancellationToken))
        return Problem(StatusCodes.Status409Conflict, "该邮箱已注册");

    var now = DateTimeOffset.UtcNow;
    var user = new UserEntity
    {
        Id = Guid.NewGuid(), Email = pending.Email, NormalizedEmail = pending.NormalizedEmail,
        Nickname = pending.Nickname, AuthSecretHash = pending.AuthSecretHash,
        CryptoProfileJson = pending.CryptoProfileJson, WrappedVaultKey = pending.WrappedVaultKey, CreatedAt = now
    };
    var device = new DeviceEntity
    {
        Id = pending.DeviceId, UserId = user.Id, Name = pending.DeviceName,
        Platform = pending.DevicePlatform, CreatedAt = now, LastSeenAt = now
    };
    db.Users.Add(user);
    db.Devices.Add(device);
    db.AccountCursors.Add(new AccountCursorEntity { UserId = user.Id });
    db.PendingRegistrations.Remove(pending);
    await db.SaveChangesAsync(cancellationToken);
    var pair = await tokens.IssueAsync(db, user, device, cancellationToken);
    return Results.Ok(ToAuthSession(user, pair));
}).AllowAnonymous();

auth.MapPost("/login/challenge", async (LoginChallengeRequest request, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var normalized = NormalizeEmail(request.Email);
    var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
    var profile = user is null
        ? new CryptoProfile("argon2id-v1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)))
        : JsonSerializer.Deserialize<CryptoProfile>(user.CryptoProfileJson) ?? new CryptoProfile("argon2id-v1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
    return Results.Ok(new AuthChallenge(profile));
}).AllowAnonymous();

auth.MapPost("/login", async (
    LoginRequest request,
    BeansDbContext db,
    IPasswordHasher<UserEntity> passwordHasher,
    TokenService tokens,
    CancellationToken cancellationToken) =>
{
    if (!ValidateDevice(request.Device) || !ContractValidation.IsBase64Bytes(request.AuthSecret, 32))
        return Problem(StatusCodes.Status400BadRequest, "登录参数无效");
    var normalized = NormalizeEmail(request.Email);
    var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
    if (user is null || passwordHasher.VerifyHashedPassword(user, user.AuthSecretHash, request.AuthSecret) == PasswordVerificationResult.Failed)
        return Problem(StatusCodes.Status401Unauthorized, "邮箱或密码错误");
    var now = DateTimeOffset.UtcNow;
    var device = await db.Devices.FindAsync([user.Id, request.Device.Id], cancellationToken);
    if (device is null)
    {
        device = new DeviceEntity { Id = request.Device.Id, UserId = user.Id, Name = request.Device.Name.Trim(), Platform = request.Device.Platform, CreatedAt = now, LastSeenAt = now };
        db.Devices.Add(device);
    }
    else
    {
        device.Name = request.Device.Name.Trim();
        device.Platform = request.Device.Platform;
        device.LastSeenAt = now;
        device.RevokedAt = null;
    }
    var pair = await tokens.IssueAsync(db, user, device, cancellationToken);
    return Results.Ok(ToAuthSession(user, pair));
}).AllowAnonymous();

auth.MapPost("/refresh", async (RefreshRequest request, BeansDbContext db, TokenService tokens, CancellationToken cancellationToken) =>
{
    var validated = await tokens.ValidateRefreshAsync(db, request.RefreshToken, cancellationToken);
    if (validated is null) return Problem(StatusCodes.Status401Unauthorized, "刷新令牌无效或已失效");
    validated.Value.OldToken.RevokedAt = DateTimeOffset.UtcNow;
    var pair = await tokens.IssueAsync(db, validated.Value.User, validated.Value.Device, cancellationToken);
    return Results.Ok(pair);
}).AllowAnonymous();

auth.MapPost("/logout", async (ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    var active = await db.RefreshTokens.Where(x => x.UserId == identity.Value.UserId && x.DeviceId == identity.Value.DeviceId && x.RevokedAt == null).ToListAsync(cancellationToken);
    foreach (var token in active) token.RevokedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization();

auth.MapPost("/password/reset/start", async (
    PasswordResetStartRequest request,
    BeansDbContext db,
    VerificationService verification,
    IOptions<BeansOptions> options,
    CancellationToken cancellationToken) =>
{
    var normalized = NormalizeEmail(request.Email);
    var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
    if (user is null) return Results.Accepted();
    if (!await verification.ReserveSendAsync(normalized, "reset", cancellationToken)) return Results.Accepted();
    var code = VerificationService.NewCode();
    db.VerificationCodes.Add(new VerificationCodeEntity
    {
        Id = Guid.NewGuid(), UserId = user.Id, NormalizedEmail = normalized, Purpose = "reset",
        CodeHash = VerificationService.HashCode(code), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
    });
    await db.SaveChangesAsync(cancellationToken);
    await verification.SendAsync(user.Email, code, "reset", cancellationToken);
    return options.Value.ExposeVerificationCodes ? Results.Accepted(value: new { developmentCode = code }) : Results.Accepted();
}).AllowAnonymous();

auth.MapPost("/password/reset/complete", async (
    PasswordResetRequest request,
    BeansDbContext db,
    IPasswordHasher<UserEntity> passwordHasher,
    CancellationToken cancellationToken) =>
{
    if (!ValidateCrypto(request.CryptoProfile) || !ContractValidation.IsBase64Bytes(request.AuthSecret, 32) || !ContractValidation.IsBase64Bytes(request.WrappedVaultKey, 32))
        return Problem(StatusCodes.Status400BadRequest, "重置参数无效");
    var normalized = NormalizeEmail(request.Email);
    var challenge = await db.VerificationCodes
        .Where(x => x.NormalizedEmail == normalized && x.Purpose == "reset" && x.ConsumedAt == null)
        .OrderByDescending(x => x.ExpiresAt).FirstOrDefaultAsync(cancellationToken);
    if (challenge is null || challenge.ExpiresAt <= DateTimeOffset.UtcNow || !VerificationService.Matches(request.Code, challenge.CodeHash))
        return Problem(StatusCodes.Status400BadRequest, "验证码无效或已过期");
    var user = await db.Users.SingleAsync(x => x.NormalizedEmail == normalized, cancellationToken);
    user.AuthSecretHash = passwordHasher.HashPassword(user, request.AuthSecret);
    user.CryptoProfileJson = JsonSerializer.Serialize(request.CryptoProfile);
    user.WrappedVaultKey = request.WrappedVaultKey;
    challenge.ConsumedAt = DateTimeOffset.UtcNow;
    var tokens = await db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync(cancellationToken);
    foreach (var token in tokens) token.RevokedAt = DateTimeOffset.UtcNow;
    var vault = await db.Vaults.FindAsync([user.Id], cancellationToken);
    if (vault is not null) db.Vaults.Remove(vault);
    db.SyncRecords.RemoveRange(db.SyncRecords.Where(x => x.UserId == user.Id));
    var cursor = await db.AccountCursors.FindAsync([user.Id], cancellationToken);
    if (cursor is not null) cursor.CurrentRevision = 0;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).AllowAnonymous();

var devices = app.MapGroup("/v1/devices").RequireAuthorization();
devices.MapGet("/", async (ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    var rows = await db.Devices.Where(x => x.UserId == identity.Value.UserId).OrderByDescending(x => x.LastSeenAt).ToListAsync(cancellationToken);
    return Results.Ok(rows.Select(x => new DeviceRecord(x.Id, x.Name, x.Platform, x.CreatedAt, x.LastSeenAt, x.RevokedAt is not null)));
});
devices.MapDelete("/{deviceId:guid}", async (Guid deviceId, ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    var device = await db.Devices.FindAsync([identity.Value.UserId, deviceId], cancellationToken);
    if (device is null) return Results.NotFound();
    device.RevokedAt = DateTimeOffset.UtcNow;
    var tokens = await db.RefreshTokens.Where(x => x.UserId == identity.Value.UserId && x.DeviceId == deviceId && x.RevokedAt == null).ToListAsync(cancellationToken);
    foreach (var token in tokens) token.RevokedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

var qr = app.MapGroup("/v1/qr-sessions");
qr.MapPost("/", async (CreateQrSessionRequest request, BeansDbContext db, CancellationToken cancellationToken) =>
{
    if (!ValidateDevice(request.Device) || !ContractValidation.IsBase64Bytes(request.EphemeralPublicKey, 32) || Convert.FromBase64String(request.EphemeralPublicKey).Length != 32 || !ContractValidation.IsBase64Bytes(request.ExchangeSecret, 32))
        return Problem(StatusCodes.Status400BadRequest, "二维码会话参数无效");
    var session = new QrSessionEntity
    {
        Id = Guid.NewGuid(), TargetDeviceId = request.Device.Id, TargetDeviceName = request.Device.Name.Trim(),
        TargetPlatform = request.Device.Platform, EphemeralPublicKey = request.EphemeralPublicKey,
        ExchangeSecretHash = TokenService.HashOpaqueToken(request.ExchangeSecret), VerificationCode = VerificationService.NewCode(),
        Status = "waiting", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
    };
    db.QrSessions.Add(session);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/v1/qr-sessions/{session.Id}", ToQrDto(session));
}).AllowAnonymous();
qr.MapGet("/{sessionId:guid}", async (Guid sessionId, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var session = await db.QrSessions.FindAsync([sessionId], cancellationToken);
    if (session is null) return Results.NotFound();
    await ExpireQrIfNeeded(session, db, cancellationToken);
    return Results.Ok(ToQrDto(session));
}).AllowAnonymous();
qr.MapPost("/{sessionId:guid}/approve", async (Guid sessionId, ApproveQrSessionRequest request, ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    var session = await db.QrSessions.FindAsync([sessionId], cancellationToken);
    if (session is null) return Results.NotFound();
    await ExpireQrIfNeeded(session, db, cancellationToken);
    if (session.Status != "waiting") return Problem(StatusCodes.Status409Conflict, "二维码会话不可确认");
    // The QR envelope contains an ephemeral public key plus AES-GCM nonce/tag,
    // so its encoded payload is larger than the 32-byte vault key itself.
    if (!FixedEquals(request.VerificationCode, session.VerificationCode) || !ContractValidation.IsBase64Bytes(request.EncryptedVaultKey, 80))
        return Problem(StatusCodes.Status400BadRequest, "校验码或密钥信封无效");
    session.Status = "approved";
    session.ApprovedByUserId = identity.Value.UserId;
    session.EncryptedVaultKey = request.EncryptedVaultKey;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization();
qr.MapPost("/{sessionId:guid}/exchange", async (Guid sessionId, ExchangeQrSessionRequest request, BeansDbContext db, TokenService tokens, CancellationToken cancellationToken) =>
{
    if (!ValidateDevice(request.Device) || !ContractValidation.IsBase64Bytes(request.EphemeralPublicKey, 32) || Convert.FromBase64String(request.EphemeralPublicKey).Length != 32 || !ContractValidation.IsBase64Bytes(request.ExchangeSecret, 32))
        return Problem(StatusCodes.Status400BadRequest, "二维码兑换参数无效");
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
    var session = await db.QrSessions.FindAsync([sessionId], cancellationToken);
    if (session is null) return Results.NotFound();
    await ExpireQrIfNeeded(session, db, cancellationToken);
    if (session.Status != "approved" || session.ExchangedAt is not null || session.ApprovedByUserId is null || session.EncryptedVaultKey is null)
        return Problem(StatusCodes.Status409Conflict, "二维码会话尚未确认或已兑换");
    if (session.TargetDeviceId != request.Device.Id || session.EphemeralPublicKey != request.EphemeralPublicKey || TokenService.HashOpaqueToken(request.ExchangeSecret) != session.ExchangeSecretHash)
        return Problem(StatusCodes.Status401Unauthorized, "二维码兑换凭证不匹配");
    var user = await db.Users.FindAsync([session.ApprovedByUserId.Value], cancellationToken);
    if (user is null) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    var device = await db.Devices.FindAsync([user.Id, request.Device.Id], cancellationToken);
    if (device is null)
    {
        device = new DeviceEntity { Id = request.Device.Id, UserId = user.Id, Name = request.Device.Name.Trim(), Platform = request.Device.Platform, CreatedAt = now, LastSeenAt = now };
        db.Devices.Add(device);
    }
    else
    {
        device.Name = request.Device.Name.Trim(); device.Platform = request.Device.Platform; device.RevokedAt = null; device.LastSeenAt = now;
    }
    session.Status = "exchanged";
    session.ExchangedAt = now;
    var pair = await tokens.IssueAsync(db, user, device, cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new QrExchangeResult(ToAccount(user), session.EncryptedVaultKey, pair.AccessToken, pair.AccessTokenExpiresAt, pair.RefreshToken, pair.RefreshTokenExpiresAt));
}).AllowAnonymous();

var vaults = app.MapGroup("/v1/vault").RequireAuthorization();
vaults.MapGet("/", async (ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    var vault = await db.Vaults.FindAsync([identity.Value.UserId], cancellationToken);
    return vault is null ? Results.NotFound() : Results.Ok(new VaultEnvelope(vault.Version, vault.Ciphertext, vault.UpdatedAt));
});
vaults.MapPut("/", async (VaultEnvelope request, ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    if (!ContractValidation.IsBase64Bytes(request.Ciphertext, 16)) return Problem(StatusCodes.Status400BadRequest, "保险库密文无效");
    var existing = await db.Vaults.FindAsync([identity.Value.UserId], cancellationToken);
    var expectedVersion = existing is null ? 1 : existing.Version + 1;
    if (request.Version != expectedVersion) return Problem(StatusCodes.Status409Conflict, $"保险库版本冲突，下一版本应为 {expectedVersion}");
    if (existing is null)
    {
        db.Vaults.Add(new VaultEntity { UserId = identity.Value.UserId, Version = 1, Ciphertext = request.Ciphertext, UpdatedAt = DateTimeOffset.UtcNow });
    }
    else
    {
        existing.Version = request.Version; existing.Ciphertext = request.Ciphertext; existing.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

var sync = app.MapGroup("/v1/sync").RequireAuthorization();
sync.MapGet("/", async (long? cursor, ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    var after = Math.Max(0, cursor ?? 0);
    var rows = await db.SyncRecords.Where(x => x.UserId == identity.Value.UserId && x.Revision > after).OrderBy(x => x.Revision).Take(201).ToListAsync(cancellationToken);
    var hasMore = rows.Count > 200;
    var page = rows.Take(200).Select(ToSyncDto).ToList();
    var next = page.Count == 0 ? after : page[^1].Revision;
    return Results.Ok(new SyncPage(next, hasMore, page));
});
sync.MapPost("/batch", async (List<SyncEnvelope> request, ClaimsPrincipal principal, BeansDbContext db, CancellationToken cancellationToken) =>
{
    var identity = Identity(principal);
    if (identity is null) return Results.Unauthorized();
    if (request.Count is 0 or > 200) return Problem(StatusCodes.Status400BadRequest, "同步批次必须包含 1 到 200 条记录");
    if (request.Any(x => x.DeviceId != identity.Value.DeviceId || !ContractValidation.IsEntityType(x.EntityType) || string.IsNullOrWhiteSpace(x.EntityId) || !ContractValidation.IsBase64Bytes(x.Ciphertext, 16)))
        return Problem(StatusCodes.Status400BadRequest, "同步记录无效");
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
    var cursor = await db.AccountCursors.FindAsync([identity.Value.UserId], cancellationToken);
    if (cursor is null) { cursor = new AccountCursorEntity { UserId = identity.Value.UserId }; db.AccountCursors.Add(cursor); }
    var response = new List<SyncEnvelope>(request.Count);
    foreach (var item in request)
    {
        var duplicate = await db.SyncRecords.SingleOrDefaultAsync(x => x.UserId == identity.Value.UserId && x.Id == item.Id, cancellationToken);
        if (duplicate is not null) { response.Add(ToSyncDto(duplicate)); continue; }
        var current = await db.SyncRecords.Where(x => x.UserId == identity.Value.UserId && x.EntityType == item.EntityType && x.EntityId == item.EntityId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(cancellationToken);
        var currentRevision = current?.Revision ?? 0;
        if (item.BaseRevision != currentRevision)
            return Results.Conflict(new { title = "同步版本冲突", current = current is null ? null : ToSyncDto(current) });
        cursor.CurrentRevision++;
        var entity = new SyncRecordEntity
        {
            Id = item.Id, UserId = identity.Value.UserId, EntityType = item.EntityType, EntityId = item.EntityId,
            DeviceId = item.DeviceId, BaseRevision = item.BaseRevision, Revision = cursor.CurrentRevision,
            Deleted = item.Deleted, Ciphertext = item.Ciphertext, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SyncRecords.Add(entity);
        response.Add(ToSyncDto(entity));
    }
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(response);
});

app.Run();

static IResult? ValidateRegistration(RegisterStartRequest request)
{
    if (request.Nickname.Trim().Length is < 2 or > 40) return Problem(StatusCodes.Status400BadRequest, "昵称长度应为 2 到 40 个字符");
    try { _ = new MailAddress(request.Email); } catch { return Problem(StatusCodes.Status400BadRequest, "邮箱格式无效"); }
    if (!ValidateDevice(request.Device) || !ValidateCrypto(request.CryptoProfile) || !ContractValidation.IsBase64Bytes(request.AuthSecret, 32) || !ContractValidation.IsBase64Bytes(request.WrappedVaultKey, 32))
        return Problem(StatusCodes.Status400BadRequest, "注册参数无效");
    return null;
}

static bool ValidateDevice(DeviceInput device) => device.Id != Guid.Empty && device.Name.Trim().Length is > 0 and <= 80 && ContractValidation.IsPlatform(device.Platform);
static bool ValidateCrypto(CryptoProfile profile) => profile.Algorithm == "argon2id-v1" && ContractValidation.IsBase64Bytes(profile.Salt, 16) && profile.MemoryKiB is >= 32768 and <= 262144 && profile.Iterations is >= 2 and <= 10 && profile.Parallelism is >= 1 and <= 8;
static string NormalizeEmail(string value) => value.Trim().ToUpperInvariant();
static AccountDto ToAccount(UserEntity user) => new(user.Id, user.Nickname, user.Email, user.CreatedAt);
static AuthSession ToAuthSession(UserEntity user, TokenPair pair) => new(ToAccount(user), user.WrappedVaultKey, pair.AccessToken, pair.AccessTokenExpiresAt, pair.RefreshToken, pair.RefreshTokenExpiresAt);
static QrSessionDto ToQrDto(QrSessionEntity value) => new(value.Id, value.Status, value.VerificationCode, value.ExpiresAt, new DeviceInput(value.TargetDeviceId, value.TargetDeviceName, value.TargetPlatform), value.EphemeralPublicKey);
static SyncEnvelope ToSyncDto(SyncRecordEntity value) => new(value.Id, value.EntityType, value.EntityId, value.DeviceId, value.BaseRevision, value.Revision, value.Deleted, value.Ciphertext, value.UpdatedAt);
static (Guid UserId, Guid DeviceId)? Identity(ClaimsPrincipal principal)
{
    var userRaw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    var deviceRaw = principal.FindFirstValue("device_id");
    return Guid.TryParse(userRaw, out var userId) && Guid.TryParse(deviceRaw, out var deviceId) ? (userId, deviceId) : null;
}
static async Task ExpireQrIfNeeded(QrSessionEntity session, BeansDbContext db, CancellationToken cancellationToken)
{
    if (session.Status is ("waiting" or "approved") && session.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        session.Status = "expired";
        await db.SaveChangesAsync(cancellationToken);
    }
}
static bool FixedEquals(string left, string right)
{
    var a = Encoding.UTF8.GetBytes(left);
    var b = Encoding.UTF8.GetBytes(right);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}
static IResult Problem(int status, string detail) => Results.Problem(statusCode: status, detail: detail);

public partial class Program;
