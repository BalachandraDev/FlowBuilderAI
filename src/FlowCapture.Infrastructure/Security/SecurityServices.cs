using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FlowCapture.Application;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FlowCapture.Infrastructure.Security;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return _hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;
    }
}

public sealed class TokenService(IOptions<SecurityOptions> options, IClock clock) : ITokenService
{
    private readonly SecurityOptions _o = options.Value;

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_o.RefreshTokenDays);

    public AccessToken CreateAccessToken(User user, IReadOnlyCollection<string> roles)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.JwtSigningKey));
        var expires = clock.UtcNow.AddMinutes(_o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        // The API validates tokens with RoleClaimType = "role" and no claim-type remapping, so write the short name.
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var token = new JwtSecurityToken(_o.JwtIssuer, _o.JwtAudience, claims, notBefore: clock.UtcNow.AddSeconds(-5), expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string Raw, string Hash) NewRefreshToken()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (raw, HashRefreshToken(raw));
    }

    public string HashRefreshToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Placeholder push channel: records that a notification would be sent. The FCM adapter is wired with the mobile app.</summary>
public sealed class LoggingPushSender(Microsoft.Extensions.Logging.ILogger<LoggingPushSender> log) : IPushSender
{
    public Task SendToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
    {
        log.LogInformation("Push to user {UserId}: {Title}", userId, title);
        return Task.CompletedTask;
    }
}
