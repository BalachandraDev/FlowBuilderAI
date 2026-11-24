using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FlowCapture.Api.Infrastructure;
using FlowCapture.Application;
using FlowCapture.Application.Abstractions;
using FlowCapture.Infrastructure;
using FlowCapture.Infrastructure.Ai;
using FlowCapture.Infrastructure.Persistence;
using FlowCapture.Infrastructure.Security;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}"));

var config = builder.Configuration;
var security = config.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
if (Encoding.UTF8.GetByteCount(security.JwtSigningKey) < 32)
    throw new InvalidOperationException("Security:JwtSigningKey must be set to a random secret of at least 32 characters (use an environment variable or Key Vault).");

// ---- the database: when the administrator moved the app to another SQL Server, that server is used (see the Database page). If it cannot be
// reached the configured database is used instead, so the app still starts and the page can be opened to put things right.
var (savedDatabase, databaseState) = DatabaseOverride.Load(config, new SecretProtector(Microsoft.Extensions.Options.Options.Create(security)));
if (savedDatabase is not null) { config["ConnectionStrings:Default"] = savedDatabase; config["Database:Provider"] = "SqlServer"; }
if (databaseState.Problem is not null) Console.WriteLine("WARNING: " + databaseState.Problem);
builder.Services.AddSingleton(databaseState);

// ---- application layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(config);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---- MVC / JSON
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "bbflowbuild API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
        Description = "Paste the access token from /api/v1/auth/login.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
    c.CustomSchemaIds(t => t.FullName!.Replace('+', '.'));
});

// ---- authentication and authorisation
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = security.JwtIssuer,
        ValidateAudience = true, ValidAudience = security.JwtAudience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(security.JwtSigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name", RoleClaimType = "role",
    };
});
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, token) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { title = "That was a lot at once. Wait a minute and try again.", status = 429, code = "too_many_requests" }, token);
    };
    var perMinute = config.GetValue("RateLimit:AuthPerMinute", 10);
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));

    // Everything that starts an AI call (analysis, document, a photo, a recording). One free key is shared by the whole team, so one
    // person pressing buttons in a loop must not use up everybody's quota. Per signed-in user, per minute.
    var aiPerMinute = config.GetValue("RateLimit:AiPerMinute", 20);
    o.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = aiPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// ---- background jobs
var useHangfire = (config["Jobs:Mode"] ?? "Hangfire").Equals("Hangfire", StringComparison.OrdinalIgnoreCase);
if (useHangfire)
{
    builder.Services.AddHangfire(h => h
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(config.GetConnectionString("Default"), new SqlServerStorageOptions { PrepareSchemaIfNecessary = true, QueuePollInterval = TimeSpan.FromSeconds(2) }));
    builder.Services.AddHangfireServer(o => o.WorkerCount = config.GetValue("Jobs:Workers", 4));
}

// ---- health
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database")
    .AddCheck<AiKeyHealthCheck>("ai-key");

// ---- behind a reverse proxy (Azure App Service, nginx, a load balancer): trust X-Forwarded-* so HTTPS redirection and the
// audit trail's IP address see the real client. Off by default; the proxy must be listed or the network trusted explicitly.
if (config.GetValue("ForwardedHeaders:Enabled", false))
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = config.GetValue("ForwardedHeaders:ForwardLimit", 1);
        var trustAll = config.GetValue("ForwardedHeaders:TrustAllProxies", false);
        if (!trustAll)
        {
            o.KnownProxies.Clear();
            o.KnownNetworks.Clear();
        }
        foreach (var p in config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            o.KnownProxies.Add(System.Net.IPAddress.Parse(p));
        foreach (var n in config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        {
            var parts = n.Split('/');
            o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1])));
        }
        if (trustAll) { o.KnownProxies.Clear(); o.KnownNetworks.Clear(); }
    });
}

var app = builder.Build();

await DbSeeder.InitialiseAsync(app.Services);

if (config.GetValue("ForwardedHeaders:Enabled", false)) app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (!app.Environment.IsDevelopment() && config.GetValue("Https:Redirect", true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
if (config.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();                      // the web app (built into wwwroot by "npm run build" in the web folder)
// Anything that is not an API endpoint and not a file (for example /flow/123 from a shared link, or after a reload) gets the app itself;
// a missing file stays a 404. This is middleware, not a catch-all endpoint, because a catch-all endpoint would hide the files above.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint() is null && !context.Request.Path.StartsWithSegments("/swagger") && !SecurityHeadersMiddleware.IsApiPath(context.Request.Path))
    {
        await ServeApp(context);
        return;
    }
    await next();
});

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
// The web app. Its pages (the address "/", and for example /flow/123 opened from a shared link or after a reload) all get the app itself;
// the app's router shows the right page. Anything under /api, /health or /swagger that matches nothing stays a real 404, and so does a
// missing script or image. If the app has not been built yet, "/" explains what to do instead of showing a bare 401.
async Task ServeApp(HttpContext context)
{
    var index = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo("index.html");
    var path = context.Request.Path;
    if (index.Exists && HttpMethods.IsGet(context.Request.Method) && !SecurityHeadersMiddleware.IsApiPath(path) && !path.StartsWithSegments("/swagger")
        && (path == "/" || !Path.HasExtension(path.Value)))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
        return;
    }
    if (path == "/" && HttpMethods.IsGet(context.Request.Method))
    {
        var swagger = config.GetValue("Swagger:Enabled", context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment());
        if (swagger) { context.Response.Redirect("/swagger"); return; }
        await context.Response.WriteAsJsonAsync(new { name = "bbflowbuild API", status = "running", health = "/health/ready", app = "not built: run npm run build in the web folder" });
        return;
    }
    context.Response.StatusCode = StatusCodes.Status404NotFound;
}
app.MapGet("/", ServeApp).AllowAnonymous().ExcludeFromDescription();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

if (useHangfire && config.GetValue("Hangfire:Dashboard", false))
    app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new HangfireAdminFilter()] });

app.Run();

/// <summary>Reports "degraded" (not "unhealthy") when no AI key is set or the AI is in demo mode, so the API still serves capture and review.</summary>
internal sealed class AiKeyHealthCheck(IAiStatusService status) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var s = await status.GetAsync(ct);
        return s.Provider == "demo" ? HealthCheckResult.Degraded("AI is in demo mode: analyses return a fixed example. Not for real work.")
            : s.Ready ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(s.Notice ?? "The AI is not set up.");
    }
}

/// <summary>Allows the Hangfire dashboard for signed-in administrators only.</summary>
internal sealed class HangfireAdminFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context) =>
        context is Hangfire.Dashboard.AspNetCoreDashboardContext web && web.HttpContext.User.IsInRole(FlowCapture.Shared.AppRoles.Admin);
}

public partial class Program;
