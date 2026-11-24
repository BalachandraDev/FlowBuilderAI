using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using FlowCapture.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlowCapture.UnitTests;

/// <summary>Voice notes on Groq's own free allowance, with Gemini as the safety net. No network: every answer is made up here.</summary>
public class GroqTests
{
    private sealed record Seen(string Url, string? Authorization, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // the recording is binary: only the text parts of the form are looked at
            var body = request.Content is null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(ct));
            Calls.Add(new Seen(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    private sealed class Settings(string? groqKey, string provider = "Gemini", string? geminiKey = "gem-key", bool useForVoice = true) : ISettingsService
    {
        public Task<GroqSettings> GetGroqAsync(CancellationToken ct = default) => Task.FromResult(new GroqSettings(groqKey, "whisper-test", groqKey is not null && useForVoice, groqKey is not null));
        public Task<AiProviderSettings> GetProviderAsync(CancellationToken ct = default) => Task.FromResult(new AiProviderSettings(provider, geminiKey, "gemini-9-flash", true));
        public Task<AiSettings> GetAiAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetNameDictionaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetAudioMaxMinutesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> GetFlagAsync(string key, bool fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAsync(string k, string v, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSecretAsync(string k, string? v, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GroqClient Client, FakeHandler Handler) MakeGroq(Func<HttpRequestMessage, HttpResponseMessage> respond, ISettingsService settings)
    {
        var handler = new FakeHandler(respond);
        return (new GroqClient(new HttpClient(handler) { BaseAddress = new Uri("https://groq.test/openai/v1/") }, settings, NullLogger<GroqClient>.Instance), handler);
    }

    private static string GeminiAnswer(string text) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }, ["finishReason"] = "STOP" }),
        ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 50, ["candidatesTokenCount"] = 20 },
    }.ToJsonString();

    private static SpeechToTextRouter Router(ISettingsService settings, Func<HttpRequestMessage, HttpResponseMessage> groqAnswer, Func<HttpRequestMessage, HttpResponseMessage> geminiAnswer, out FakeHandler groqHandler, out FakeHandler geminiHandler)
    {
        var (groqClient, gh) = MakeGroq(groqAnswer, settings);
        groqHandler = gh;
        geminiHandler = new FakeHandler(geminiAnswer);
        var geminiClient = new GeminiClient(new HttpClient(geminiHandler) { BaseAddress = new Uri("https://gemini.test/") }, settings, NullLogger<GeminiClient>.Instance) { Delay = (_, _) => Task.CompletedTask };
        return new SpeechToTextRouter(settings, new GeminiSpeechToText(geminiClient), new GroqSpeechToText(groqClient), new DemoSpeechToText(), null!, new ConfigurationBuilder().Build(), NullLogger<SpeechToTextRouter>.Instance);
    }

    private static MemoryStream Recording() => new([1, 2, 3, 4, 5, 6, 7, 8]);

    [Fact]
    public async Task A_recording_is_sent_to_Whisper_with_the_key_in_the_header_only_and_the_text_comes_back()
    {
        var (client, handler) = MakeGroq(_ =>
        {
            var res = Json(HttpStatusCode.OK, """{"text":"  The nurse opens the discharge menu. "}""");
            res.Headers.Add("x-ratelimit-limit-requests", "2000"); res.Headers.Add("x-ratelimit-remaining-requests", "1999"); res.Headers.Add("x-ratelimit-reset-requests", "43s");
            return res;
        }, new Settings("gsk_secret_value"));

        var result = await new GroqSpeechToText(client).TranscribeAsync(Recording(), "audio/wav", "note.wav", ["en-IN"], default);

        result.Text.Should().Be("The nurse opens the discharge menu.");
        result.Provider.Should().Be("groq");
        result.Model.Should().Be("whisper-test");
        result.Locale.Should().Be("en-IN");
        var call = handler.Calls.Single();
        call.Url.Should().Be("https://groq.test/openai/v1/audio/transcriptions");
        call.Authorization.Should().Be("Bearer gsk_secret_value");
        call.Body.Should().Contain("whisper-test", "the model is named in the form").And.NotContain("gsk_secret_value", "the key travels in the header, never in the body");
        GroqClient.Limits["whisper-test"].RequestsRemaining.Should().Be(1999, "what Groq says is left is remembered for the admin page");
    }

