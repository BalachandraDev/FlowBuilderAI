using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Infrastructure.Jobs;
using FlowCapture.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FlowCapture.TestSupport;

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Scripted stand-in for Claude. By default it answers with the MRD-003 example adapted to the request.</summary>
public sealed class FakeAi : IFlowAnalysisService
{
    public ConcurrentQueue<Func<AiCallRequest, string>> Script { get; } = new();
    public ConcurrentQueue<AiCallRequest> Calls { get; } = new();
    public int InputTokens { get; set; } = 1200;
    public int OutputTokens { get; set; } = 700;

    public void Reset() { Script.Clear(); Calls.Clear(); }

    public Task<AiCallResult> CallAsync(AiCallRequest request, CancellationToken ct)
    {
        Calls.Enqueue(request);
        var produce = Script.TryDequeue(out var scripted) ? scripted : Default;
        return Task.FromResult(new AiCallResult(produce(request), InputTokens, 0, 300, OutputTokens, "tool_use", request.Model, 20));
    }

    public static string Default(AiCallRequest r) =>
        r.ToolName == "submit_project_analysis" ? ProjectResult()
        : r.ToolName == "submit_implementation_plan" ? PlanResult()
        : FlowResult(r.UserMessage);

    public static string PlanResult() => """
        {"summary":"This flow lets the MRD team receive case sheets.","scopeIn":["Receiving case sheets"],"scopeOut":[],
         "roles":[{"role":"MRD user","responsibility":"Receives and saves the sheets.","usesAreas":["Receive menu"]}],
         "journey":[{"title":"Open the menu","who":"MRD user","where":"Receive menu","description":"The user opens the menu <script>alert(1)</script>.","outcome":"The ward list appears.","branches":[{"condition":"A super user","result":"Own ward selected"}]}],
         "touchpoints":[{"name":"Receive menu","type":"Menu","whatHappens":"Pick the ward and save.","dataIn":["Ward"],"dataOut":["Received status"]}],
         "existingAreasAffected":[{"area":"Ward dropdown","effect":"Pre-selected for super users."}],
         "handoffs":[{"from":"MRD","to":"Billing","whatPasses":"Received sheets","when":"On save"}],
         "assumptions":["Super users are mapped in user settings."],"openQuestions":["Confirm how long a sheet may stay pending."]}
        """;

    /// <summary>The example result, with department, module and source refs matched to what the request actually contained.</summary>
    public static string FlowResult(string message)
    {
        var previous = Between(message, "<previous_result>", "</previous_result>");
        var node = JsonNode.Parse(previous ?? File.ReadAllText(Path.Combine(Repo.Prompts, "examples", "mrd-003.expected.json")))!;
        if (previous is null)
        {
            node["department"] = Between(message, "<department>", "</department>")!;
            node["moduleCode"] = Between(message, "<module_code>", "</module_code>")!;
            var refs = Regex.Matches(message, "<entry ref=\"(E\\d+)\"").Select(m => m.Groups[1].Value).ToList();
            FixRefs(node, refs);
        }
        return node.ToJsonString();
    }

