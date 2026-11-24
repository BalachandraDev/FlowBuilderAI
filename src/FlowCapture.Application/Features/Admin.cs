using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.Admin;

// ------------------------------------------------------------------ users

public sealed record ListUsersQuery : IRequest<IReadOnlyList<UserDto>>;
public sealed record CreateUserCommand(CreateUserRequest Body) : IRequest<UserDto>;
public sealed record UpdateUserCommand(Guid Id, UpdateUserRequest Body) : IRequest<UserDto>;
public sealed record ResetPasswordCommand(Guid Id) : IRequest<ResetPasswordResponse>;
public sealed record DeleteUserCommand(Guid Id) : IRequest;

public static class PasswordPolicy
{
    public static bool IsStrong(string p) =>
        p.Length >= 10 && p.Any(char.IsLetter) && p.Any(char.IsDigit);
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Body.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Body.Password).Must(PasswordPolicy.IsStrong).WithMessage("Password must be at least 10 characters with letters and digits.");
        RuleFor(x => x.Body.Roles).NotEmpty().Must(r => r.All(AppRoles.All.Contains)).WithMessage("Unknown role.");
    }
}

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Body.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Body.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Body.Email));
        RuleFor(x => x.Body.Roles).NotEmpty().Must(r => r.All(AppRoles.All.Contains)).WithMessage("Unknown role.");
    }
}

