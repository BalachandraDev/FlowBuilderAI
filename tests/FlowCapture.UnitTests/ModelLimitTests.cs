using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using FlowCapture.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlowCapture.UnitTests;

/// <summary>Gemini's free limits are per model (requests a minute, tokens a minute, requests a day): the client counts every request and respects them. No network.</summary>
public class ModelLimitTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
        public int Generated => Urls.Count(u => u.Contains(":generateContent"));
    }

    private sealed class Settings(string key, string model = "") : ISettingsService
    {
        public Task<AiProviderSettings> GetProviderAsync(CancellationToken ct = default) => Task.FromResult(new AiProviderSettings("Gemini", key, model, true));
        public Task<GroqSettings> GetGroqAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AiSettings> GetAiAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetNameDictionaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetAudioMaxMinutesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> GetFlagAsync(string k, bool fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAsync(string k, string v, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSecretAsync(string k, string? v, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Answers with a scripted state per model and writes down what the client records.</summary>
    private sealed class FakeModelQuota(Func<string, ModelQuota> state) : IModelQuota
    {
        public List<(string Model, string Outcome, int In, int Out)> Recorded { get; } = [];
        public int Asked { get; private set; }
        public Task<IReadOnlyList<ModelQuota>> GetManyAsync(IEnumerable<string> models, CancellationToken ct = default) { Asked++; return Task.FromResult<IReadOnlyList<ModelQuota>>(models.Select(state).ToList()); }
        public Task<IReadOnlyList<ModelQuota>> GetKnownAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AiCapacity> GetCapacityAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordAsync(string provider, string model, string outcome, int inputTokens, int outputTokens, CancellationToken ct = default) { Recorded.Add((model, outcome, inputTokens, outputTokens)); return Task.CompletedTask; }
    }

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static ModelQuota Quota(string model, long used = 0, long perDay = 20, long usedMinute = 0, long perMinute = 5, long tokensMinute = 0, bool google = false, DateTime? oldest = null) =>
        new(model, used, usedMinute, tokensMinute, new ModelLimits(perDay, perMinute, 250_000, false), Now.AddHours(3), oldest ?? (usedMinute > 0 ? Now.AddSeconds(-20) : null), google, Now);

    private static string ModelList(params string[] ids) => new JsonObject { ["models"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["name"] = "models/" + id, ["displayName"] = id, ["supportedGenerationMethods"] = new JsonArray("generateContent") }).ToArray()) }.ToJsonString();
    private static string Answer(string text = "ok") => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }, ["finishReason"] = "STOP" }),
        ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 120, ["candidatesTokenCount"] = 30 },
    }.ToJsonString();
    private static HttpResponseMessage Json(HttpStatusCode s, string body) => new(s) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (GeminiClient Client, FakeHandler Handler, List<TimeSpan> Waits) Make(string key, FakeModelQuota quota, Func<HttpRequestMessage, HttpResponseMessage> respond, string model = "")
    {
        var handler = new FakeHandler(respond);
        var waits = new List<TimeSpan>();
        var client = new GeminiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gemini.test/") }, new Settings(key, model), NullLogger<GeminiClient>.Instance, null, quota)
        { Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        return (client, handler, waits);
    }

    private static HttpResponseMessage Reply(HttpRequestMessage req, params string[] ids) =>
        req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList(ids)) : Json(HttpStatusCode.OK, Answer());

    [Fact]
    public async Task A_model_that_has_used_its_requests_for_the_day_is_never_asked_and_the_next_model_answers()
    {
        var quota = new FakeModelQuota(m => m == "gemini-11.8-flash" ? Quota(m, used: 20) : Quota(m, used: 3));
        var (client, handler, _) = Make("key-limit-1", quota, req => Reply(req, "gemini-11.8-flash", "gemini-10.5-flash"));
        var reply = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);
        reply.Model.Should().Be("gemini-10.5-flash");
        handler.Urls.Should().NotContain(u => u.Contains("gemini-11.8-flash:generateContent"), "it has used its 20 requests today");
    }

    [Fact]
    public async Task When_every_model_has_used_its_day_nothing_is_sent_and_each_model_is_named_with_when_it_renews()
    {
        var quota = new FakeModelQuota(m => Quota(m, used: 20));
        var (client, handler, _) = Make("key-limit-2", quota, req => Reply(req, "gemini-12.8-flash", "gemini-12.5-flash"));
        var ex = await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();
        ex.Which.Code.Should().Be("ai_quota_reached");
        ex.Which.Retryable.Should().BeFalse();
        ex.Which.Message.Should().Contain("Gemini 12.8 Flash: 20 of 20 requests today").And.Contain("Gemini 12.5 Flash").And.Contain("renews in 3 h").And.Contain("nothing is lost");
        handler.Generated.Should().Be(0);
    }

    [Fact]
    public async Task What_Google_says_about_the_day_counts_even_when_the_app_has_counted_fewer_requests()
    {
        // the key is also used elsewhere: Google answered "daily limit" earlier, so the app trusts that and stops, however few requests it saw itself
        var quota = new FakeModelQuota(m => Quota(m, used: 2, google: true));
        var (client, handler, _) = Make("key-limit-3", quota, req => Reply(req, "gemini-13.8-flash"));
        await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();
        handler.Generated.Should().Be(0);
    }

    [Fact]
    public async Task When_every_model_is_at_its_per_minute_limit_the_call_waits_for_the_window_and_then_goes_ahead()
    {
        var calls = 0;
        var quota = new FakeModelQuota(m => calls++ == 0 ? Quota(m, usedMinute: 5, perMinute: 5, oldest: Now.AddSeconds(-20)) : Quota(m, usedMinute: 0));
        var (client, handler, waits) = Make("key-limit-4", quota, req => Reply(req, "gemini-14.8-flash"));
        var reply = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);
        reply.Text.Should().Be("ok");
        waits.Should().ContainSingle().Which.TotalSeconds.Should().BeInRange(35, 45, "the oldest of the 5 requests is 20 s old: about 41 s until the minute has room");
        handler.Generated.Should().Be(1);
    }

    [Fact]
    public async Task A_request_too_big_for_what_is_left_of_the_tokens_a_minute_waits_too()
    {
        var calls = 0;
        var quota = new FakeModelQuota(m => calls++ == 0 ? Quota(m, usedMinute: 1, tokensMinute: 249_900) : Quota(m));
        var (client, _, waits) = Make("key-limit-5", quota, req => Reply(req, "gemini-15.8-flash"));
        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText(new string('x', 4000))]), default);
        waits.Should().HaveCount(1);
    }

    [Fact]
    public async Task Every_request_that_goes_to_Google_is_written_down_the_good_ones_with_their_tokens_and_the_failed_ones_with_why()
    {
        var quota = new FakeModelQuota(m => Quota(m));
        var step = 0;
        var (client, _, _) = Make("key-limit-6", quota, req =>
        {
            if (req.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, ModelList("gemini-16.8-flash", "gemini-15.5-flash"));
            return step++ switch
            {
                0 => Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded","status":"UNAVAILABLE"}}"""),
                1 => Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Quota exceeded for metric generate_content_free_tier_requests, limit: 20, per day","status":"RESOURCE_EXHAUSTED"}}"""),
                _ => Json(HttpStatusCode.OK, Answer()),
            };
        });
        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Text.Should().Be("ok");
        quota.Recorded.Select(r => r.Outcome).Should().Equal("busy", "daily_limit", "ok");
        quota.Recorded.Last().In.Should().Be(120);
        quota.Recorded.Last().Out.Should().Be(30);
    }

    [Fact]
    public async Task A_key_test_does_not_ask_a_model_that_has_used_its_day_so_it_does_not_burn_the_requests_that_are_left()
    {
        var quota = new FakeModelQuota(m => m == "gemini-17.8-flash" ? Quota(m, used: 27) : Quota(m));
        var (client, handler, _) = Make("key-limit-7", quota, req => Reply(req, "gemini-17.8-flash", "gemini-17.5-flash"));
        var probes = await client.ProbeAsync(["gemini-17.8-flash", "gemini-17.5-flash"], default);
        probes.Single(p => p.Model == "gemini-17.8-flash").Ok.Should().BeFalse();
        probes.Single(p => p.Model == "gemini-17.8-flash").Message.Should().Contain("Daily limit used up");
        probes.Single(p => p.Model == "gemini-17.5-flash").Ok.Should().BeTrue();
        handler.Urls.Count(u => u.Contains("gemini-17.8-flash:generateContent")).Should().Be(0);
    }

    [Fact]
    public void The_limits_from_the_AI_Studio_page_are_preset_and_the_tool_works_out_what_is_left()
    {
        ModelQuotaService.Presets["gemini-3.8-flash"].Should().Be((20, 5, 250_000));
        ModelQuotaService.Presets["gemini-3.6-flash"].Should().Be((20, 5, 250_000));
        ModelQuotaService.Presets["gemini-3.5-flash-lite"].Should().Be((500, 15, 250_000));

        Quota("gemini-3.8-flash", used: 27).DayReached.Should().BeTrue("27 of 20");
        Quota("gemini-3.8-flash", used: 27).LeftToday.Should().Be(0);
        Quota("gemini-3.8-flash", used: 4).LeftToday.Should().Be(16);
        Quota("gemini-3.8-flash", usedMinute: 5).MinuteBlocked(1000).Should().BeTrue("5 requests in the last minute, the limit is 5");
        Quota("gemini-3.8-flash", usedMinute: 4).MinuteBlocked(1000).Should().BeFalse();
        Quota("gemini-3.8-flash", usedMinute: 1, tokensMinute: 240_000).MinuteBlocked(20_000).Should().BeTrue("it would go over 250,000 tokens in the minute");
        Quota("gemini-3.8-flash", usedMinute: 5, oldest: Now.AddSeconds(-50)).WaitForMinute.TotalSeconds.Should().BeInRange(10, 12);
        ModelQuota.Pretty("gemini-3.5-flash-lite").Should().Be("Gemini 3.5 Flash Lite");
        Quota("gemini-3.8-flash", used: 27).Describe().Should().Be("Gemini 3.8 Flash: 27 of 20 requests today, renews in 3 h 0 min");
        ModelQuotaService.IsModelLimitKey("Ai.ModelLimit.gemini-3.8-flash.PerDay").Should().BeTrue();
        ModelQuotaService.IsModelLimitKey("Ai.ModelLimit.x'; DROP TABLE.PerDay").Should().BeFalse();
        ModelQuotaService.IsModelLimitKey("Ai.Provider").Should().BeFalse();
    }
}