using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Ai;

/// <summary>
/// The flow analysis with Gemini. The model is asked for one JSON object that follows the flow schema; the server validates everything
/// it returns, and the caller asks once more when something is off, exactly as with Claude.
/// </summary>
public sealed partial class GeminiFlowAnalysisService(GeminiClient client) : IFlowAnalysisService
{
    private const string Footer = "\n\nAnswer with the JSON object only. No explanation before or after it, and no code fences.";

    public async Task<AiCallResult> CallAsync(AiCallRequest request, CancellationToken ct)
    {
        // The model name in the request is the Claude one from the analysis settings; Gemini uses its own setting.
        var reply = await client.GenerateAsync(new GeminiRequest(
            request.SystemPrompt + Footer, [GeminiPart.OfText(request.UserMessage)], request.InputSchema, JsonOutput: true,
            MaxOutputTokens: Math.Max(request.MaxTokens, 32000), Temperature: 0.2, Thinking: request.Careful ? "medium" : "low", TimeoutSeconds: 120), ct);

        var json = Clean(reply.Text);
        if (JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true }) is not JsonObject)
            throw new AiServiceException("ai_no_tool_call", "The AI did not return a structured result.", retryable: true);
        return new AiCallResult(json, reply.InputTokens, 0, 0, reply.OutputTokens, reply.FinishReason, reply.Model, reply.DurationMs);
    }

    /// <summary>Removes the ```json fences some models add around an answer.</summary>
    internal static string Clean(string text)
    {
        var t = text.Trim();
        var m = Fence().Match(t);
        if (m.Success) t = m.Groups[1].Value.Trim();
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }

    [GeneratedRegex(@"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Fence();
}

/// <summary>Voice notes to text with Gemini's audio understanding. Meant for short recordings: a few minutes each.</summary>
public sealed class GeminiSpeechToText(GeminiClient client) : ISpeechToTextService
{
    private const long MaxBytes = 14 * 1024 * 1024;       // the request, with the audio written as text, must stay under about 20 MB

    public async Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await audio.CopyToAsync(ms, ct);
        if (ms.Length > MaxBytes)
            throw new SttException("This recording is too long to transcribe in one go. Record shorter notes (up to about 7 minutes each).", retryable: false);

        try
        {
            var reply = await client.GenerateAsync(new GeminiRequest(
                "You are a careful transcriber for hospital software requirement interviews.",
                [
                    GeminiPart.OfText("Transcribe this recording exactly as spoken. Keep the language the speakers used, including English mixed with Tamil or Hindi, " +
                                      "and write Tamil and Hindi words in their own script. Do not translate, summarise or add comments. " +
                                      "If there is no speech, answer with exactly: NO_SPEECH"),
                    GeminiPart.OfFile(MimeFor(contentType), ms.ToArray()),
                ], MaxOutputTokens: 16000, Temperature: 0, LowThinking: true, TimeoutSeconds: 150), ct);

            var text = reply.Text.Trim();
            if (text.Length == 0 || text.Equals("NO_SPEECH", StringComparison.OrdinalIgnoreCase))
                throw new SttException("No speech was detected in the recording.", retryable: false);
            return new SttResult(text, candidateLocales.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "en-IN", null, null, "gemini",
                reply.InputTokens, reply.OutputTokens, reply.Model);
        }
        catch (AiServiceException e)
        {
            throw new SttException(e.Message, e.Retryable);
        }
    }

    internal static string MimeFor(string contentType)
    {
        var t = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return t switch { "audio/x-wav" or "audio/wave" => "audio/wav", "audio/mp3" => "audio/mpeg", "audio/x-m4a" or "audio/m4a" => "audio/mp4", "" or "application/octet-stream" => "audio/wav", _ => t };
    }
}

/// <summary>Photos of handwritten or printed notes to text with Gemini.</summary>
public sealed class GeminiImageText(GeminiClient client) : IImageTextService
{
    public async Task<ImageTextResult> ReadAsync(Stream image, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, ct);
        var reply = await client.GenerateAsync(new GeminiRequest(
            "You read photos of handwritten or printed notes taken during hospital software requirement interviews.",
            [
                GeminiPart.OfText("Write out all the text in this image exactly as written, keeping the order, line breaks, bullet points and numbering. " +
                                  "Keep the language used, including Tamil or Hindi in their own script. " +
                                  "If there is a drawing or a table, describe it briefly in [square brackets]. Do not add comments of your own. " +
                                  "If the image has no readable text, answer with exactly: NO_TEXT"),
                GeminiPart.OfFile(contentType, ms.ToArray()),
            ], MaxOutputTokens: 8000, Temperature: 0, LowThinking: true, TimeoutSeconds: 90), ct);

        var text = reply.Text.Trim();
        if (text.Length == 0 || text.Equals("NO_TEXT", StringComparison.OrdinalIgnoreCase))
            throw new SttException("No readable text was found in the photo. Try a clearer, well lit photo.", retryable: false);
        return new ImageTextResult(text, "gemini-vision", reply.InputTokens, reply.OutputTokens, reply.Model);
    }
}

/// <summary>Stand-ins for demos without a key: the same fixed text every time, so the whole journey can be shown.</summary>
public static class DemoText
{
    public const string Note =
        "The MRD user opens the IP Case Sheet Receive menu and picks the ward. The system lists the case sheets sent from that ward. " +
        "The user checks each sheet against the list, scans it and saves. If a sheet has missing pages the system shows a warning and the sheet stays pending. " +
        "A super user sees his own ward first. Received sheets go to the billing department.";
}