    private static void FixRefs(JsonNode? node, List<string> refs)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (key == "sourceEntryIds" && value is JsonArray arr)
                    {
                        var keep = arr.Select(x => x!.GetValue<string>()).Where(refs.Contains).ToList();
                        if (keep.Count == 0 && refs.Count > 0) keep.Add(refs[0]);
                        o[key] = new JsonArray(keep.Select(k => (JsonNode)k).ToArray());
                    }
                    else FixRefs(value, refs);
                }
                break;
            case JsonArray a:
                foreach (var item in a) FixRefs(item, refs);
                break;
        }
    }

    public static string ProjectResult() => """
        {"schemaVersion":"1.0","summary":"Two flows reviewed.",
         "handoffs":[{"id":"H1","from":{"flowRef":"F1","itemId":"S5"},"to":{"flowRef":"F2","itemId":"S1"},
           "description":"Received case sheets go on to the next department.","dataPassed":["Case sheet"],"basis":"Stated in one flow","evidence":"S5 receives the case sheets."}],
         "duplicates":[],
         "conflicts":[{"id":"CON1","flowRefs":["F1","F2"],"itemRefs":[{"flowRef":"F1","itemId":"BR1"}],"description":"Both flows define ward pre-selection.",
           "severity":"Low","suggestedQuestion":"Which department owns the ward pre-selection rule?"}],
         "gaps":[],"clarificationQuestions":[],"combinedMermaid":"flowchart TD\n  A[\"x\"]",
         "confidence":{"handoffs":0.6,"conflictsAndDuplicates":0.5}}
        """;

    public static string? Between(string text, string open, string close)
    {
        var a = text.IndexOf(open, StringComparison.Ordinal);
        if (a < 0) return null;
        a += open.Length;
        var b = text.IndexOf(close, a, StringComparison.Ordinal);
        return b < 0 ? null : text[a..b];
    }
}

public sealed class FakeStt : ISpeechToTextService
{
    public string Text { get; set; } = "The user opens the menu. UHID: AB123456 and phone 9876543210 should not appear.";
    public ConcurrentQueue<IReadOnlyList<string>> Locales { get; } = new();
    public bool Fail { get; set; }

    public Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct)
    {
        Locales.Enqueue(candidateLocales);
        if (Fail) throw new SttException("The speech service is down.", retryable: false);
        return Task.FromResult(new SttResult(Text, candidateLocales[0], 0.93, "[]", "fake", 900, 120, "fake-audio-model"));
    }
}

public sealed class FakeImageText : IImageTextService
{
    public string Text { get; set; } = "Handwritten note: the MRD user checks the ward list. UHID: AB123456 must be hidden.";
    public bool Fail { get; set; }
    public int Calls { get; private set; }

    public Task<ImageTextResult> ReadAsync(Stream image, string contentType, CancellationToken ct)
    {
        Calls++;
        if (Fail) throw new SttException("No readable text was found in the photo.", retryable: false);
        return Task.FromResult(new ImageTextResult(Text, "fake-vision", 400, 60, "fake-vision-model"));
    }
}

public static class Repo
{
    public static readonly string Prompts = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "prompts", "schemas"))) return Path.Combine(dir.FullName, "prompts");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("prompts folder not found");
    }
}

