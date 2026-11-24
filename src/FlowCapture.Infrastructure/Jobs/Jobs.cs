using System.Collections.Concurrent;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Analysis;
using FlowCapture.Application.Capture;
using FlowCapture.Application.Documents;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Jobs;

public sealed class JobsOptions
{
    public const string Section = "Jobs";
    /// <summary>Hangfire (SQL Server storage, retries, dashboard) | Inline (in-process; development and tests).</summary>
    public string Mode { get; set; } = "Hangfire";
}

/// <summary>Transcription: retried with back-off when the speech service is busy or unreachable.</summary>
public sealed class TranscriptionJob(TranscriptionService service)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 600], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(Guid entryId, CancellationToken ct) => service.ProcessAsync(entryId, ct);
}

/// <summary>Reading the text of a photo: retried with back-off when the free AI is busy.</summary>
public sealed class ImageReadJob(TranscriptionService service)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 600], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(Guid entryId, CancellationToken ct) => service.ProcessImageAsync(entryId, ct);
}

/// <summary>The implementation guide: diagram, AI proposal and PDF. Not retried automatically: the user starts it again.</summary>
public sealed class GuideJob(GuideGenerationService service)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid jobId, CancellationToken ct) => service.ExecuteAsync(jobId, ct);
}

/// <summary>Analysis: never retried automatically (it costs money). A failed run is shown to the user, who can run it again.</summary>
public sealed class AnalysisJob(AnalysisRunner runner)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid runId, CancellationToken ct) => runner.ExecuteAsync(runId, ct);
}

/// <summary>Document generation takes a while (diagram, Word, PDF), so it is a background job. Not retried automatically: the user starts it again.</summary>
public sealed class DocumentJob(DocumentGenerationService service)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid jobId, CancellationToken ct) => service.ExecuteAsync(jobId, ct);
}

public sealed class HangfireJobScheduler(IBackgroundJobClient client) : IJobScheduler
{
    public void EnqueueTranscription(Guid entryId) => client.Enqueue<TranscriptionJob>(j => j.RunAsync(entryId, CancellationToken.None));
    public void EnqueueImageRead(Guid entryId) => client.Enqueue<ImageReadJob>(j => j.RunAsync(entryId, CancellationToken.None));
    public void EnqueueGuide(Guid jobId) => client.Enqueue<GuideJob>(j => j.RunAsync(jobId, CancellationToken.None));
    public void EnqueueAnalysis(Guid runId) => client.Enqueue<AnalysisJob>(j => j.RunAsync(runId, CancellationToken.None));
    public void EnqueueDocument(Guid jobId) => client.Enqueue<DocumentJob>(j => j.RunAsync(jobId, CancellationToken.None));
}

/// <summary>Runs jobs on the thread pool in a fresh DI scope. Used when no job server is available, and by the tests.</summary>
public sealed class InlineJobScheduler(IServiceScopeFactory scopes, ILogger<InlineJobScheduler> log) : IJobScheduler
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public void EnqueueTranscription(Guid entryId) => Run(sp => sp.GetRequiredService<TranscriptionService>().ProcessAsync(entryId, CancellationToken.None), "transcription");
    public void EnqueueImageRead(Guid entryId) => Run(sp => sp.GetRequiredService<TranscriptionService>().ProcessImageAsync(entryId, CancellationToken.None), "photo reading");
    public void EnqueueGuide(Guid jobId) => Run(sp => sp.GetRequiredService<GuideGenerationService>().ExecuteAsync(jobId, CancellationToken.None), "guide");
    public void EnqueueAnalysis(Guid runId) => Run(sp => sp.GetRequiredService<AnalysisRunner>().ExecuteAsync(runId, CancellationToken.None), "analysis");
    public void EnqueueDocument(Guid jobId) => Run(sp => sp.GetRequiredService<DocumentGenerationService>().ExecuteAsync(jobId, CancellationToken.None), "document");

    /// <summary>Completes when every job started so far has finished (tests await this).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Values.ToArray());

    private void Run(Func<IServiceProvider, Task> work, string name)
    {
        var id = Guid.NewGuid();
        _running[id] = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await work(scope.ServiceProvider);
            }
            catch (Exception e)
            {
                log.LogError(e, "Inline {Job} job failed", name);
            }
            finally { _running.TryRemove(id, out _); }
        });
    }
}
