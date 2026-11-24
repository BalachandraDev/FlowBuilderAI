using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FlowCapture.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Speech;

public sealed class SttOptions
{
    public const string Section = "Stt";
    /// <summary>Azure | None</summary>
    public string Provider { get; set; } = "None";
    public string? Region { get; set; }
    public string? Endpoint { get; set; }
    public string? Key { get; set; }
    public string ApiVersion { get; set; } = "2024-11-15";
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>Used when no speech provider is configured: recordings upload fine but transcription reports a clear error.</summary>
public sealed class NotConfiguredSpeechToText : ISpeechToTextService
{
    public Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct) =>
        throw new SttException("Speech-to-text is not configured. Ask an administrator to set it up.", retryable: false);
}

/// <summary>
/// Azure AI Speech "fast transcription" REST API. The candidate locales (the entry's language first, then the other active
/// languages) let the service pick the spoken language. Code-mixed speech quality must be verified on real recordings.
/// </summary>
public sealed class AzureSpeechToTextService(HttpClient http, IOptions<SttOptions> options) : ISpeechToTextService
{
    private const int MaxLocales = 8;

    public async Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Key) || (string.IsNullOrWhiteSpace(o.Region) && string.IsNullOrWhiteSpace(o.Endpoint)))
            throw new SttException("Azure Speech is not configured (region and key are required).", retryable: false);

        var baseUrl = !string.IsNullOrWhiteSpace(o.Endpoint) ? o.Endpoint!.TrimEnd('/') : $"https://{o.Region}.api.cognitive.microsoft.com";
        var url = $"{baseUrl}/speechtotext/transcriptions:transcribe?api-version={o.ApiVersion}";

        var locales = candidateLocales.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxLocales).ToArray();
        var definition = JsonSerializer.Serialize(new { locales });

        using var form = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(audioContent, "audio", fileName);
        form.Add(new StringContent(definition, System.Text.Encoding.UTF8, "application/json"), "definition");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Add("Ocp-Apim-Subscription-Key", o.Key);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new SttException("The speech service could not be reached: " + e.Message, retryable: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SttException("The speech service timed out.", retryable: true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                throw new SttException($"The speech service returned {(int)response.StatusCode}.", retryable);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var text = root.TryGetProperty("combinedPhrases", out var combined) && combined.GetArrayLength() > 0
                ? string.Join(" ", combined.EnumerateArray().Select(p => p.GetProperty("text").GetString()).Where(t => !string.IsNullOrWhiteSpace(t)))
                : "";
            if (string.IsNullOrWhiteSpace(text))
                throw new SttException("No speech was detected in the recording.", retryable: false);

            double? confidence = null;
            string locale = locales.FirstOrDefault() ?? "en-IN";
            var segments = new List<object>();
            if (root.TryGetProperty("phrases", out var phrases))
            {
                var scores = new List<double>();
                foreach (var p in phrases.EnumerateArray())
                {
                    if (p.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cd)) scores.Add(cd);
                    if (p.TryGetProperty("locale", out var l) && l.GetString() is { Length: > 0 } ls) locale = ls;
                    segments.Add(new
                    {
                        offsetMs = p.TryGetProperty("offsetMilliseconds", out var off) ? off.GetInt64() : 0,
                        durationMs = p.TryGetProperty("durationMilliseconds", out var dur) ? dur.GetInt64() : 0,
                        locale = p.TryGetProperty("locale", out var loc) ? loc.GetString() : null,
                        confidence = p.TryGetProperty("confidence", out var cf) && cf.TryGetDouble(out var cfd) ? (double?)cfd : null,
                    });
                }
                if (scores.Count > 0) confidence = Math.Round(scores.Average(), 3);
            }
            return new SttResult(text.Trim(), locale, confidence, JsonSerializer.Serialize(segments), "azure-fast-transcription");
        }
    }
}