/// <summary>One real API host for all integration tests: SQLite in memory, inline jobs, temp blob folder, fake AI and speech.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly SqliteConnection? _keepAlive;
    private readonly string? _sqlServerDb;
    private readonly string? _sqlServerInstance;
    private readonly string _blobDir = Path.Combine(Path.GetTempPath(), "flowcapture-tests-" + Guid.NewGuid().ToString("N"));

    public const string AdminEmail = "admin@test.local";
    public const string Password = "Passw0rd-Test-123";

    public FakeAi Ai { get; } = new();
    public FakeStt Stt { get; } = new();
    public FakeImageText Images { get; } = new();

    public TestApp()
    {
        // By default the tests run on SQLite in memory. Set FLOWCAPTURE_TEST_SQLSERVER (for example "(localdb)\MSSQLLocalDB")
        // to run the same tests on a throw-away SQL Server database, which also exercises the EF migrations.
        _sqlServerInstance = Environment.GetEnvironmentVariable("FLOWCAPTURE_TEST_SQLSERVER");
        string connection;
        if (!string.IsNullOrWhiteSpace(_sqlServerInstance))
        {
            _sqlServerDb = "FlowCaptureTest_" + Guid.NewGuid().ToString("N");
            connection = $"Server={_sqlServerInstance};Database={_sqlServerDb};Trusted_Connection=True;TrustServerCertificate=True";
        }
        else
        {
            var db = "ft_" + Guid.NewGuid().ToString("N");
            connection = $"Data Source={db};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(connection);
            _keepAlive.Open();
        }

        // Environment variables are read by Program.cs before the test host can add configuration.
        void Set(string k, string v) => Environment.SetEnvironmentVariable(k, v);
        Set("ASPNETCORE_ENVIRONMENT", "Development");
        Set("Database__Provider", _sqlServerDb is null ? "Sqlite" : "SqlServer");
        Set("ConnectionStrings__Default", connection);
        Set("Jobs__Mode", "Inline");
        Set("Storage__RootPath", _blobDir);
        Set("Prompts__Path", Repo.Prompts);
        Set("RateLimit__AuthPerMinute", "100000");
        Set("Anthropic__ApiKey", "test-key");
        Set("Security__JwtSigningKey", "integration-test-signing-key-0123456789-abcdefghij");
        Set("Seed__AdminEmail", AdminEmail);
        Set("Seed__AdminPassword", Password);
        Set("Swagger__Enabled", "false");
        Set("Hangfire__Dashboard", "false");
        Set("Maintenance__Enabled", "false");            // the tests call POST /admin/maintenance/run themselves
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFlowAnalysisService>();
            services.AddSingleton<IFlowAnalysisService>(Ai);
            services.RemoveAll<ISpeechToTextService>();
            services.AddSingleton<ISpeechToTextService>(Stt);
            services.RemoveAll<IImageTextService>();
            services.AddSingleton<IImageTextService>(Images);
        });
    }

    private void DropSqlServerDatabase()
    {
        try
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            using var master = new Microsoft.Data.SqlClient.SqlConnection($"Server={_sqlServerInstance};Database=master;Trusted_Connection=True;TrustServerCertificate=True");
            master.Open();
            using var cmd = master.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{_sqlServerDb}') IS NOT NULL BEGIN ALTER DATABASE [{_sqlServerDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_sqlServerDb}]; END";
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { /* a leftover test database is harmless */ }
    }

    public Task WhenJobsDone() => Services.GetRequiredService<InlineJobScheduler>().WhenIdleAsync();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _keepAlive?.Dispose();
        if (_sqlServerDb is not null) DropSqlServerDatabase();
        try { if (Directory.Exists(_blobDir)) Directory.Delete(_blobDir, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ http helpers

    public HttpClient Anonymous() => CreateClient();

    public async Task<HttpClient> LoginAsync(string email, string password = Password)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), TestJson.Options);
        res.EnsureSuccessStatusCode();
        var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>(TestJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {res.RequestMessage?.Method} {res.RequestMessage?.RequestUri?.AbsolutePath}: {text}");
        return JsonSerializer.Deserialize<T>(text, TestJson.Options)!;
    }

    public static async Task<T> PostAsync<T>(this HttpClient c, string url, object? body = null)
    {
        var res = await c.PostAsJsonAsync(url, body ?? new { }, TestJson.Options);
        return await res.ReadAsync<T>();
    }

    public static async Task<T> PutAsync<T>(this HttpClient c, string url, object body) =>
        await (await c.PutAsJsonAsync(url, body, TestJson.Options)).ReadAsync<T>();

    public static async Task<T> GetAsync<T>(this HttpClient c, string url) => await (await c.GetAsync(url)).ReadAsync<T>();

    public static async Task<string> ProblemAsync(this HttpResponseMessage res) => await res.Content.ReadAsStringAsync();

    /// <summary>Adds a module of the administrator's list to a project, by its name. Modules are only ever picked from that list, so a test that needs one that is not there makes it as the administrator first.</summary>
    public static async Task<ProjectDepartmentDto> AddModuleAsync(this HttpClient c, Guid projectId, string moduleName)
    {
        var master = await c.GetAsync<List<DepartmentDto>>("/api/v1/master/departments");
        var pick = master.Single(d => d.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        return await c.PostAsync<ProjectDepartmentDto>($"/api/v1/projects/{projectId}/departments", new AddProjectDepartmentRequest(pick.Id));
    }
}

