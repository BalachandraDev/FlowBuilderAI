using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.Auth;

public sealed record LoginCommand(string Email, string Password, string? DeviceId) : IRequest<TokenResponse>;
public sealed record RefreshCommand(string RefreshToken) : IRequest<TokenResponse>;
public sealed record LogoutCommand(string RefreshToken) : IRequest;
public sealed record ChangePasswordCommand(ChangePasswordRequest Body, string? DeviceId) : IRequest<TokenResponse>;
public sealed record MeQuery : IRequest<UserProfileDto>;
public sealed record RegisterDeviceCommand(string Platform, string PushToken) : IRequest;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.Body.CurrentPassword).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.NewPassword).Must(p => p is not null && Admin.PasswordPolicy.IsStrong(p)).WithMessage("The new password must be at least 10 characters with letters and digits.").MaximumLength(200);
    }
}

public sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceValidator()
    {
        RuleFor(x => x.Platform).Must(p => p is "android" or "ios").WithMessage("Platform must be android or ios.");
        RuleFor(x => x.PushToken).NotEmpty().MaximumLength(512);
    }
}

internal static class AuthSupport
{
    public static async Task<UserProfileDto> ProfileAsync(IAppDbContext db, User user, CancellationToken ct)
    {
        var roles = user.Roles.Select(r => r.Role!.Name).OrderBy(x => x).ToArray();
        var projects = await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == user.Id)
            .Select(m => new ProjectMembershipDto(m.ProjectId, m.Project!.Code, m.ProjectRole.ToString())).ToListAsync(ct);
        return new UserProfileDto(user.Id, user.Email, user.DisplayName, roles, projects);
    }

    public static async Task<TokenResponse> IssueAsync(
        IAppDbContext db, ITokenService tokens, IClock clock, User user, string? device, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user, user.Roles.Select(r => r.Role!.Name).ToArray());
        var (raw, hash) = tokens.NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id, TokenHash = hash, CreatedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow + tokens.RefreshTokenLifetime, DeviceInfo = device,
        });
        await db.SaveChangesAsync(ct);
        return new TokenResponse(access.Token, access.ExpiresAt, raw, await ProfileAsync(db, user, ct));
    }

    public static Task<User?> LoadUserAsync(IAppDbContext db, Func<IQueryable<User>, IQueryable<User>> filter, CancellationToken ct) =>
        filter(db.Users.Include(u => u.Roles).ThenInclude(r => r.Role)).FirstOrDefaultAsync(ct);
}

public sealed class LoginHandler(IAppDbContext db, IPasswordService passwords, ITokenService tokens, IClock clock)
    : IRequestHandler<LoginCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(LoginCommand r, CancellationToken ct)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        var user = await AuthSupport.LoadUserAsync(db, q => q.Where(u => u.Email == email), ct);
        // One generic message for unknown user, wrong password and disabled account.
        if (user is null || !user.IsActive || user.DeletedAt is not null || !passwords.Verify(user.PasswordHash, r.Password))
            throw new UnauthorizedAccessException("The email or password is incorrect.");
        user.LastLoginAt = clock.UtcNow;
        return await AuthSupport.IssueAsync(db, tokens, clock, user, r.DeviceId, ct);
    }
}

public sealed class RefreshHandler(IAppDbContext db, ITokenService tokens, IClock clock)
    : IRequestHandler<RefreshCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(RefreshCommand r, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(r.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) throw new UnauthorizedAccessException("The session is no longer valid. Sign in again.");

        if (token.RevokedAt is not null)
        {
            // A rotated token was presented again: assume it was stolen and end every session of this user.
            foreach (var t in await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null).ToListAsync(ct))
                t.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("The session is no longer valid. Sign in again.");
        }
        if (token.ExpiresAt <= clock.UtcNow) throw new UnauthorizedAccessException("The session has expired. Sign in again.");

        var user = await AuthSupport.LoadUserAsync(db, q => q.Where(u => u.Id == token.UserId), ct);
        if (user is null || !user.IsActive || user.DeletedAt is not null) throw new UnauthorizedAccessException("The account is disabled.");

        var response = await AuthSupport.IssueAsync(db, tokens, clock, user, token.DeviceInfo, ct);
        token.RevokedAt = clock.UtcNow;
        var newHash = tokens.HashRefreshToken(response.RefreshToken);
        token.ReplacedByTokenId = await db.RefreshTokens.Where(t => t.TokenHash == newHash)
            .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        await db.SaveChangesAsync(ct);
        return response;
    }
}

public sealed class LogoutHandler(IAppDbContext db, ITokenService tokens, ICurrentUser user, IClock clock) : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand r, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(r.RefreshToken);
        var uid = user.RequireUserId();
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == uid, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>
/// Lets the signed-in person change their own password. It asks for the current one, ends every other sign-in of theirs (other phones and
/// browsers), and returns fresh tokens so this one stays signed in.
/// </summary>
public sealed class ChangePasswordHandler(IAppDbContext db, IPasswordService passwords, ITokenService tokens, ICurrentUser current, IClock clock)
    : IRequestHandler<ChangePasswordCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(ChangePasswordCommand r, CancellationToken ct)
    {
        var uid = current.RequireUserId();
        var user = await AuthSupport.LoadUserAsync(db, q => q.Where(u => u.Id == uid), ct) ?? throw new NotFoundException("User", uid);
        // a business-rule answer (422), not 401: the person is signed in, they just typed the wrong current password
        if (!passwords.Verify(user.PasswordHash, r.Body.CurrentPassword))
            throw new DomainException("The current password is not right.", "wrong_password");
        if (r.Body.NewPassword == r.Body.CurrentPassword)
            throw new DomainException("Choose a password that is different from the current one.", "same_password");

        user.PasswordHash = passwords.Hash(r.Body.NewPassword);
        foreach (var t in await db.RefreshTokens.Where(t => t.UserId == uid && t.RevokedAt == null).ToListAsync(ct))
            t.RevokedAt = clock.UtcNow;
        return await AuthSupport.IssueAsync(db, tokens, clock, user, r.DeviceId, ct);
    }
}

public sealed class MeHandler(IAppDbContext db, ICurrentUser current) : IRequestHandler<MeQuery, UserProfileDto>
{
    public async Task<UserProfileDto> Handle(MeQuery r, CancellationToken ct)
    {
        var uid = current.RequireUserId();
        var user = await AuthSupport.LoadUserAsync(db, q => q.AsNoTracking().Where(u => u.Id == uid), ct)
                   ?? throw new NotFoundException("User", uid);
        return await AuthSupport.ProfileAsync(db, user, ct);
    }
}

public sealed class RegisterDeviceHandler(IAppDbContext db, ICurrentUser user, IClock clock) : IRequestHandler<RegisterDeviceCommand>
{
    public async Task Handle(RegisterDeviceCommand r, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var existing = await db.DeviceTokens.FirstOrDefaultAsync(d => d.UserId == uid && d.PushToken == r.PushToken, ct);
        if (existing is null)
            db.DeviceTokens.Add(new DeviceToken { UserId = uid, Platform = r.Platform, PushToken = r.PushToken, LastSeenAt = clock.UtcNow });
        else
            existing.LastSeenAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
