using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using FlowCapture.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Ai;

public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";
    /// <summary>Normally supplied through the ANTHROPIC_API_KEY environment variable or a secret store, never committed.</summary>
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 2;
}

/// <summary>
/// Calls Claude through the official SDK. The model is asked to answer by calling one tool whose input schema is the
/// flow (or project) schema. Forced tool choice is not supported on current models, so the tool choice is "auto", the
/// prompt tells the model to call the tool, and a response without the tool call is reported as an error.
/// The server validates everything the model returns, so a schema slip here is retried once by the caller.
/// </summary>
public sealed class AnthropicFlowAnalysisService : IFlowAnalysisService
{
    private readonly AnthropicClient _client;
    private readonly ILogger<AnthropicFlowAnalysisService> _log;

    public AnthropicFlowAnalysisService(IOptions<AnthropicOptions> options, ILogger<AnthropicFlowAnalysisService> log)
    {
        _log = log;
        var o = options.Value;
        var key = !string.IsNullOrWhiteSpace(o.ApiKey) ? o.ApiKey : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _client = string.IsNullOrWhiteSpace(o.BaseUrl)
            ? new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds), MaxRetries = o.MaxRetries }
            : new AnthropicClient { ApiKey = key, BaseUrl = o.BaseUrl, Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds), MaxRetries = o.MaxRetries };
        HasKey = !string.IsNullOrWhiteSpace(key);
    }

    public bool HasKey { get; }

    public async Task<AiCallResult> CallAsync(AiCallRequest request, CancellationToken ct)
    {
        if (!HasKey)
            throw new AiServiceException("ai_not_configured", "The AI service is not configured. Ask an administrator to set the Anthropic API key.");

        var sw = Stopwatch.StartNew();
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            // The system prompt is identical for every call of a task, so it is cached.
            System = new List<TextBlockParam> { new() { Text = request.SystemPrompt, CacheControl = new CacheControlEphemeral() } },
            Messages = [new UserMessageParam { Content = request.UserMessage }],
            Tools = [BuildTool(request)],
            ToolChoice = new ToolChoiceAuto(),
        };
        if (ParseEffort(request.Effort) is { } effort) parameters = parameters with { OutputConfig = new OutputConfig { Effort = effort } };
        if (request.DisableThinking) parameters = parameters with { Thinking = new ThinkingConfigBetweenTools() };

        Message response;
        try
        {
            response = await _client.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException e)
        {
            _log.LogWarning(e, "Anthropic rate limit");
            throw new AiServiceException("ai_rate_limited", "The AI service is busy. Try again in a few minutes.", retryable: true);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new AiServiceException("ai_auth", "The AI service rejected the API key. Ask an administrator to check it.");
        }
        catch (AnthropicBadRequestException e)
        {
            _log.LogError(e, "Anthropic rejected the request");
            throw new AiServiceException("ai_bad_request", "The AI service rejected the request. Check the model name and settings.");
        }
        catch (Anthropic5xxException e)
        {
            _log.LogWarning(e, "Anthropic server error");
            throw new AiServiceException("ai_unavailable", "The AI service is temporarily unavailable. Try again later.", retryable: true);
        }
        catch (AnthropicApiException e)
        {
            _log.LogError(e, "Anthropic call failed");
            throw new AiServiceException("ai_error", "The AI call failed.");
        }

        var stop = response.StopReason?.ToString() ?? "";
        if (stop.Equals("refusal", StringComparison.OrdinalIgnoreCase))
            throw new AiServiceException("ai_refusal", "The AI declined to process this input. Review the text and try again, or contact an administrator.");

        string? toolInput = null;
        foreach (var block in response.Content)
            if (block.TryPickToolUse(out ToolUseBlock? use) && use.Name == request.ToolName)
            {
                toolInput = JsonSerializer.Serialize(use.Input);
                break;
            }

        if (toolInput is null)
            throw new AiServiceException(stop.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) ? "ai_truncated" : "ai_no_tool_call",
                stop.Equals("max_tokens", StringComparison.OrdinalIgnoreCase)
                    ? "The AI output was cut off before it finished. The flow may be too large for one analysis."
                    : "The AI did not return a structured result.", retryable: true);

        sw.Stop();
        var u = response.Usage;
        return new AiCallResult(toolInput, (int)u.InputTokens, (int)(u.CacheCreationInputTokens ?? 0), (int)(u.CacheReadInputTokens ?? 0),
            (int)u.OutputTokens, stop, response.Model.ToString(), (int)sw.ElapsedMilliseconds);
    }

    private static Tool BuildTool(AiCallRequest request)
    {
        var properties = new Dictionary<string, JsonElement>();
        if (request.InputSchema["properties"] is JsonObject props)
            foreach (var (name, schema) in props)
                properties[name] = JsonSerializer.SerializeToElement(schema);
        var required = (request.InputSchema["required"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
        return new Tool
        {
            Name = request.ToolName,
            Description = request.ToolDescription,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static Effort? ParseEffort(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => null,
    };
}
