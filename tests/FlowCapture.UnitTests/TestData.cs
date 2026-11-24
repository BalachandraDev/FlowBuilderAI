using System.Text.Json.Nodes;

namespace FlowCapture.UnitTests;

/// <summary>Locates the repository's prompt pack (schemas and the MRD-003 example) from the test output folder.</summary>
public static class TestData
{
    public static string PromptsDir { get; } = FindPrompts();

    public static JsonObject FlowSchema() => Load("schemas/flow.v1.json");
    public static JsonObject ProjectSchema() => Load("schemas/project-analysis.v1.json");
    public static JsonObject Example() => Load("examples/mrd-003.expected.json");
    public static string ExampleUserMessage() => File.ReadAllText(Path.Combine(PromptsDir, "examples", "mrd-003.user-message.txt"));

    public static JsonObject Load(string relative) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(PromptsDir, relative)))!;

    private static string FindPrompts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts");
            if (Directory.Exists(Path.Combine(candidate, "schemas"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the /prompts folder above " + AppContext.BaseDirectory);
    }
}
