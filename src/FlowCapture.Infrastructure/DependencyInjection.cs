using FlowCapture.Application;
using FlowCapture.Application.Abstractions;
using FlowCapture.Infrastructure.Ai;
using FlowCapture.Infrastructure.Diagrams;
using FlowCapture.Infrastructure.Documents;
using FlowCapture.Infrastructure.Jobs;
using FlowCapture.Infrastructure.Maintenance;
using FlowCapture.Infrastructure.Persistence;
using FlowCapture.Infrastructure.Security;
using FlowCapture.Infrastructure.Speech;
using FlowCapture.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowCapture.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiOptions>(config.GetSection(AiOptions.Section));
        services.Configure<CaptureOptions>(config.GetSection(CaptureOptions.Section));
        services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.Section));
        services.Configure<AnthropicOptions>(config.GetSection(AnthropicOptions.Section));
        services.Configure<SttOptions>(config.GetSection(SttOptions.Section));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<JobsOptions>(config.GetSection(JobsOptions.Section));
        services.Configure<DiagramOptions>(config.GetSection(DiagramOptions.Section));
        services.Configure<DocumentOptions>(config.GetSection(DocumentOptions.Section));

        // ---- database
        services.AddScoped<AuditSaveChangesInterceptor>();
        var provider = config["Database:Provider"] ?? "SqlServer";
        var connection = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
        services.AddDbContext<AppDbContext>((sp, o) =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) o.UseSqlite(connection);
            else o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure(3));
            o.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.TryAddSingleton(new DatabaseStartupState(false, null));         // the host replaces this when a saved server was (or could not be) used
        services.AddSingleton<DatabaseMoveService>();
        services.AddSingleton<IFileTextExtractor, FlowCapture.Infrastructure.Files.FileTextExtractor>();

        // ---- security, time, notifications
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IPushSender, LoggingPushSender>();

        // ---- storage
        if ((config["Storage:Provider"] ?? "FileSystem").Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IBlobStore, AzureBlobStore>();
        else
            services.AddSingleton<IBlobStore, FileSystemBlobStore>();

        // ---- speech, photos and AI. The administrator chooses the provider in the app (Gemini free tier by default, Anthropic, or Demo),
        // so the routers below look at the saved choice on every call and a change needs no restart.
        services.Configure<GeminiOptions>(config.GetSection(GeminiOptions.Section));
        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddHttpClient<GeminiClient>((sp, c) =>
        {
            var o = config.GetSection(GeminiOptions.Section).Get<GeminiOptions>() ?? new GeminiOptions();
            c.BaseAddress = new Uri(string.IsNullOrWhiteSpace(o.BaseUrl) ? "https://generativelanguage.googleapis.com/" : o.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.AddHttpClient<GroqClient>(c =>
        {
            c.BaseAddress = new Uri(config["Groq:BaseUrl"] is { Length: > 0 } b ? b : "https://api.groq.com/openai/v1/");
            c.Timeout = TimeSpan.FromSeconds(config.GetValue("Groq:TimeoutSeconds", 120));
        });
        services.AddHttpClient<AzureSpeechToTextService>(c => c.Timeout = TimeSpan.FromSeconds(config.GetValue("Stt:TimeoutSeconds", 600)));
        services.AddSingleton<AnthropicFlowAnalysisService>();
        services.AddSingleton<DemoFlowAnalysisService>();                                  // a fixed example, for demos without a key
        services.AddSingleton<DemoSpeechToText>();
        services.AddSingleton<DemoImageText>();
        services.AddTransient<GeminiFlowAnalysisService>();
        services.AddTransient<GeminiSpeechToText>();
        services.AddTransient<GroqSpeechToText>();
        services.AddTransient<GeminiImageText>();
        services.AddScoped<IFlowAnalysisService, AiProviderRouter>();
        services.AddScoped<ISpeechToTextService, SpeechToTextRouter>();
        services.AddScoped<IImageTextService, ImageTextRouter>();
        services.AddScoped<IAiStatusService, AiStatusService>();

        // ---- diagrams and documents
        services.AddSingleton<IDiagramRenderer, ChromiumDiagramRenderer>();
        services.AddSingleton<IHtmlPdfRenderer, ChromiumPdfRenderer>();
        services.AddSingleton<IRgdDocxWriter, OpenXmlRgdWriter>();
        services.AddSingleton<IPdfConverter, LibreOfficePdfConverter>();

        // ---- housekeeping
        services.Configure<MaintenanceOptions>(config.GetSection(MaintenanceOptions.Section));
        services.AddScoped<MaintenanceService>();
        services.AddHostedService<MaintenanceHostedService>();
        services.AddHostedService<Files.WarmUpHostedService>();             // the slow "first time" work happens at start, not when somebody saves

        // ---- jobs
        services.AddScoped<TranscriptionJob>();
        services.AddScoped<ImageReadJob>();
        services.AddScoped<GuideJob>();
        services.AddScoped<AnalysisJob>();
        services.AddScoped<DocumentJob>();
        if ((config["Jobs:Mode"] ?? "Hangfire").Equals("Inline", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InlineJobScheduler>();
            services.AddSingleton<IJobScheduler>(sp => sp.GetRequiredService<InlineJobScheduler>());
        }
        else
        {
            services.AddSingleton<IJobScheduler, HangfireJobScheduler>();
        }
        return services;
    }
}
