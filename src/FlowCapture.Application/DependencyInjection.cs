using FlowCapture.Application.Analysis;
using FlowCapture.Application.Capture;
using FlowCapture.Application.Documents;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Flows;
using FlowCapture.Application.Reviews;
using FlowCapture.Application.Services;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace FlowCapture.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddMediatR(c => c.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<IProjectAccess, ProjectAccess>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IPhiMasker, PhiMasker>();
        services.AddScoped<AiUsageRecorder>();

        services.AddScoped<PromptComposer>();
        services.AddScoped<AnalysisInputBuilder>();
        services.AddScoped<FlowVersionWriter>();
        services.AddScoped<AnalysisRunner>();
        services.AddScoped<AnalysisQueueService>();

        services.AddScoped<DiagramService>();
        services.AddScoped<DocumentNumbering>();
        services.AddScoped<DocumentGenerationService>();
        services.AddScoped<GuideGenerationService>();
        services.AddScoped<CombinedGuideService>();

        services.AddScoped<EntryService>();
        services.AddScoped<AudioService>();
        services.AddScoped<ImageService>();
        services.AddScoped<FileNoteService>();
        services.AddScoped<Documents.FigureService>();
        services.AddScoped<Services.IAiQuota, Services.AiQuotaService>();
        services.AddScoped<Services.IModelQuota, Services.ModelQuotaService>();
        services.AddScoped<TranscriptionService>();
        services.AddScoped<ReviewService>();
        return services;
    }
}