public sealed class DemoSpeechToText : ISpeechToTextService
{
    public Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct) =>
        Task.FromResult(new SttResult("(Demo transcript) " + DemoText.Note, candidateLocales.FirstOrDefault() ?? "en-IN", null, null, "demo"));
}

public sealed class DemoImageText : IImageTextService
{
    public Task<ImageTextResult> ReadAsync(Stream image, string contentType, CancellationToken ct) =>
        Task.FromResult(new ImageTextResult("(Demo reading of the photo) " + DemoText.Note, "demo"));
}

// ---------------------------------------------------------------------- choosing the service

/// <summary>Sends each call to the AI service the administrator chose (Gemini, Anthropic or Demo), so a change in the dashboard needs no restart.</summary>
public sealed class AiProviderRouter(ISettingsService settings, GeminiFlowAnalysisService gemini, AnthropicFlowAnalysisService anthropic, DemoFlowAnalysisService demo) : IFlowAnalysisService
{
    public async Task<AiCallResult> CallAsync(AiCallRequest request, CancellationToken ct) =>
        (await settings.GetProviderAsync(ct)).Provider switch
        {
            "Anthropic" => await anthropic.CallAsync(request, ct),
            "Demo" => await demo.CallAsync(request, ct),
            _ => await gemini.CallAsync(request, ct),
        };
}

/// <summary>
/// Voice notes: Groq (Whisper, its own free allowance) when a Groq key is saved and chosen; when Groq is busy or refuses, and a Gemini key exists,
/// the same recording goes to Gemini instead, so a note is never lost to one account running out. Without a Groq key, Gemini does it, as before.
/// </summary>
public sealed class SpeechToTextRouter(ISettingsService settings, GeminiSpeechToText gemini, GroqSpeechToText groq, DemoSpeechToText demo, IServiceProvider services, IConfiguration config,
    Microsoft.Extensions.Logging.ILogger<SpeechToTextRouter> log) : ISpeechToTextService
{
    public async Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct)
    {
        if ((config["Stt:Provider"] ?? "None").Equals("Azure", StringComparison.OrdinalIgnoreCase))
            return await ((ISpeechToTextService)services.GetService(typeof(Speech.AzureSpeechToTextService))!).TranscribeAsync(audio, contentType, fileName, candidateLocales, ct);

        var provider = await settings.GetProviderAsync(ct);
        if (provider.Provider == "Demo") return await demo.TranscribeAsync(audio, contentType, fileName, candidateLocales, ct);

        var geminiReady = provider.Provider == "Gemini" && !string.IsNullOrWhiteSpace(provider.GeminiApiKey);
        var g = await settings.GetGroqAsync(ct);
        if (g.UseForVoice)
        {
            // the recording is kept in memory so the second AI can be given the same one
            var ms = new MemoryStream();
            await audio.CopyToAsync(ms, ct);
            try
            {
                ms.Position = 0;
                return await groq.TranscribeAsync(ms, contentType, fileName, candidateLocales, ct);
            }
            catch (GroqUnavailableException e)
            {
                if (!geminiReady) throw new SttException(e.Message, e.Retryable);
                log.LogWarning("Groq could not transcribe ({Reason}); using Gemini for this recording", e.Message);
                ms.Position = 0;
                return await gemini.TranscribeAsync(ms, contentType, fileName, candidateLocales, ct);
            }
        }
        if (provider.Provider == "Gemini") return await gemini.TranscribeAsync(audio, contentType, fileName, candidateLocales, ct);
        throw new SttException("Voice notes need the free Gemini AI, or a free Groq key, to be set up in the Admin dashboard.", retryable: false);
    }
}

public sealed class ImageTextRouter(ISettingsService settings, GeminiImageText gemini, DemoImageText demo) : IImageTextService
{
    public async Task<ImageTextResult> ReadAsync(Stream image, string contentType, CancellationToken ct) =>
        (await settings.GetProviderAsync(ct)).Provider switch
        {
            "Demo" => await demo.ReadAsync(image, contentType, ct),
            "Gemini" => await gemini.ReadAsync(image, contentType, ct),
            _ => throw new SttException("Reading photos needs the free Gemini AI to be switched on in the Admin dashboard.", retryable: false),
        };
}

public sealed class AiStatusService(ISettingsService settings, AnthropicFlowAnalysisService anthropic) : IAiStatusService
{
    public async Task<AiStatus> GetAsync(CancellationToken ct = default)
    {
        var s = await settings.GetProviderAsync(ct);
        return s.Provider switch
        {
            "Demo" => new AiStatus("demo", true, "AI is in demo mode: analyses return a fixed example, not a real analysis of your notes."),
            "Anthropic" => anthropic.HasKey ? new AiStatus("anthropic", true, null) : new AiStatus("no-key", false, "The AI key is not set on the server, so analysis will not work yet."),
            _ => string.IsNullOrWhiteSpace(s.GeminiApiKey)
                ? new AiStatus("no-key", false, "The AI is not set up yet. An administrator must add the free Gemini key in the Admin dashboard.")
                : new AiStatus("gemini", true, null),
        };
    }
}
