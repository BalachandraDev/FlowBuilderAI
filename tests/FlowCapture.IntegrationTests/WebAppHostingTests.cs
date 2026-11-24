using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>The API also serves the web app, so one link works for everything. These run once the app is built into wwwroot.</summary>
[Collection("api")]
public class WebAppHostingTests(TestApp app)
{
    private HttpClient Client() => app.CreateClient(new() { AllowAutoRedirect = false });

    [RequiresWebAppFact]
    public async Task The_address_of_the_server_opens_the_app_and_it_may_only_load_its_own_files()
    {
        var res = await Client().GetAsync("/");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("<div id=\"root\">").And.Contain("/assets/");

        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'").And.Contain("script-src 'self'").And.Contain("frame-ancestors 'none'").And.Contain("connect-src 'self'");
        csp.Should().NotContain("unsafe-eval").And.NotContain("script-src 'self' 'unsafe-inline'");
        res.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        res.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        res.Headers.CacheControl!.NoCache.Should().BeTrue();                       // the page itself is always checked, so an update reaches everyone
    }

    [RequiresWebAppFact]
    public async Task A_link_to_a_page_inside_the_app_opens_the_app_on_that_page()
    {
        foreach (var path in new[] { "/login", "/settings", "/project/3f2b6f0e-6a7c-4f34-8f5a-0d9e8a8c1b11", "/flow/3f2b6f0e-6a7c-4f34-8f5a-0d9e8a8c1b11/new", "/entry/abc" })
        {
            var res = await Client().GetAsync(path);
            res.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await res.Content.ReadAsStringAsync()).Should().Contain("<div id=\"root\">", path);
        }
    }

    [RequiresWebAppFact]
    public async Task Every_file_the_page_loads_is_served_and_files_with_a_hash_in_their_name_are_cached_for_a_year()
    {
        var html = await (await Client().GetAsync("/")).Content.ReadAsStringAsync();
        var files = Regex.Matches(html, "(?:src|href)=\"(/[^\"]+)\"").Select(m => m.Groups[1].Value).Where(p => p.StartsWith("/assets/")).ToList();
        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            var res = await Client().GetAsync(file);
            res.StatusCode.Should().Be(HttpStatusCode.OK, file);
            res.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromDays(365), file);
            res.Headers.CacheControl.Extensions.Select(e => e.Name).Should().Contain("immutable");
        }
    }

    [RequiresWebAppFact]
    public async Task The_service_worker_and_manifest_are_always_checked_so_an_update_reaches_every_phone()
    {
        foreach (var path in new[] { "/sw.js", "/manifest.webmanifest" })
        {
            var res = await Client().GetAsync(path);
            res.StatusCode.Should().Be(HttpStatusCode.OK, path);
            res.Headers.CacheControl!.NoCache.Should().BeTrue(path);
        }
    }

    [RequiresWebAppFact]
    public async Task A_missing_file_is_a_404_not_the_app_and_the_api_keeps_its_own_strict_headers()
    {
        (await Client().GetAsync("/assets/does-not-exist.js")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client().GetAsync("/icons/nothing.png")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var api = await Client().GetAsync("/health/live");
        api.StatusCode.Should().Be(HttpStatusCode.OK);
        api.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("default-src 'none'");
        (await Client().GetAsync("/api/v1/projects")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);       // the API still needs a sign-in
        (await Client().PostAsync("/flow/1", new StringContent(""))).StatusCode.Should().NotBe(HttpStatusCode.OK);   // only a GET opens a page
    }
}