public sealed class UserHandlers(IAppDbContext db, IPasswordService passwords, ICurrentUser currentUser) :
    IRequestHandler<ListUsersQuery, IReadOnlyList<UserDto>>,
    IRequestHandler<CreateUserCommand, UserDto>,
    IRequestHandler<UpdateUserCommand, UserDto>,
    IRequestHandler<ResetPasswordCommand, ResetPasswordResponse>,
    IRequestHandler<DeleteUserCommand>
{
    public async Task<IReadOnlyList<UserDto>> Handle(ListUsersQuery r, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(x => x.Role).Where(u => u.DeletedAt == null).OrderBy(u => u.DisplayName).ToListAsync(ct);
        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto> Handle(CreateUserCommand r, CancellationToken ct)
    {
        var email = r.Body.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new ConflictException("A user with this email already exists.");
        var user = new User
        {
            Email = email, DisplayName = r.Body.DisplayName.Trim(), PasswordHash = passwords.Hash(r.Body.Password),
            PreferredLanguage = r.Body.PreferredLanguage, IsActive = true,
        };
        await SetRolesAsync(user, r.Body.Roles, ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return await Load(user.Id, ct);
    }

    public async Task<UserDto> Handle(UpdateUserCommand r, CancellationToken ct)
    {
        // an administrator who stops or demotes themselves could leave nobody able to sign in as admin
        if (r.Id == currentUser.UserId && (!r.Body.IsActive || !r.Body.Roles.Contains(AppRoles.Admin)))
            throw new DomainException("You cannot stop your own access or remove your own administrator role. Ask another administrator.", "self_lockout");
        var user = await db.Users.Include(u => u.Roles).ThenInclude(x => x.Role).FirstOrDefaultAsync(u => u.Id == r.Id, ct)
                   ?? throw new NotFoundException("User", r.Id);
        user.DisplayName = r.Body.DisplayName.Trim();
        user.IsActive = r.Body.IsActive;
        user.PreferredLanguage = r.Body.PreferredLanguage;
        // the sign-in name (email) can be changed too; it stays unique, and the person is signed out so they sign in with the new one
        var newEmail = string.IsNullOrWhiteSpace(r.Body.Email) ? null : r.Body.Email.Trim().ToLowerInvariant();
        var emailChanged = newEmail is not null && newEmail != user.Email;
        if (emailChanged)
        {
            if (await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != user.Id, ct)) throw new ConflictException("A user with this email already exists.");
            user.Email = newEmail!;
        }
        await SetRolesAsync(user, r.Body.Roles, ct);
        if (!user.IsActive || (emailChanged && user.Id != currentUser.UserId))
            foreach (var t in await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct))
                t.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await Load(user.Id, ct);
    }

    public async Task<ResetPasswordResponse> Handle(ResetPasswordCommand r, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == r.Id, ct) ?? throw new NotFoundException("User", r.Id);
        var temp = NewTemporaryPassword();
        user.PasswordHash = passwords.Hash(temp);
        foreach (var t in await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct))
            t.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ResetPasswordResponse(temp);
    }

    /// <summary>
    /// Deletes a user: they cannot sign in, they leave the lists and the client assignments, and their email is freed for somebody else. Nothing is
    /// erased: the name stays on the notes and documents they made, and the row can be brought back by clearing DeletedAt. Only an administrator
    /// (the controller), never oneself, and never the last administrator.
    /// </summary>
    public async Task Handle(DeleteUserCommand r, CancellationToken ct)
    {
        if (r.Id == currentUser.UserId) throw new DomainException("You cannot delete your own account. Ask another administrator.", "self_delete");
        var user = await db.Users.Include(u => u.Roles).ThenInclude(x => x.Role).FirstOrDefaultAsync(u => u.Id == r.Id && u.DeletedAt == null, ct)
                   ?? throw new NotFoundException("User", r.Id);
        if (user.Roles.Any(x => x.Role!.Name == AppRoles.Admin) && user.IsActive
            && !await db.Users.AnyAsync(u => u.Id != user.Id && u.IsActive && u.DeletedAt == null && u.Roles.Any(x => x.Role!.Name == AppRoles.Admin), ct))
            throw new DomainException("This is the only administrator. Make another person an administrator first.", "last_admin");

        var now = DateTime.UtcNow;
        user.DeletedAt = now;
        user.IsActive = false;
        // the email is freed (it is unique), and what it was is kept in the new value
        var freed = $"deleted-{now:yyyyMMddHHmmss}-{user.Id.ToString("N")[..6]}-{user.Email}";
        user.Email = freed.Length > 256 ? freed[..256] : freed;

        db.ClientUsers.RemoveRange(await db.ClientUsers.Where(c => c.UserId == user.Id).ToListAsync(ct));
        // access they were given (a colleague on a client, an executive, a reviewer) goes; a project they started stays theirs on paper
        db.ProjectMembers.RemoveRange(await db.ProjectMembers.Where(m => m.UserId == user.Id && m.ProjectRole != ProjectRole.Lead).ToListAsync(ct));
        db.DepartmentAssignments.RemoveRange(await db.DepartmentAssignments.Where(a => a.UserId == user.Id).ToListAsync(ct));
        db.DeviceTokens.RemoveRange(await db.DeviceTokens.Where(d => d.UserId == user.Id).ToListAsync(ct));
        foreach (var t in await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct)) t.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task SetRolesAsync(User user, string[] roleNames, CancellationToken ct)
    {
        var roles = await db.Roles.Where(x => roleNames.Contains(x.Name)).ToListAsync(ct);
        user.Roles.RemoveAll(ur => !roleNames.Contains(ur.Role!.Name));
        foreach (var role in roles.Where(role => user.Roles.All(ur => ur.RoleId != role.Id)))
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });
    }

    private async Task<UserDto> Load(Guid id, CancellationToken ct) =>
        ToDto(await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(x => x.Role).FirstAsync(u => u.Id == id, ct));

    private static UserDto ToDto(User u) => new(u.Id, u.Email, u.DisplayName, u.IsActive,
        u.Roles.Select(x => x.Role!.Name).OrderBy(x => x).ToArray(), u.PreferredLanguage);

    private static string NewTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var chars = new char[14];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        chars[^1] = (char)('2' + RandomNumberGenerator.GetInt32(8));    // guarantee a digit
        chars[0] = 'A';                                                 // guarantee a letter
        return new string(chars);
    }
}

// ------------------------------------------------------------------ master data

