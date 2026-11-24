using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Persistence;

/// <summary>Applies migrations and creates reference data, the first admin and the prompt pack. Safe to run on every start.</summary>
public static partial class DbSeeder
{
    public static async Task InitialiseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var config = sp.GetRequiredService<IConfiguration>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

        if (db.Database.IsSqlServer()) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);

        await SeedReferenceDataAsync(db, ct);
        await SeedAdminAsync(db, sp.GetRequiredService<IPasswordService>(), config, log, ct);
        await FileExistingProjectsUnderClientsAsync(db, log, ct);
        await SeedPromptsAsync(db, config, log, ct);
        await SeedTemplateAsync(db, sp.GetRequiredService<IBlobStore>(), config, log, ct);
    }

    /// <summary>
    /// The first start stores a Word template and makes it the active one: templates/RGD_Template.docx if that file exists,
    /// otherwise the built-in default. Later templates are uploaded by an administrator.
    /// </summary>
    private static async Task SeedTemplateAsync(AppDbContext db, IBlobStore blobs, IConfiguration config, ILogger log, CancellationToken ct)
    {
        if (await db.DocumentTemplates.AnyAsync(ct)) return;

        var path = config["Documents:TemplatePath"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "templates", "RGD_Template.docx");
        byte[] bytes;
        string version;
        if (File.Exists(path)) { bytes = await File.ReadAllBytesAsync(path, ct); version = "1.0 (file)"; }
        else { bytes = Documents.RgdTemplateFactory.CreateDefault(); version = "1.0 (built-in)"; }

        var template = new DocumentTemplate { Name = "RGD template", Version = version, IsActive = true };
        template.BlobPath = $"templates/{template.Id:N}.docx";
        using (var ms = new MemoryStream(bytes))
            await blobs.SaveAsync(template.BlobPath, ms, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ct);
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded the Word template ({Version})", version);
    }

    private static async Task SeedReferenceDataAsync(AppDbContext db, CancellationToken ct)
    {
        foreach (var name in AppRoles.All)
            if (!await db.Roles.AnyAsync(r => r.Name == name, ct)) db.Roles.Add(new Role { Name = name });

        (string Code, string Name, string Stt)[] languages =
            [("en-IN", "English (India)", "en-IN"), ("ta-IN", "Tamil", "ta-IN"), ("hi-IN", "Hindi", "hi-IN")];
        foreach (var (code, name, stt) in languages)
            if (!await db.Languages.AnyAsync(l => l.Code == code, ct)) db.Languages.Add(new Language { Code = code, Name = name, SttLocale = stt });

        (string Code, string Name, string Definition, int Order)[] categories =
        [
            ("CR", "Change Request", "a change to existing BACKBONE behaviour", 1),
            ("NR", "New Requirement", "a capability that does not exist today", 2),
            ("ER", "Enhancement", "an improvement to an existing capability", 3),
        ];
        foreach (var (code, name, def, order) in categories)
            if (!await db.RequirementCategories.AnyAsync(c => c.Code == code, ct))
                db.RequirementCategories.Add(new RequirementCategory { Code = code, Name = name, Definition = def, SortOrder = order });

        (string Code, string Name)[] departments =
        [
            ("MRD", "MRD"), ("OT", "OT"), ("DIETARY", "Dietary"), ("INCIDENT", "Incident Management"),
            ("IP", "IP"), ("OP", "OP"), ("BILLING", "Billing"), ("PHARMACY", "Pharmacy"),
            ("LAB", "Lab"), ("NURSING", "Nursing"),
            // offered in the module picker of a project (the people who use the app named these two)
            ("WARD", "Ward"), ("NURSESTATION", "Nurse Station"),
        ];
        foreach (var (code, name) in departments)
            if (!await db.Departments.AnyAsync(d => d.Code == code, ct)) db.Departments.Add(new Department { Code = code, Name = name });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Projects made before clients existed are filed under a client of the same name, and the people who were on them are assigned to it, so nothing
    /// disappears when clients arrive. Only projects without a client are touched, so this is safe to run on every start.
    /// </summary>
    private static async Task FileExistingProjectsUnderClientsAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        var loose = await db.Projects.IgnoreQueryFilters().Include(p => p.Members).Where(p => p.ClientId == null).ToListAsync(ct);
        if (loose.Count == 0) return;

        var admins = (await db.UserRoles.AsNoTracking().Where(ur => ur.Role!.Name == AppRoles.Admin).Select(ur => ur.UserId).ToListAsync(ct)).ToHashSet();
        var clients = await db.Clients.Include(c => c.Users).ToListAsync(ct);
        foreach (var group in loose.GroupBy(p => p.HospitalName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var client = clients.FirstOrDefault(c => c.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                client = new Client { Name = group.Key, Location = group.Select(p => p.Location).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "" };
                db.Clients.Add(client);
                clients.Add(client);
            }
            foreach (var p in group)
            {
                p.ClientId = client.Id;
                foreach (var m in p.Members.Where(m => !admins.Contains(m.UserId) && client.Users.All(u => u.UserId != m.UserId)))
                    client.Users.Add(new ClientUser { ClientId = client.Id, UserId = m.UserId });
            }
        }
        // everybody assigned to a client can open all of its projects
        foreach (var p in loose)
            foreach (var cu in clients.First(c => c.Id == p.ClientId).Users.Where(u => p.Members.All(m => m.UserId != u.UserId)))
                p.Members.Add(new ProjectMember { ProjectId = p.Id, UserId = cu.UserId, ProjectRole = ProjectRole.Collaborator });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Filed {Projects} existing projects under {Clients} clients", loose.Count, clients.Count);
    }

    private static async Task SeedAdminAsync(AppDbContext db, IPasswordService passwords, IConfiguration config, ILogger log, CancellationToken ct)
    {
        var email = config["Seed:AdminEmail"]?.Trim().ToLowerInvariant();
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            if (!await db.Users.AnyAsync(ct))
                log.LogWarning("No users exist and Seed:AdminEmail / Seed:AdminPassword are not set. Nobody can sign in until an admin is seeded.");
            return;
        }
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;

        var admin = new User { Email = email, DisplayName = config["Seed:AdminName"] ?? "Administrator", PasswordHash = passwords.Hash(password) };
        var role = await db.Roles.FirstAsync(r => r.Name == AppRoles.Admin, ct);
        admin.Roles.Add(new UserRole { UserId = admin.Id, RoleId = role.Id });
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded administrator {Email}", email);
    }

    // ------------------------------------------------------------------ prompt pack

    [GeneratedRegex(@"^(?<key>.+)\.v(?<ver>\d+)\.(md|json)$")]
    private static partial Regex VersionedFile();

    private static async Task SeedPromptsAsync(AppDbContext db, IConfiguration config, ILogger log, CancellationToken ct)
    {
        var root = config["Prompts:Path"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "prompts");
        if (!Directory.Exists(root))
        {
            log.LogWarning("Prompt folder {Path} was not found. Analysis will fail until prompts are available.", root);
            return;
        }

        // oldest version first, so a newer file is always seen after the one it replaces
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, Rel: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Select(f => (f.File, f.Rel, Parsed: Classify(f.Rel)))
            .Where(f => f.Parsed is not null)
            .OrderBy(f => f.Parsed!.Value.Key, StringComparer.Ordinal).ThenBy(f => f.Parsed!.Value.Version)
            .ToList();

        foreach (var (file, rel, parsed) in files)
        {
            var (key, version) = parsed!.Value;

            var content = await File.ReadAllTextAsync(file, ct);
            var hash = Hashing.Sha256Hex(content);
            var existing = await db.PromptTemplates.FirstOrDefaultAsync(t => t.Key == key && t.Version == version, ct);
            if (existing is null)
            {
                // A new version in the prompt folder is how a prompt change is deployed, so it becomes the active one, replacing the earlier
                // versions that also came from the folder. A version somebody added by hand in the admin screen is never replaced.
                var active = await db.PromptTemplates.Where(t => t.Key == key && t.IsActive).ToListAsync(ct);
                var replaceable = active.Count == 0 || (active.All(a => (a.Notes ?? "").StartsWith("Seeded from", StringComparison.Ordinal)) && active.All(a => a.Version < version));
                if (replaceable)
                {
                    foreach (var a in active) a.IsActive = false;
                    log.LogInformation("Prompt {Key} v{Version} from {File} is now the active version", key, version, rel);
                }
                db.PromptTemplates.Add(new PromptTemplate { Key = key, Version = version, Content = content, Sha256 = hash, IsActive = replaceable, Notes = "Seeded from " + rel });
                await db.SaveChangesAsync(ct);                      // so the next file of the same key sees this one
            }
            else if (existing.Sha256 != hash)
            {
                log.LogWarning("Prompt file {File} differs from the stored {Key} v{Version}. Prompts are immutable: rename the file to the next version to deploy a change.", rel, key, version);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Maps a file in the prompt folder to its template key and version, or null when it is not a template.</summary>
    internal static (string Key, int Version)? Classify(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) return null;

        if (relativePath.StartsWith("schemas/", StringComparison.Ordinal))
        {
            var m = VersionedFile().Match(name);
            return m.Success ? ("schema." + m.Groups["key"].Value, int.Parse(m.Groups["ver"].Value)) : null;
        }
        if (relativePath.StartsWith("examples/", StringComparison.Ordinal))
            return ("example." + Path.GetFileNameWithoutExtension(name), 1);

        var v = VersionedFile().Match(name);
        return v.Success && relativePath.IndexOf('/') < 0 ? (v.Groups["key"].Value, int.Parse(v.Groups["ver"].Value)) : null;
    }
}