    [Fact]
    public async Task A_refused_key_a_used_up_allowance_and_a_busy_service_are_told_apart_and_never_show_the_key()
    {
        foreach (var (status, retryable, keyProblem, words) in new[]
                 {
                     (HttpStatusCode.Unauthorized, false, true, "key was not accepted"),
                     (HttpStatusCode.TooManyRequests, true, false, "allowance is used up"),
                     (HttpStatusCode.BadGateway, true, false, "busy or unavailable"),
                 })
        {
            var (client, _) = MakeGroq(_ => Json(status, """{"error":{"message":"nope"}}"""), new Settings("gsk_secret_value"));
            var ex = await client.Invoking(c => c.TranscribeAsync([1, 2], "audio/wav", "a.wav", default)).Should().ThrowAsync<GroqUnavailableException>();
            ex.Which.Retryable.Should().Be(retryable);
            ex.Which.KeyProblem.Should().Be(keyProblem);
            ex.Which.Message.Should().Contain(words).And.NotContain("gsk_secret_value");
        }
    }

    [Fact]
    public async Task A_recording_with_no_speech_is_reported_as_such_and_not_sent_anywhere_else()
    {
        var router = Router(new Settings("gsk_x"), _ => Json(HttpStatusCode.OK, """{"text":""}"""), _ => Json(HttpStatusCode.OK, GeminiAnswer("should not be asked")), out _, out var gemini);
        var ex = await router.Invoking(r => r.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default)).Should().ThrowAsync<SttException>();
        ex.Which.Message.Should().Contain("No speech");
        gemini.Calls.Should().BeEmpty("a silent recording would be silent for Gemini too: asking again only wastes its allowance");
    }

    [Fact]
    public async Task With_a_Groq_key_the_voice_note_is_done_by_Groq_and_Gemini_is_not_touched()
    {
        var router = Router(new Settings("gsk_x"), _ => Json(HttpStatusCode.OK, """{"text":"Hello ward."}"""), _ => Json(HttpStatusCode.OK, GeminiAnswer("unused")), out var groq, out var gemini);
        var result = await router.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default);
        result.Provider.Should().Be("groq");
        groq.Calls.Should().HaveCount(1);
        gemini.Calls.Should().BeEmpty("the Gemini allowance is kept for the analysis and the photos");
    }

    [Fact]
    public async Task When_Groq_is_busy_the_same_recording_goes_to_Gemini()
    {
        var router = Router(new Settings("gsk_x"), _ => Json(HttpStatusCode.TooManyRequests, "{}"), _ => Json(HttpStatusCode.OK, GeminiAnswer("The doctor signs.")), out var groq, out var gemini);
        var result = await router.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default);
        result.Provider.Should().Be("gemini");
        result.Text.Should().Be("The doctor signs.");
        groq.Calls.Should().HaveCount(1);
        gemini.Calls.Should().HaveCount(1);
        gemini.Calls[0].Body.Should().Contain("AQIDBAUGBwg=", "it is the very same recording (base64 of bytes 1..8)");
    }

    [Fact]
    public async Task Without_a_Groq_key_or_when_Gemini_is_chosen_for_voice_Gemini_does_it_as_before()
    {
        var noKey = Router(new Settings(null), _ => Json(HttpStatusCode.OK, """{"text":"x"}"""), _ => Json(HttpStatusCode.OK, GeminiAnswer("From Gemini.")), out var groq1, out _);
        (await noKey.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default)).Provider.Should().Be("gemini");
        groq1.Calls.Should().BeEmpty();

        var chosen = Router(new Settings("gsk_x", useForVoice: false), _ => Json(HttpStatusCode.OK, """{"text":"x"}"""), _ => Json(HttpStatusCode.OK, GeminiAnswer("From Gemini.")), out var groq2, out _);
        (await chosen.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default)).Provider.Should().Be("gemini");
        groq2.Calls.Should().BeEmpty("the administrator chose Gemini for voice");
    }

    [Fact]
    public async Task Groq_alone_is_enough_for_voice_and_a_failure_without_Gemini_is_explained_and_marked_for_retry()
    {
        var router = Router(new Settings("gsk_x", provider: "Anthropic", geminiKey: null), _ => Json(HttpStatusCode.TooManyRequests, "{}"), _ => Json(HttpStatusCode.OK, GeminiAnswer("unused")), out _, out var gemini);
        var ex = await router.Invoking(r => r.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default)).Should().ThrowAsync<SttException>();
        ex.Which.Retryable.Should().BeTrue();
        ex.Which.Message.Should().Contain("allowance is used up");
        gemini.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_key_is_proved_by_listing_models_which_spends_none_of_the_allowance()
    {
        var (client, handler) = MakeGroq(_ => Json(HttpStatusCode.OK, """{"data":[{"id":"a"},{"id":"b"},{"id":"c"}]}"""), new Settings("gsk_x"));
        (await client.CountModelsAsync(default)).Should().Be(3);
        handler.Calls.Single().Url.Should().Be("https://groq.test/openai/v1/models");
    }

    // ------------------------------------------------------------------ the daily free allowance

    private sealed class Quota(params string[] full) : IAiQuota
    {
        public List<string> Asked { get; } = [];
        public Task<AiQuota> GetAsync(string provider, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiQuota>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureAvailableAsync(string provider, CancellationToken ct = default)
        {
            Asked.Add(provider);
            return full.Contains(provider) ? throw new AiServiceException("ai_quota_reached", $"The daily free limit for {provider} is reached.", false) : Task.CompletedTask;
        }
    }

    [Fact]
    public void Each_AIs_day_ends_where_its_provider_says_Gemini_at_midnight_Pacific_and_Groq_at_midnight_UTC()
    {
        var (gStart, gEnd) = AiQuotaService.DayOf("Gemini", new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc));      // 02:00 in Pacific standard time
        gStart.Should().Be(new DateTime(2026, 11, 18, 8, 0, 0)); gEnd.Should().Be(new DateTime(2026, 11, 19, 8, 0, 0));
        var (lStart, lEnd) = AiQuotaService.DayOf("Gemini", new DateTime(2026, 11, 18, 5, 0, 0, DateTimeKind.Utc));       // still the 17th in Pacific time
        lStart.Should().Be(new DateTime(2026, 11, 17, 8, 0, 0)); lEnd.Should().Be(new DateTime(2026, 11, 18, 8, 0, 0));
        var (summerStart, _) = AiQuotaService.DayOf("Gemini", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));       // daylight saving: 7 hours behind
        summerStart.Should().Be(new DateTime(2026, 7, 1, 7, 0, 0));
        var (qStart, qEnd) = AiQuotaService.DayOf("Groq", new DateTime(2026, 11, 18, 23, 59, 0, DateTimeKind.Utc));
        qStart.Should().Be(new DateTime(2026, 11, 18)); qEnd.Should().Be(new DateTime(2026, 11, 19));
    }

    [Fact]
    public void A_limit_is_reached_by_requests_or_by_tokens_and_the_message_says_how_much_and_when_it_renews()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var renews = now.AddHours(3).AddMinutes(26);
        new AiQuota("Gemini", 10, 900, null, null, renews, now).Reached.Should().BeFalse("no limit set: the app does not limit it");
        new AiQuota("Gemini", 999, 900, 1000, 1000, renews, now).Reached.Should().BeFalse();
        var byRequests = new AiQuota("Gemini", 1000, 10, 1000, null, renews, now);
        byRequests.Reached.Should().BeTrue();
        byRequests.Message.Should().Contain("Gemini").And.Contain("1,000 of 1,000 requests").And.Contain("3 h 26 min").And.Contain("AI setup page");
        var byTokens = new AiQuota("Groq", 5, 250_000, null, 250_000, now.AddMinutes(20), now);
        byTokens.Reached.Should().BeTrue();
        byTokens.Message.Should().Contain("250,000 of 250,000 tokens").And.Contain("in 20 min");
        new AiQuota("Groq", 5, 250_000, 0, 0, now, now).Reached.Should().BeFalse("0 means no limit");
    }

    [Fact]
    public async Task Gemini_is_not_asked_at_all_once_its_daily_limit_is_reached()
    {
        var quota = new Quota("Gemini");
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, GeminiAnswer("should not be asked")));
        var client = new GeminiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gemini.test/") }, new Settings(null), NullLogger<GeminiClient>.Instance, quota) { Delay = (_, _) => Task.CompletedTask };
        var ex = await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();
        ex.Which.Code.Should().Be("ai_quota_reached");
        ex.Which.Retryable.Should().BeFalse();
        handler.Calls.Should().BeEmpty("nothing is sent to an AI that is at its limit");
    }

    [Fact]
    public async Task Groq_is_not_asked_at_its_limit_and_the_recording_goes_to_Gemini_while_Gemini_has_room()
    {
        var quota = new Quota("Groq");
        var settings = new Settings("gsk_x");
        var groqHandler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"text":"should not be asked"}"""));
        var geminiHandler = new FakeHandler(_ => Json(HttpStatusCode.OK, GeminiAnswer("The doctor signs.")));
        var groq = new GroqSpeechToText(new GroqClient(new HttpClient(groqHandler) { BaseAddress = new Uri("https://groq.test/openai/v1/") }, settings, NullLogger<GroqClient>.Instance, quota));
        var gemini = new GeminiSpeechToText(new GeminiClient(new HttpClient(geminiHandler) { BaseAddress = new Uri("https://gemini.test/") }, settings, NullLogger<GeminiClient>.Instance, quota) { Delay = (_, _) => Task.CompletedTask });
        var router = new SpeechToTextRouter(settings, gemini, groq, new DemoSpeechToText(), null!, new ConfigurationBuilder().Build(), NullLogger<SpeechToTextRouter>.Instance);

        var result = await router.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default);
        result.Provider.Should().Be("gemini");
        groqHandler.Calls.Should().BeEmpty("Groq is at its limit");
        geminiHandler.Calls.Should().HaveCount(1);

        // both at their limit: the note says so, plainly, and nothing is sent anywhere
        var both = new Quota("Groq", "Gemini");
        var groqB = new GroqSpeechToText(new GroqClient(new HttpClient(groqHandler) { BaseAddress = new Uri("https://groq.test/openai/v1/") }, settings, NullLogger<GroqClient>.Instance, both));
        var geminiB = new GeminiSpeechToText(new GeminiClient(new HttpClient(geminiHandler) { BaseAddress = new Uri("https://gemini.test/") }, settings, NullLogger<GeminiClient>.Instance, both) { Delay = (_, _) => Task.CompletedTask });
        var router2 = new SpeechToTextRouter(settings, geminiB, groqB, new DemoSpeechToText(), null!, new ConfigurationBuilder().Build(), NullLogger<SpeechToTextRouter>.Instance);
        var ex = await router2.Invoking(r => r.TranscribeAsync(Recording(), "audio/wav", "a.wav", ["en-IN"], default)).Should().ThrowAsync<SttException>();
        ex.Which.Message.Should().Contain("daily free limit");
        ex.Which.Retryable.Should().BeFalse();
        geminiHandler.Calls.Should().HaveCount(1, "still only the earlier call");
    }
}