public sealed record ListDepartmentsQuery : IRequest<IReadOnlyList<DepartmentDto>>;
public sealed record UpsertDepartmentCommand(Guid? Id, UpsertDepartmentRequest Body) : IRequest<DepartmentDto>;
public sealed record ListCategoriesQuery : IRequest<IReadOnlyList<CategoryDto>>;
public sealed record PutCategoriesCommand(IReadOnlyList<CategoryDto> Items) : IRequest<IReadOnlyList<CategoryDto>>;
public sealed record ListLanguagesQuery : IRequest<IReadOnlyList<LanguageDto>>;
public sealed record PutLanguagesCommand(IReadOnlyList<LanguageDto> Items) : IRequest<IReadOnlyList<LanguageDto>>;

public sealed class UpsertDepartmentValidator : AbstractValidator<UpsertDepartmentCommand>
{
    public UpsertDepartmentValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().Matches("^[A-Z0-9_-]{2,20}$").WithMessage("Code must be 2-20 characters: A-Z, 0-9, _ or -.");
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(120);
    }
}

public sealed class PutCategoriesValidator : AbstractValidator<PutCategoriesCommand>
{
    public PutCategoriesValidator()
    {
        RuleFor(x => x.Items).NotEmpty().Must(i => i.Select(c => c.Code).Distinct().Count() == i.Count).WithMessage("Category codes must be unique.");
        RuleForEach(x => x.Items).ChildRules(c =>
        {
            c.RuleFor(i => i.Code).NotEmpty().Matches("^[A-Z]{1,6}$").WithMessage("Category code must be 1-6 capital letters.");
            c.RuleFor(i => i.Name).NotEmpty().MaximumLength(60);
            c.RuleFor(i => i.Definition).NotEmpty().MaximumLength(400);
        });
    }
}

public sealed class PutLanguagesValidator : AbstractValidator<PutLanguagesCommand>
{
    public PutLanguagesValidator()
    {
        RuleFor(x => x.Items).NotEmpty().Must(i => i.Select(c => c.Code).Distinct().Count() == i.Count).WithMessage("Language codes must be unique.");
        RuleForEach(x => x.Items).ChildRules(c =>
        {
            c.RuleFor(i => i.Code).NotEmpty().MaximumLength(10);
            c.RuleFor(i => i.Name).NotEmpty().MaximumLength(60);
            c.RuleFor(i => i.SttLocale).NotEmpty().MaximumLength(20);
        });
    }
}

