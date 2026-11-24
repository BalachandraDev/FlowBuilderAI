using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FlowCapture.Application;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using FlowCapture.Infrastructure.Ai;
using FlowCapture.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowCapture.UnitTests;

public class GeminiTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Key)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.RequestUri!.ToString(), body, request.Headers.TryGetValues("x-goog-api-key", out var k) ? k.First() : null));
            return respond(request, body);
        }
    }

    private sealed class FakeSettings(string? key = "test-key", string model = "gemini-9-flash") : ISettingsService
    {
        public Task<AiProviderSettings> GetProviderAsync(CancellationToken ct = default) => Task.FromResult(new AiProviderSettings("Gemini", key, model, true));
        public Task<GroqSettings> GetGroqAsync(CancellationToken ct = default) => Task.FromResult(new GroqSettings(null, "whisper-test", false, false));
        public Task<AiSettings> GetAiAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetNameDictionaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetAudioMaxMinutesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> GetFlagAsync(string key, bool fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAsync(string k, string v, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSecretAsync(string k, string? v, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Answer(string text, string finish = "STOP") =>
        new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }, ["finishReason"] = finish,
            }),
            ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 120, ["candidatesTokenCount"] = 30, ["thoughtsTokenCount"] = 10 },
        }.ToJsonString();

    private static (GeminiClient Client, FakeHandler Handler) Make(Func<HttpRequestMessage, string, HttpResponseMessage> respond, ISettingsService? settings = null)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        return (new GeminiClient(http, settings ?? new FakeSettings(), NullLogger<GeminiClient>.Instance) { Delay = (_, _) => Task.CompletedTask }, handler);
    }

    // ------------------------------------------------------------------ choosing a model

    [Theory]
    [InlineData("gemini-2.0-flash,gemini-2.5-flash,gemini-2.5-flash-lite,gemini-2.5-pro", "gemini-2.5-flash")]
    [InlineData("gemini-3-flash-preview,gemini-2.5-flash,gemini-2.5-flash-image", "gemini-2.5-flash")]               // a stable one beats a preview
    [InlineData("gemini-3-flash-preview,gemini-2.5-flash-lite", "gemini-3-flash-preview")]                           // nothing stable: take the preview rather than a Lite
    [InlineData("gemini-2.5-pro,gemini-2.5-flash-lite", "gemini-2.5-flash-lite")]
    [InlineData("gemini-2.5-pro-preview,gemini-1.5-pro", "gemini-1.5-pro")]
    [InlineData("gemini-3.1-flash,gemini-3-flash,gemini-2.5-flash", "gemini-3.1-flash")]
    public void The_newest_stable_flash_model_is_chosen_when_the_admin_names_none(string ids, string expected) =>
        GeminiClient.PickBest(ids.Split(',')).Should().Be(expected);

    [Fact]
    public void No_model_at_all_gives_nothing() => GeminiClient.PickBest([]).Should().BeNull();

    [Fact]
    public async Task Without_a_chosen_model_the_keys_own_model_list_decides_and_is_remembered()
    {
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """{"models":[{"name":"models/gemini-8-flash","displayName":"G8","supportedGenerationMethods":["generateContent"]},{"name":"models/embed-1","supportedGenerationMethods":["embedContent"]}]}""")
            : Json(HttpStatusCode.OK, Answer("hello")), new FakeSettings(key: "unique-key-for-this-test", model: ""));

        var one = await client.GenerateAsync(new GeminiRequest("sys", [GeminiPart.OfText("hi")]), default);
        await client.GenerateAsync(new GeminiRequest("sys", [GeminiPart.OfText("hi")]), default);

        one.Model.Should().Be("gemini-8-flash");
        handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(2);
        handler.Seen.Count(s => s.Url.Contains("v1beta/models?")).Should().Be(1, "the list is fetched once");
    }

    [Theory]
    [InlineData("gemini-2.0-flash,gemini-2.5-flash,gemini-2.5-flash-lite,gemini-2.5-pro", "gemini-2.5-flash,gemini-2.5-flash-lite,gemini-2.0-flash")]
    [InlineData("gemini-3.8-flash,gemini-3.5-flash,gemini-2.5-flash,gemini-2.5-flash-image,gemini-3.8-flash-live,gemini-2.5-flash-lite,gemini-2.5-pro", "gemini-3.8-flash,gemini-2.5-flash,gemini-2.5-flash-lite,gemini-3.5-flash")]
    // the real case: four sister models of one generation share the same overloaded servers, so an OLDER generation comes second, not the sister
    [InlineData("gemini-3.8-flash,gemini-3.7-flash,gemini-3.6-flash,gemini-3.5-flash,gemini-2.5-flash,gemini-2.5-flash-lite,gemini-2.0-flash", "gemini-3.8-flash,gemini-2.5-flash,gemini-2.5-flash-lite,gemini-3.7-flash,gemini-3.6-flash")]
    [InlineData("gemini-3-flash-preview,gemini-2.5-flash-lite", "gemini-3-flash-preview,gemini-2.5-flash-lite")]
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro")]
    public void The_fallback_list_is_the_best_model_then_the_other_stable_ones_then_lite_and_never_a_model_that_cannot_write_text(string ids, string expected) =>
        GeminiClient.Rank(ids.Split(',')).Should().Equal(expected.Split(','));

    private static string ModelList(params string[] ids) =>
        new JsonObject { ["models"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["name"] = "models/" + i, ["supportedGenerationMethods"] = new JsonArray("generateContent") }).ToArray()) }.ToJsonString();

    private static string ModelOf(string url) => System.Text.RegularExpressions.Regex.Match(url, @"models/([^:/?]+):generateContent").Groups[1].Value;

    // ------------------------------------------------------------------ a busy model is not asked again: the next one is

    [Fact]
    public async Task When_the_best_model_is_overloaded_the_next_one_answers_at_once_without_waiting()
    {
        var waits = new List<TimeSpan>();
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-7.8-flash", "gemini-7.5-flash", "gemini-7.0-flash"))
            : ModelOf(req.RequestUri!.ToString()) == "gemini-7.8-flash" ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"The model is overloaded.","status":"UNAVAILABLE"}}""")
            : Json(HttpStatusCode.OK, Answer("fine")), new FakeSettings(key: "key-overloaded-first", model: ""));
        client.Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; };

        var reply = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);

        reply.Text.Should().Be("fine");
        reply.Model.Should().Be("gemini-7.5-flash", "the usage page shows the model that really answered");
        handler.Seen.Where(s => s.Url.Contains(":generateContent")).Select(s => ModelOf(s.Url)).Should().Equal("gemini-7.8-flash", "gemini-7.5-flash");
        waits.Should().BeEmpty("moving to another model costs no waiting");
    }

    [Fact]
    public async Task A_model_that_just_failed_goes_last_so_the_next_call_does_not_lose_time_on_it_again()
    {
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-6.8-flash", "gemini-6.5-flash"))
            : ModelOf(req.RequestUri!.ToString()) == "gemini-6.8-flash" ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}""")
            : Json(HttpStatusCode.OK, Answer("fine")), new FakeSettings(key: "key-remember-failure", model: ""));

        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);
        handler.Seen.Clear();
        var second = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);

        second.Model.Should().Be("gemini-6.5-flash");
        handler.Seen.Where(s => s.Url.Contains(":generateContent")).Should().ContainSingle("the model that failed a moment ago is not tried first");
    }

    [Fact]
    public async Task A_model_whose_daily_quota_is_used_up_hands_over_to_the_next_one_which_has_its_own_quota()
    {
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-5.8-flash", "gemini-5.5-flash"))
            : ModelOf(req.RequestUri!.ToString()) == "gemini-5.8-flash" ? Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Quota exceeded for metric GenerateRequestsPerDayPerProjectPerModel-FreeTier","status":"RESOURCE_EXHAUSTED"}}""")
            : Json(HttpStatusCode.OK, Answer("from the second")), new FakeSettings(key: "key-daily-quota", model: ""));

        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Model.Should().Be("gemini-5.5-flash");
        handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(2);
    }

    [Fact]
    public async Task When_every_model_is_busy_it_waits_once_between_rounds_then_says_so_plainly_and_stops()
    {
        var waits = new List<TimeSpan>();
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-4.8-flash", "gemini-4.5-flash"))
            : Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}"""), new FakeSettings(key: "key-everything-busy", model: ""));
        client.Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; };

        var ex = await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();

        ex.Which.Code.Should().Be("ai_unavailable");
        ex.Which.Retryable.Should().BeTrue();
        ex.Which.Message.Should().Contain("overloaded").And.Contain("2 models").And.Contain("Nothing was lost");
        handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(4, "two rounds over two models, not an endless loop");
        waits.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_model_that_no_longer_exists_is_skipped_when_the_model_is_automatic_but_an_error_when_the_admin_named_it()
    {
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-8.8-flash", "gemini-8.1-flash"))
            : ModelOf(req.RequestUri!.ToString()) == "gemini-8.8-flash" ? Json(HttpStatusCode.NotFound, """{"error":{"message":"models/gemini-8.8-flash is not found","status":"NOT_FOUND"}}""")
            : Json(HttpStatusCode.OK, Answer("fine")), new FakeSettings(key: "key-retired-model", model: ""));

        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Model.Should().Be("gemini-8.1-flash");
        handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(2);

        var (named, _) = Make((_, _) => Json(HttpStatusCode.NotFound, """{"error":{"message":"models/x is not found"}}"""));
        (await named.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>()).Which.Code.Should().Be("ai_bad_request");
    }

    [Fact]
    public async Task A_model_that_failed_before_gets_a_short_time_limit_so_asking_it_again_cannot_cost_a_long_wait()
    {
        var old = GeminiClient.ProbationSeconds;
        GeminiClient.ProbationSeconds = 1;
        try
        {
            var hang = false;
            var inner = new FakeHandler((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-9.8-flash"))
                : Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}"""));
            var slow = new SlowThenFast(inner, "gemini-9.8-flash", () => hang);
            var client = new GeminiClient(new HttpClient(slow) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(key: "key-probation", model: ""), NullLogger<GeminiClient>.Instance) { Delay = (_, _) => Task.CompletedTask };

            await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();    // fails: now on probation
            hang = true;                                                                                                                                         // and now it would hang for a minute
            var started = DateTime.UtcNow;
            await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], TimeoutSeconds: 100), default)).Should().ThrowAsync<AiServiceException>();

            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10), "three attempts of one second each, not three of a hundred");
        }
        finally { GeminiClient.ProbationSeconds = old; }
    }
    [Fact]
    public async Task A_model_the_admin_named_is_the_only_one_used_and_the_model_list_is_never_fetched()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}"""));   // named: gemini-9-flash

        await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();

        handler.Seen.Should().HaveCount(3).And.OnlyContain(s => s.Url.Contains("gemini-9-flash:generateContent"));
    }

    [Fact]
    public async Task A_key_or_request_problem_is_not_tried_on_other_models()
    {
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-3.9-flash", "gemini-3.4-flash"))
            : Json(HttpStatusCode.Forbidden, """{"error":{"message":"denied"}}"""), new FakeSettings(key: "key-forbidden", model: ""));

        (await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>()).Which.Code.Should().Be("ai_auth");
        handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(1);
    }

    [Fact]
    public async Task An_attempt_that_hangs_is_given_up_after_its_time_limit_and_the_next_model_is_tried()
    {
        var handler = new FakeHandler((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-2.9-flash", "gemini-2.4-flash")) : Json(HttpStatusCode.OK, Answer("late")));
        var slow = new SlowThenFast(handler, "gemini-2.9-flash");
        var client = new GeminiClient(new HttpClient(slow) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(key: "key-hang", model: ""), NullLogger<GeminiClient>.Instance) { Delay = (_, _) => Task.CompletedTask };

        var started = DateTime.UtcNow;
        var reply = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], TimeoutSeconds: 1), default);

        reply.Model.Should().Be("gemini-2.4-flash");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10), "it did not wait for the hanging model");
    }

    [Fact]
    public async Task With_other_models_to_fall_back_on_a_silent_model_is_given_up_after_a_short_first_try_not_the_full_time()
    {
        var old = GeminiClient.FirstTrySeconds;
        GeminiClient.FirstTrySeconds = 1;
        try
        {
            var handler = new FakeHandler((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-2.7-flash", "gemini-2.2-flash")) : Json(HttpStatusCode.OK, Answer("quick")));
            var slow = new SlowThenFast(handler, "gemini-2.7-flash");
            var client = new GeminiClient(new HttpClient(slow) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(key: "key-firsttry", model: ""), NullLogger<GeminiClient>.Instance) { Delay = (_, _) => Task.CompletedTask };

            var started = DateTime.UtcNow;
            var reply = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);          // the default time limit is 120 s

            reply.Model.Should().Be("gemini-2.2-flash");
            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10), "the silent model cost about a second, not two minutes");
        }
        finally { GeminiClient.FirstTrySeconds = old; }
    }

    [Fact]
    public async Task A_model_that_has_not_answered_after_a_few_seconds_is_not_waited_for_the_next_model_is_asked_too_and_the_first_answer_wins()
    {
        var old = GeminiClient.HedgeSeconds;
        GeminiClient.HedgeSeconds = 1;
        try
        {
            var handler = new FakeHandler((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-6.7-flash", "gemini-6.2-flash")) : Json(HttpStatusCode.OK, Answer("fast")));
            var slow = new SlowThenFast(handler, "gemini-6.7-flash");                       // the best model hangs for a minute
            var client = new GeminiClient(new HttpClient(slow) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(key: "key-hedge", model: ""), NullLogger<GeminiClient>.Instance) { Delay = (_, _) => Task.CompletedTask };

            var started = DateTime.UtcNow;
            var reply = await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);     // time limits: 35 s first try, 120 s in all

            reply.Model.Should().Be("gemini-6.2-flash");
            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(8), "the second model was asked after about a second, not after the first one's time limit");
            handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(1, "only the second model's request reached the fake server; the first was still hanging");
        }
        finally { GeminiClient.HedgeSeconds = old; }
    }

    [Fact]
    public async Task When_the_first_model_answers_before_the_wait_is_over_no_second_request_is_sent()
    {
        var old = GeminiClient.HedgeSeconds;
        GeminiClient.HedgeSeconds = 5;
        try
        {
            var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-6.5-flash", "gemini-6.1-flash")) : Json(HttpStatusCode.OK, Answer("ok")), new FakeSettings(key: "key-nohedge", model: ""));

            (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Model.Should().Be("gemini-6.5-flash");

            handler.Seen.Count(s => s.Url.Contains(":generateContent")).Should().Be(1);
        }
        finally { GeminiClient.HedgeSeconds = old; }
    }

    /// <summary>A model that never answers until the caller gives up on it.</summary>
    private sealed class SlowThenFast(HttpMessageHandler inner, string hangingModel, Func<bool>? when = null) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.ToString().Contains(hangingModel) && (when?.Invoke() ?? true)) await Task.Delay(TimeSpan.FromSeconds(60), ct);
            return await base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task The_key_test_asks_every_candidate_at_once_says_which_answered_and_teaches_the_next_call_to_skip_the_busy_ones()
    {
        var (client, handler) = Make((req, _) => req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, ModelList("gemini-1.8-flash", "gemini-1.5-flash"))
            : ModelOf(req.RequestUri!.ToString()) == "gemini-1.8-flash" ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}""")
            : Json(HttpStatusCode.OK, Answer("OK")), new FakeSettings(key: "key-probe", model: ""));

        var models = await client.CandidateModelsAsync(default);
        var probes = await client.ProbeAsync(models, default);

        probes.Select(p => (p.Model, p.Ok)).Should().BeEquivalentTo([("gemini-1.8-flash", false), ("gemini-1.5-flash", true)]);
        probes.Single(p => !p.Ok).Message.Should().Be("Busy or not answering.");
        probes.Single(p => p.Ok).InputTokens.Should().Be(120);

        handler.Seen.Clear();
        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Model.Should().Be("gemini-1.5-flash");
        handler.Seen.Should().ContainSingle("the busy model learnt by the test is not tried first");
    }

    // ------------------------------------------------------------------ many photos and recordings at once

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int InFlight, Most, Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref InFlight);
            Interlocked.Increment(ref Calls);
            int seen; while ((seen = Volatile.Read(ref Most)) < now && Interlocked.CompareExchange(ref Most, now, seen) != seen) { }
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref InFlight);
            return Json(HttpStatusCode.OK, Answer("read"));
        }
    }

    [Fact]
    public async Task Photos_and_recordings_attached_in_bulk_are_read_three_at_a_time_and_all_of_them_are_read()
    {
        var handler = new CountingHandler();
        var client = new GeminiClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(), NullLogger<GeminiClient>.Instance);
        var media = new GeminiRequest("", [GeminiPart.OfText("read"), GeminiPart.OfFile("image/jpeg", [1, 2, 3])]);

        var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GenerateAsync(media, default)));

        replies.Should().HaveCount(12).And.OnlyContain(x => x.Text == "read");
        handler.Calls.Should().Be(12);
        handler.Most.Should().BeLessThanOrEqualTo(3, "no more than three are sent at the same moment").And.BeGreaterThan(1, "but they are not one by one either");
        GeminiClient.MediaGate.CurrentCount.Should().Be(3, "every place is given back");
    }

    [Fact]
    public async Task Text_requests_such_as_the_analysis_do_not_wait_behind_photos()
    {
        var handler = new CountingHandler();
        var client = new GeminiClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(), NullLogger<GeminiClient>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("analyse")]), default)));

        handler.Most.Should().BeGreaterThan(3, "text-only calls are not limited by the photo and recording gate");
    }

    [Fact]
    public async Task A_failed_photo_read_gives_its_place_back()
    {
        var client = new GeminiClient(new HttpClient(new FakeHandler((_, _) => Json(HttpStatusCode.Forbidden, """{"error":{"message":"denied"}}"""))) { BaseAddress = new Uri("https://example.test/") }, new FakeSettings(), NullLogger<GeminiClient>.Instance);
        for (var i = 0; i < 5; i++)
            await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfFile("image/jpeg", [1])]), default)).Should().ThrowAsync<AiServiceException>();
        GeminiClient.MediaGate.CurrentCount.Should().Be(3);
    }
    // ------------------------------------------------------------------ thinking briefly

    [Fact]
    public async Task Extraction_asks_the_model_to_think_briefly_and_an_ordinary_request_does_not()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("ok")), new FakeSettings(model: "gemini-3.7-flash"));
        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true), default);
        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default);

        JsonNode.Parse(handler.Seen[0].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("low");
        JsonNode.Parse(handler.Seen[1].Body)!["generationConfig"]!["thinkingConfig"].Should().BeNull();

        var (older, olderHandler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("ok")), new FakeSettings(model: "gemini-2.5-flash"));
        await older.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true), default);
        JsonNode.Parse(olderHandler.Seen[0].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>().Should().Be(1024);

        var (unknown, unknownHandler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("ok")));       // gemini-9-flash: no known setting, so none is sent
        await unknown.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true), default);
        JsonNode.Parse(unknownHandler.Seen[0].Body)!["generationConfig"]!["thinkingConfig"].Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_long_set_of_notes_asks_for_medium_thinking_and_a_transcription_for_low()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("ok")), new FakeSettings(model: "gemini-3.6-flash-levels"));
        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], Thinking: "medium"), default);
        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true), default);
        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true, Thinking: "high"), default);

        JsonNode.Parse(handler.Seen[0].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("medium");
        JsonNode.Parse(handler.Seen[1].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("low");
        JsonNode.Parse(handler.Seen[2].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("high", "an explicit level wins");

        var (older, olderHandler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("ok")), new FakeSettings(model: "gemini-2.5-flash"));
        await older.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], Thinking: "medium"), default);
        JsonNode.Parse(olderHandler.Seen[0].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>().Should().Be(4096);
    }
    [Fact]
    public async Task A_model_that_rejects_the_thinking_setting_is_asked_again_without_it_and_remembered()
    {
        var (client, handler) = Make((_, body) => body.Contains("thinkingConfig")
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"Unknown name \"thinkingLevel\": thinking is not supported for this model","status":"INVALID_ARGUMENT"}}""")
            : Json(HttpStatusCode.OK, Answer("fine")), new FakeSettings(model: "gemini-3.3-flash-nothink"));

        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true), default)).Text.Should().Be("fine");
        handler.Seen.Should().HaveCount(2);
        handler.Seen[1].Body.Should().NotContain("thinkingConfig");

        await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")], LowThinking: true), default);
        handler.Seen.Should().HaveCount(3, "the second call does not try the setting again");
    }

    // ------------------------------------------------------------------ the request and the answer

    [Fact]
    public async Task The_key_goes_in_a_header_never_in_the_address_and_the_files_are_sent_inline()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("{\"a\":1}")));

        var reply = await client.GenerateAsync(new GeminiRequest("be careful", [GeminiPart.OfText("transcribe"), GeminiPart.OfFile("audio/wav", [1, 2, 3])], MaxOutputTokens: 777, JsonOutput: true), default);

        var seen = handler.Seen.Single();
        seen.Key.Should().Be("test-key");
        seen.Url.Should().NotContain("test-key").And.Contain("models/gemini-9-flash:generateContent");
        var body = JsonNode.Parse(seen.Body)!;
        body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Be("be careful");
        body["contents"]![0]!["parts"]![1]!["inline_data"]!["mime_type"]!.GetValue<string>().Should().Be("audio/wav");
        body["contents"]![0]!["parts"]![1]!["inline_data"]!["data"]!.GetValue<string>().Should().Be(Convert.ToBase64String([1, 2, 3]));
        body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().Should().Be(777);
        body["generationConfig"]!["responseMimeType"]!.GetValue<string>().Should().Be("application/json");
        reply.Text.Should().Be("{\"a\":1}");
        reply.InputTokens.Should().Be(120);
        reply.OutputTokens.Should().Be(40, "the answer and the thinking both count");
    }

    [Fact]
    public async Task Thinking_parts_are_not_part_of_the_answer()
    {
        var body = """{"candidates":[{"content":{"parts":[{"text":"private thoughts","thought":true},{"text":"the answer"}]},"finishReason":"STOP"}]}""";
        var (client, _) = Make((_, _) => Json(HttpStatusCode.OK, body));
        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Text.Should().Be("the answer");
    }

    [Fact]
    public async Task A_schema_the_model_does_not_accept_is_retried_once_with_the_schema_written_into_the_instructions()
    {
        var (client, handler) = Make((_, body) => body.Contains("responseJsonSchema")
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid JSON payload: responseJsonSchema is not supported","status":"INVALID_ARGUMENT"}}""")
            : Json(HttpStatusCode.OK, Answer("{\"ok\":true}")));
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}}}""")!;

        var reply = await client.GenerateAsync(new GeminiRequest("sys", [GeminiPart.OfText("go")], schema), default);

        reply.Text.Should().Be("{\"ok\":true}");
        handler.Seen.Should().HaveCount(2);
        var second = JsonNode.Parse(handler.Seen[1].Body)!;
        second["generationConfig"]!["responseJsonSchema"].Should().BeNull();
        second["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Contain("JSON Schema").And.Contain("\"ok\"");
    }

    [Fact]
    public async Task A_busy_service_is_tried_again_and_a_rate_limit_that_lasts_is_explained()
    {
        var calls = 0;
        var (client, _) = Make((_, _) => ++calls < 3 ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}""") : Json(HttpStatusCode.OK, Answer("fine")));
        (await client.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Text.Should().Be("fine");
        calls.Should().Be(3);

        var (limited, handler) = Make((_, _) => Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota","details":[{"retryDelay":"20s"}]}}"""));
        var ex = await limited.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();
        ex.Which.Code.Should().Be("ai_rate_limited");
        ex.Which.Retryable.Should().BeTrue();
        ex.Which.Message.Should().Contain("free AI limit");
        handler.Seen.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"API key not valid. Please pass a valid API key."}}""", "ai_auth")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"message":"denied"}}""", "ai_auth")]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"message":"models/x is not found"}}""", "ai_bad_request")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Request contains an invalid argument."}}""", "ai_bad_request")]
    public async Task Errors_become_plain_messages_with_a_code(HttpStatusCode status, string body, string code)
    {
        var (client, _) = Make((_, _) => Json(status, body));
        var ex = await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();
        ex.Which.Code.Should().Be(code);
        ex.Which.Message.Should().NotContain("test-key");
    }

    [Fact]
    public async Task A_blocked_or_cut_off_answer_is_reported_not_returned_as_empty_text()
    {
        var (blocked, _) = Make((_, _) => Json(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}"""));
        (await blocked.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>()).Which.Code.Should().Be("ai_refusal");

        var (cut, _) = Make((_, _) => Json(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[]},"finishReason":"MAX_TOKENS"}]}"""));
        (await cut.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>()).Which.Code.Should().Be("ai_truncated");
    }

    [Fact]
    public async Task The_analysis_thinks_lightly_the_first_time_and_harder_only_on_the_corrective_attempt()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("{\"flowTitle\":\"X\"}")), new FakeSettings(model: "gemini-3.7-flash"));
        var service = new GeminiFlowAnalysisService(client);
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"flowTitle":{"type":"string"}}}""")!;
        await service.CallAsync(new AiCallRequest("m", "system", "user text", "tool", "d", schema, 16000, null, false), default);
        await service.CallAsync(new AiCallRequest("m", "system", "user text", "tool", "d", schema, 16000, null, false, Careful: true), default);
        JsonNode.Parse(handler.Seen[0].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("low", "thinking is billed as output");
        JsonNode.Parse(handler.Seen[1].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("medium");
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent_and_the_message_points_to_the_admin_dashboard()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("x")), new FakeSettings(key: null));
        var ex = await client.Invoking(c => c.GenerateAsync(new GeminiRequest("", [GeminiPart.OfText("x")]), default)).Should().ThrowAsync<AiServiceException>();
        ex.Which.Code.Should().Be("ai_not_configured");
        ex.Which.Message.Should().Contain("Admin dashboard");
        handler.Seen.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ the three services built on it

    [Fact]
    public async Task The_flow_analysis_returns_the_json_object_even_when_the_model_wraps_it_in_a_code_fence()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("```json\n{\"flowTitle\":\"X\"}\n```")));
        var service = new GeminiFlowAnalysisService(client);
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"flowTitle":{"type":"string"}}}""")!;

        var result = await service.CallAsync(new AiCallRequest("claude-ignored", "system", "user text", "tool", "d", schema, 16000, "medium", false), default);

        result.ToolInputJson.Should().Be("{\"flowTitle\":\"X\"}");
        result.InputTokens.Should().Be(120);
        var sent = JsonNode.Parse(handler.Seen.Single().Body)!;
        sent["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(32000);
        handler.Seen.Single().Url.Should().Contain("gemini-9-flash").And.NotContain("claude-ignored");
    }

    [Fact]
    public async Task A_voice_note_is_sent_as_audio_and_comes_back_as_text_and_silence_is_reported()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("  The user opens the menu.  ")));
        var stt = new GeminiSpeechToText(client);

        var result = await stt.TranscribeAsync(new MemoryStream([1, 2, 3, 4]), "audio/wav", "note.wav", ["ta-IN", "en-IN"], default);

        result.Text.Should().Be("The user opens the menu.");
        result.Locale.Should().Be("ta-IN");
        result.Provider.Should().Be("gemini");
        JsonNode.Parse(handler.Seen.Single().Body)!["contents"]![0]!["parts"]![1]!["inline_data"]!["mime_type"]!.GetValue<string>().Should().Be("audio/wav");

        var (silent, _) = Make((_, _) => Json(HttpStatusCode.OK, Answer("NO_SPEECH")));
        var ex = await new GeminiSpeechToText(silent).Invoking(s => s.TranscribeAsync(new MemoryStream([1]), "audio/wav", "n.wav", ["en-IN"], default)).Should().ThrowAsync<SttException>();
        ex.Which.Message.Should().Contain("No speech");
        ex.Which.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_recording_that_is_too_large_is_refused_before_anything_is_sent()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("x")));
        var ex = await new GeminiSpeechToText(client).Invoking(s => s.TranscribeAsync(new MemoryStream(new byte[15 * 1024 * 1024]), "audio/wav", "n.wav", ["en-IN"], default)).Should().ThrowAsync<SttException>();
        ex.Which.Message.Should().Contain("too long");
        handler.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task A_busy_AI_makes_the_voice_note_retryable_so_the_job_tries_again()
    {
        var (client, _) = Make((_, _) => Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"busy"}}"""));
        var ex = await new GeminiSpeechToText(client).Invoking(s => s.TranscribeAsync(new MemoryStream([1]), "audio/wav", "n.wav", ["en-IN"], default)).Should().ThrowAsync<SttException>();
        ex.Which.Retryable.Should().BeTrue();
    }

    [Theory]
    [InlineData("audio/webm;codecs=opus", "audio/webm")]
    [InlineData("audio/x-wav", "audio/wav")]
    [InlineData("audio/mp3", "audio/mpeg")]
    [InlineData("application/octet-stream", "audio/wav")]
    public void Audio_types_are_cleaned_up_for_Gemini(string given, string expected) => GeminiSpeechToText.MimeFor(given).Should().Be(expected);

    [Fact]
    public async Task A_photo_is_read_into_text_and_an_empty_photo_is_reported()
    {
        var (client, handler) = Make((_, _) => Json(HttpStatusCode.OK, Answer("Line one\nLine two")));
        var result = await new GeminiImageText(client).ReadAsync(new MemoryStream([9, 9]), "image/jpeg", default);

        result.Text.Should().Be("Line one\nLine two");
        result.Provider.Should().Be("gemini-vision");
        JsonNode.Parse(handler.Seen.Single().Body)!["contents"]![0]!["parts"]![1]!["inline_data"]!["mime_type"]!.GetValue<string>().Should().Be("image/jpeg");

        var (blank, _) = Make((_, _) => Json(HttpStatusCode.OK, Answer("NO_TEXT")));
        var ex = await new GeminiImageText(blank).Invoking(s => s.ReadAsync(new MemoryStream([1]), "image/png", default)).Should().ThrowAsync<SttException>();
        ex.Which.Message.Should().Contain("clearer");
    }

    // ------------------------------------------------------------------ saved secrets

    [Fact]
    public void A_saved_key_is_unreadable_without_the_signing_key_and_round_trips_with_it()
    {
        var a = new SecretProtector(Options.Create(new SecurityOptions { JwtSigningKey = "signing-key-one-0123456789-0123456789" }));
        var b = new SecretProtector(Options.Create(new SecurityOptions { JwtSigningKey = "signing-key-two-0123456789-0123456789" }));

        var sealedKey = a.Protect("AIza-my-key");

        sealedKey.Should().NotContain("AIza");
        a.Unprotect(sealedKey).Should().Be("AIza-my-key");
        a.Protect("AIza-my-key").Should().NotBe(sealedKey, "every encryption uses a fresh nonce");
        b.Unprotect(sealedKey).Should().BeNull("another signing key cannot read it");
        a.Unprotect("not base64 !!").Should().BeNull();
        a.Unprotect(Convert.ToBase64String(new byte[10])).Should().BeNull();
    }
}
