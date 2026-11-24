using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Api.Infrastructure;

/// <summary>
/// With an Idempotency-Key header (a GUID), the first successful response is stored per user and replayed for repeats of the same
/// request, so a client that lost the response can retry without creating a duplicate.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IAsyncActionFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var header = context.HttpContext.Request.Headers[ApiHeaders.IdempotencyKey].FirstOrDefault();
        var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (header is null || user.UserId is not { } uid) { await next(); return; }

        if (!Guid.TryParse(header, out var key))
        {
            context.Result = new BadRequestObjectResult(new ProblemDetails { Status = 400, Title = "Idempotency-Key must be a GUID." });
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<IAppDbContext>();
        var clock = context.HttpContext.RequestServices.GetRequiredService<IClock>();
        var path = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

        var stored = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key && r.UserId == uid);
        if (stored is not null)
        {
            if (stored.Path != path)
            {
                context.Result = new UnprocessableEntityObjectResult(new ProblemDetails
                {
                    Status = 422, Title = "This Idempotency-Key was already used for a different request.",
                });
                return;
            }
            context.HttpContext.Response.Headers["Idempotent-Replay"] = "true";
            context.Result = new ContentResult { StatusCode = stored.StatusCode, Content = stored.ResponseBody ?? "", ContentType = "application/json" };
            return;
        }

        var executed = await next();
        if (executed.Exception is not null || executed.Result is not ObjectResult { Value: not null } ok || ok.StatusCode is not (>= 200 and < 300 or null))
            return;

        try
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key, UserId = uid, Path = path, StatusCode = ok.StatusCode ?? 200,
                ResponseBody = JsonSerializer.Serialize(ok.Value, Json), CreatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A parallel request with the same key stored its response first. The action already ran, so just return its result.
        }
    }
}