public sealed class MasterDataHandlers(IAppDbContext db) :
    IRequestHandler<ListDepartmentsQuery, IReadOnlyList<DepartmentDto>>,
    IRequestHandler<UpsertDepartmentCommand, DepartmentDto>,
    IRequestHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>,
    IRequestHandler<PutCategoriesCommand, IReadOnlyList<CategoryDto>>,
    IRequestHandler<ListLanguagesQuery, IReadOnlyList<LanguageDto>>,
    IRequestHandler<PutLanguagesCommand, IReadOnlyList<LanguageDto>>
{
    public async Task<IReadOnlyList<DepartmentDto>> Handle(ListDepartmentsQuery r, CancellationToken ct) =>
        await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new DepartmentDto(d.Id, d.Code, d.Name, d.IsActive)).ToListAsync(ct);

    public async Task<DepartmentDto> Handle(UpsertDepartmentCommand r, CancellationToken ct)
    {
        Department? d = null;
        if (r.Id is { } id) d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Department", id);
        if (await db.Departments.AnyAsync(x => x.Code == r.Body.Code && (d == null || x.Id != d.Id), ct))
            throw new ConflictException($"Department code '{r.Body.Code}' is already used.");
        var wanted = r.Body.Name.Trim().ToLower();
        if (await db.Departments.AnyAsync(x => x.Name.ToLower() == wanted && (d == null || x.Id != d.Id), ct))
            throw new ConflictException($"There is already a module called \"{r.Body.Name.Trim()}\".");
        if (d is null) db.Departments.Add(d = new Department());
        d.Code = r.Body.Code;
        d.Name = r.Body.Name.Trim();
        d.IsActive = r.Body.IsActive;
        await db.SaveChangesAsync(ct);
        return new DepartmentDto(d.Id, d.Code, d.Name, d.IsActive);
    }

    public async Task<IReadOnlyList<CategoryDto>> Handle(ListCategoriesQuery r, CancellationToken ct) =>
        await db.RequirementCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Code)
            .Select(c => new CategoryDto(c.Code, c.Name, c.Definition, c.IsActive, c.SortOrder)).ToListAsync(ct);

    public async Task<IReadOnlyList<CategoryDto>> Handle(PutCategoriesCommand r, CancellationToken ct)
    {
        var existing = await db.RequirementCategories.ToListAsync(ct);
        foreach (var item in r.Items)
        {
            var row = existing.FirstOrDefault(c => c.Code == item.Code);
            if (row is null) db.RequirementCategories.Add(row = new RequirementCategory { Code = item.Code });
            row.Name = item.Name; row.Definition = item.Definition; row.IsActive = item.IsActive; row.SortOrder = item.SortOrder;
        }
        // Codes that are no longer listed are retired, not deleted, because old flows still use them.
        foreach (var row in existing.Where(c => r.Items.All(i => i.Code != c.Code))) row.IsActive = false;
        if (!r.Items.Any(i => i.IsActive)) throw new DomainException("At least one category must stay active.");
        await db.SaveChangesAsync(ct);
        return await Handle(new ListCategoriesQuery(), ct);
    }

    public async Task<IReadOnlyList<LanguageDto>> Handle(ListLanguagesQuery r, CancellationToken ct) =>
        await db.Languages.AsNoTracking().OrderBy(l => l.Name).Select(l => new LanguageDto(l.Code, l.Name, l.SttLocale, l.IsActive)).ToListAsync(ct);

    public async Task<IReadOnlyList<LanguageDto>> Handle(PutLanguagesCommand r, CancellationToken ct)
    {
        var existing = await db.Languages.ToListAsync(ct);
        foreach (var item in r.Items)
        {
            var row = existing.FirstOrDefault(c => c.Code == item.Code);
            if (row is null) db.Languages.Add(row = new Language { Code = item.Code });
            row.Name = item.Name; row.SttLocale = item.SttLocale; row.IsActive = item.IsActive;
        }
        foreach (var row in existing.Where(c => r.Items.All(i => i.Code != c.Code))) row.IsActive = false;
        await db.SaveChangesAsync(ct);
        return await Handle(new ListLanguagesQuery(), ct);
    }
}

// ------------------------------------------------------------------ settings

public sealed record GetSettingsQuery : IRequest<IReadOnlyList<SettingDto>>;
public sealed record PutSettingsCommand(IReadOnlyList<SettingDto> Items) : IRequest<IReadOnlyList<SettingDto>>;

public sealed class PutSettingsValidator : AbstractValidator<PutSettingsCommand>
{
    public PutSettingsValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(s => s.Key).Must(k => SettingsService.AllowedKeys.Contains(k)).WithMessage("Unknown setting key.");
            i.RuleFor(s => s.Value).MaximumLength(8000);
        });
        RuleForEach(x => x.Items).Must(s => ValueIsValid(s.Key, s.Value)).WithMessage("The value is not valid for this setting.");
    }

    private static bool ValueIsValid(string key, string value) => key switch
    {
        "Ai.MaxTokens" => int.TryParse(value, out var n) && n is >= 1000 and <= 128000,
        "Ai.MonthlyTokenLimitGlobal" => long.TryParse(value, out var n) && n >= 0,
        "Audio.MaxMinutes" => int.TryParse(value, out var n) && n is >= 1 and <= 120,
        "Ai.DisableThinking" => bool.TryParse(value, out _),
        "Ai.Effort" => value is "" or "low" or "medium" or "high" or "xhigh" or "max",
        "Ai.PricePerMTokInput" or "Ai.PricePerMTokOutput" or "Ai.PricePerMTokCacheRead" => decimal.TryParse(value, out var d) && d >= 0,
        "Ai.Model" => Regex.IsMatch(value, "^[a-z0-9.-]{3,60}$"),
        _ => true,
    };
}

