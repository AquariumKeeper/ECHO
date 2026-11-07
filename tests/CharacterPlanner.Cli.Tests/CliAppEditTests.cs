using System.Text;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    private string WriteOperations(string text)
    {
        var path = Path.Combine(root, "operations.json");
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    [Fact]
    public async Task ApplyReturnsEditedDocumentAndReusableTypedContextWithoutWritingInputs()
    {
        var directory = WriteMini(pack: WithContext);
        var path = WriteBuild();
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, """{"bonus":1,"enabled":"true"}""");
        var operations = WriteOperations("""
            [{"kind":"name","text":"Edited"},
             {"kind":"equipment","slot":"mask","clear":true},
             {"kind":"context","id":"bonus","value":2.5}]
            """);
        var before = FilesIn(root);
        var entries = Entries(root);
        var run = await RunAsync("build", "apply", directory, path, operations, "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        Assert.Equal("", run.Error);
        var result = run.Json.GetProperty("result");
        var edited = BuildDocument.Decode(Encoding.UTF8.GetBytes(result.GetProperty("document").GetRawText()));
        Assert.Equal("Edited", edited.Meta.Name);
        Assert.Equal("", edited.Equipment["mask"]);
        Assert.Equal(2.5, result.GetProperty("context").GetProperty("bonus").GetDouble());
        Assert.Equal("true", result.GetProperty("context").GetProperty("enabled").GetString());
        var changes = result.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, changes.Select(change => change.GetProperty("operation").GetInt32()));
        Assert.Equal(new[] { "name", "equipment", "context" }, changes.Select(change => change.GetProperty("kind").GetString()));
        Assert.Equal("meta.Name", changes[0].GetProperty("set")[0].GetString());
        Assert.Equal("equipment.mask", changes[1].GetProperty("cleared")[0].GetString());
        Assert.Equal("context.bonus", changes[2].GetProperty("set")[0].GetString());
        Assert.Equal(entries, Entries(root));
        foreach (var (file, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, file))); }

        File.WriteAllText(path, result.GetProperty("document").GetRawText());
        File.WriteAllText(context, result.GetProperty("context").GetRawText());
        var resolved = await RunAsync("build", "resolve", directory, path, "--context", context);
        Assert.Equal(CliApp.Success, resolved.ExitCode);
        Assert.Equal(2.5, resolved.Json.GetProperty("result").GetProperty("stats").GetProperty("armor").GetDouble());
    }

    [Fact]
    public async Task ApplyRuntimeFailureReportsTheFailingOperationWithoutPartialResults()
    {
        var directory = WriteMini(pack: WithContext);
        var path = WriteBuild();
        var original = File.ReadAllBytes(path);
        var operations = WriteOperations("""
            [{"kind":"name","text":"Do not publish"},
             {"kind":"context","id":"bonus","value":100}]
            """);
        var run = await RunAsync("build", "apply", directory, path, operations);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(("error", "context-value-invalid"), Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
        var error = run.Json.GetProperty("operationErrors")[0];
        Assert.Equal(2, error.GetProperty("operation").GetInt32());
        Assert.Equal("context", error.GetProperty("kind").GetString());
        Assert.Equal(operations, run.Json.GetProperty("diagnostics")[0].GetProperty("path").GetString());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("[", "operations-malformed", 0)]
    [InlineData("{}", "operations-not-array", 0)]
    [InlineData("""[{"kind":"name","text":"Valid"},{"kind":"level","value":40}]""", "operation-kind-removed", 2)]
    [InlineData("""[{"kind":"name","text":"Valid"},{"kind":"notes","text":"a","text":"b"}]""", "operation-field-duplicate", 2)]
    [InlineData("""[{"kind":"name","text":"Valid","extra":true}]""", "operation-field-unknown", 1)]
    public async Task ApplyRejectsMalformedBatchesWithIndexedDiagnostics(string json, string code, int index)
    {
        var run = await RunAsync("build", "apply", WriteMini(), WriteBuild(), WriteOperations(json));
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(("error", code), Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
        Assert.Equal(index, run.Json.GetProperty("operationErrors")[0].GetProperty("operation").GetInt32());
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("""[{"kind":"context","id":"bonus","clear":true}]""", false)]
    public async Task ApplyPreservesOrClearsExplicitAssumptionsWithoutSynthesizingDefaults(string json, bool retained)
    {
        var directory = WriteMini(pack: WithContext);
        var path = WriteBuild();
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, """{"bonus":3}""");
        var run = await RunAsync("build", "apply", directory, path, WriteOperations(json), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        var result = run.Json.GetProperty("result");
        Assert.Equal(retained, result.GetProperty("context").TryGetProperty("bonus", out _));
        Assert.False(result.GetProperty("context").TryGetProperty("enabled", out _));
        Assert.Equal(File.ReadAllBytes(path), BuildDocument.Encode(BuildDocument.Decode(Encoding.UTF8.GetBytes(result.GetProperty("document").GetRawText()))));
        if (!retained) { Assert.Equal("context.bonus", result.GetProperty("changes")[0].GetProperty("cleared")[0].GetString()); }
    }

    [Fact]
    public async Task ApplyUsesEarlierEquipmentSelectionsForLaterConfigurationEdits()
    {
        var directory = WriteMini(pack: CatalogPack);
        var operations = WriteOperations("""
            [{"kind":"equipment","slot":"mask","id":"mask_z"},
             {"kind":"attribute","slot":"mask","target":"minor","id":"armor_roll","value":6},
             {"kind":"mod","slot":"mask","target":"mod_slot","id":"mod"},
             {"kind":"talent","slot":"mask","target":"talent_slot","id":"talent"}]
            """);
        var run = await RunAsync("build", "apply", directory, WriteBuild(), operations);
        Assert.Equal(CliApp.Success, run.ExitCode);
        var document = run.Json.GetProperty("result").GetProperty("document").GetRawText();
        var build = BuildDocument.Decode(Encoding.UTF8.GetBytes(document));
        Assert.Equal("mask_z", build.Equipment["mask"]);
        Assert.Equal(6, build.AttributeRolls["mask"]["minor"].Value);
        Assert.Equal("mod", build.ModSelections["mask"]["mod_slot"]);
        Assert.Equal("talent", build.TalentSelections["mask"]["talent_slot"]);
    }

    [Theory]
    [InlineData("foreign", "build-wrong-pack")]
    [InlineData("malformed", "build-invalid")]
    [InlineData("context", "context-value-invalid")]
    [InlineData("pack", "file-size-mismatch")]
    public async Task ApplyRejectsInvalidStartingInputs(string invalid, string code)
    {
        var directory = WriteMini(invalid == "pack" ? (name, bytes) => name == CharacterPlanner.Core.Packs.PackFiles.Rules ? [.. bytes, (byte)' '] : bytes : null,
            pack: WithContext);
        var path = WriteBuild(invalid == "foreign" ? BuildSnapshot.Create(Mini) with { PackId = "foreign" } : null);
        if (invalid == "malformed") { File.WriteAllText(path, "{}"); }
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, invalid == "context" ? """{"bonus":100}""" : "{}");
        var run = await RunAsync("build", "apply", directory, path, WriteOperations("[]"), "--context", context);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(("error", code), Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ApplyBoundsOperationInputAndReportsMissingFiles()
    {
        var directory = WriteMini();
        var path = WriteBuild();
        var operations = Path.Combine(root, "operations.json");
        var missing = await RunAsync("build", "apply", directory, path, operations);
        Assert.Equal(CliApp.IoFailure, missing.ExitCode);
        Assert.Contains(("error", "io"), Diagnostics(missing, false));
        using (var stream = File.Create(operations)) { stream.SetLength(BuildDocument.MaxBytes + 1L); }
        var oversized = await RunAsync("build", "apply", directory, path, operations);
        Assert.Equal(CliApp.InvalidInput, oversized.ExitCode);
        Assert.Contains(("error", "input-too-large"), Diagnostics(oversized, false));
    }

    [Theory]
    [InlineData("build", "apply", "pack", "build")]
    [InlineData("build", "apply", "pack", "build", "ops", "--context")]
    [InlineData("build", "apply", "pack", "build", "ops", "--unknown", "context")]
    public async Task ApplyRejectsInvalidArgumentGrammar(params string[] args)
    {
        var run = await RunAsync(args);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        AssertInvalidCommand(run);
        Assert.StartsWith("Usage:", run.Error, StringComparison.Ordinal);
    }
}
