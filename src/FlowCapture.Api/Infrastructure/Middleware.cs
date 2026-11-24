using System.Text.Json;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog.Context;

namespace FlowCapture.Api.Infrastructure;

/// <summary>Reads or creates X-Correlation-Id, returns it, and attaches it to every log line of the request.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[ApiHeaders.CorrelationId].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 60) id = Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.Headers[ApiHeaders.CorrelationId] = id;
        using (LogContext.PushProperty("CorrelationId", id))
            await next(context);
    }
}

/// <summary>
/// Headers that stop browsers from guessing types or framing the API, and stop proxies and browsers from keeping responses that can hold
/// project data or documents. Swagger (development) is left alone because it needs scripts and caching.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>The web app: only its own scripts, styles and images (and pictures made in the browser), talking only to this server.</summary>
    internal const string AppPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
        "connect-src 'self'; media-src 'self' blob:; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/health") || path.StartsWithSegments("/hangfire");

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            var path = context.Request.Path;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            if (path.StartsWithSegments("/swagger") || path.StartsWithSegments("/hangfire")) return Task.CompletedTask;   // their pages need scripts and caching
            h["X-Frame-Options"] = "DENY";
            if (IsApiPath(path))
            {
                h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                if (!h.ContainsKey("Cache-Control")) h["Cache-Control"] = "no-store";
            }
            else
            {
                h["Content-Security-Policy"] = AppPolicy;
                // files with a hash in their name never change; everything else (the page, the service worker, the manifest) is checked each time
                if (!h.ContainsKey("Cache-Control"))
                    h["Cache-Control"] = path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>Turns exceptions into RFC 7807 problem responses. Details of unexpected errors are logged, never returned.</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted) throw;
            await WriteAsync(context, ex);
        }
    }

    private async Task WriteAsync(HttpContext context, Exception ex)
    {
        var problem = new ProblemDetails { Instance = context.Request.Path };
        problem.Extensions["traceId"] = context.Items[CorrelationIdMiddleware.ItemKey] ?? context.TraceIdentifier;

        switch (ex)
        {
            case ValidationException v:
                problem.Status = 400; problem.Title = "The request is not valid.";
                problem.Extensions["errors"] = v.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
                break;
            case NotFoundException:
                problem.Status = 404; problem.Title = "Not found."; problem.Detail = ex.Message;
                break;
            case ForbiddenException:
                problem.Status = 403; problem.Title = "Not allowed."; problem.Detail = ex.Message;
                break;
            case UnauthorizedAccessException:
                problem.Status = 401; problem.Title = "Authentication failed."; problem.Detail = ex.Message;
                break;
            case ConflictException c:
                problem.Status = 409; problem.Title = "Conflict."; problem.Detail = c.Message;
                if (c.ServerCopy is not null) problem.Extensions["serverCopy"] = c.ServerCopy;
                break;
            case DbUpdateConcurrencyException:
                problem.Status = 409; problem.Title = "Conflict."; problem.Detail = "The data was changed by someone else. Reload and try again.";
                break;
            case AiBudgetExceededException b:
                problem.Status = 429; problem.Title = "AI limit reached."; problem.Detail = b.Message; problem.Extensions["code"] = b.Code;
                break;
            case DependencyUnavailableException u:
                log.LogError(u, "A required tool is not available");
                problem.Status = 503; problem.Title = "A required tool is not available on the server."; problem.Detail = u.Message;
                break;
            case DomainException d:
                problem.Status = 422; problem.Title = "The request cannot be completed."; problem.Detail = d.Message; problem.Extensions["code"] = d.Code;
                break;
            case BadHttpRequestException or JsonException:
                problem.Status = 400; problem.Title = "The request could not be read."; problem.Detail = "The request body is missing or is not valid JSON.";
                break;
            case FileNotFoundException:
                problem.Status = 404; problem.Title = "Not found."; problem.Detail = "The stored file was not found.";
                break;
            default:
                log.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                problem.Status = 500; problem.Title = "Something went wrong."; problem.Detail = "The error has been logged. Quote the trace id when you report it.";
                break;
        }
        problem.Type = $"https://flowcapture.local/errors/{problem.Status}";
        context.Response.StatusCode = problem.Status!.Value;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