public sealed class SettingsHandlers(ISettingsService settings) :
    IRequestHandler<GetSettingsQuery, IReadOnlyList<SettingDto>>,
    IRequestHandler<PutSettingsCommand, IReadOnlyList<SettingDto>>
{
    public async Task<IReadOnlyList<SettingDto>> Handle(GetSettingsQuery r, CancellationToken ct) =>
        (await settings.GetAllAsync(ct)).Select(kv => new SettingDto(kv.Key, kv.Value)).OrderBy(s => s.Key).ToList();

    public async Task<IReadOnlyList<SettingDto>> Handle(PutSettingsCommand r, CancellationToken ct)
    {
        foreach (var s in r.Items) await settings.SetAsync(s.Key, s.Value, ct);
        return await Handle(new GetSettingsQuery(), ct);
    }
}

// ------------------------------------------------------------------ prompt templates

public sealed record ListPromptsQuery(bool IncludeContent) : IRequest<IReadOnlyList<PromptTemplateDto>>;
public sealed record CreatePromptCommand(CreatePromptRequest Body) : IRequest<PromptTemplateDto>;
public sealed record ActivatePromptCommand(Guid Id) : IRequest<PromptTemplateDto>;

public sealed class CreatePromptValidator : AbstractValidator<CreatePromptCommand>
{
    public CreatePromptValidator()
    {
        RuleFor(x => x.Body.Key).NotEmpty().Matches("^[a-z0-9.-]{3,80}$");
        RuleFor(x => x.Body.Content).NotEmpty().MaximumLength(200_000);
        RuleFor(x => x.Body).Must(b => !b.Key.StartsWith("schema.") || IsJsonObject(b.Content)).WithMessage("A schema must be a JSON object.");
    }

    private static bool IsJsonObject(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject; } catch (JsonException) { return false; }
    }
}

public sealed class PromptHandlers(IAppDbContext db) :
    IRequestHandler<ListPromptsQuery, IReadOnlyList<PromptTemplateDto>>,
    IRequestHandler<CreatePromptCommand, PromptTemplateDto>,
    IRequestHandler<ActivatePromptCommand, PromptTemplateDto>
{
    public async Task<IReadOnlyList<PromptTemplateDto>> Handle(ListPromptsQuery r, CancellationToken ct) =>
        (await db.PromptTemplates.AsNoTracking().OrderBy(p => p.Key).ThenByDescending(p => p.Version).ToListAsync(ct))
        .Select(p => ToDto(p, r.IncludeContent)).ToList();

    public async Task<PromptTemplateDto> Handle(CreatePromptCommand r, CancellationToken ct)
    {
        var key = r.Body.Key;
        var next = (await db.PromptTemplates.Where(p => p.Key == key).MaxAsync(p => (int?)p.Version, ct) ?? 0) + 1;
        var row = new PromptTemplate
        {
            Key = key, Version = next, Content = r.Body.Content, Sha256 = Hashing.Sha256Hex(r.Body.Content),
            Notes = r.Body.Notes, IsActive = false,
        };
        db.PromptTemplates.Add(row);
        await db.SaveChangesAsync(ct);
        if (r.Body.Activate) return await Handle(new ActivatePromptCommand(row.Id), ct);
        return ToDto(row, false);
    }

    public async Task<PromptTemplateDto> Handle(ActivatePromptCommand r, CancellationToken ct)
    {
        var row = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Id == r.Id, ct) ?? throw new NotFoundException("Prompt template", r.Id);
        foreach (var other in await db.PromptTemplates.Where(p => p.Key == row.Key && p.IsActive && p.Id != row.Id).ToListAsync(ct))
            other.IsActive = false;
        row.IsActive = true;
        await db.SaveChangesAsync(ct);
        return ToDto(row, false);
    }

    private static PromptTemplateDto ToDto(PromptTemplate p, bool content) =>
        new(p.Id, p.Key, p.Version, p.IsActive, p.Sha256, p.Notes, content ? p.Content : null);
}
