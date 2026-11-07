using System.Buffers.Text;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests : IDisposable
{
    private static readonly string Packs = Path.Combine(AppContext.BaseDirectory, "packs");
    private static readonly string DivisionV1 = Path.Combine(Packs, "division-2.json");
    private static readonly string DivisionProvenance = Path.Combine(Packs, "division-2.provenance.md");
    private static readonly string DivisionV2 = Path.Combine(Packs, "division-2");
    private static readonly string ConversionReference = Path.Combine(Packs, "conversion-reference");

    // A minimal pack without artwork, so it validates without an assets directory.
    private static readonly GamePack Mini = new()
    {
        ID = "mini",
        Name = "Mini",
        Version = "1",
        Level = 40,
        Stats = [new() { ID = "armor", Name = "Armor", Format = StatFormats.Integer, Formula = "0" }],
        Slots = [new() { ID = "mask", Name = "Mask" }],
        Items = [new() { ID = "mask_a", Name = "Mask A", Slot = "mask", Modifiers = [new() { Stat = "armor", Op = ModifierOps.Flat, Value = new(10) }] }],
        LegacyShareOrder = new() { Slots = ["mask"] }
    };

    private readonly string root = Path.Combine(Path.GetTempPath(), "planner-cli-" + Guid.NewGuid().ToString("N"));

    public CliAppTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
    }

    private sealed record Run(int ExitCode, string Output, string Error)
    {
        public JsonElement Json => JsonDocument.Parse(Output).RootElement;
    }

    private static async Task<Run> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync(args, output, error);
        return new(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>A grammar error: exit 2, a JSON invalid-command failure on stdout naming the usable grammars, usage on stderr.</summary>
    private static void AssertInvalidCommand(Run run)
    {
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        Assert.Equal([("error", "invalid-command")], Diagnostics(run, false));
        Assert.NotEmpty(run.Json.GetProperty("usage").EnumerateArray());
        Assert.False(run.Json.TryGetProperty("result", out _));
        Assert.StartsWith("Usage:", run.Error, StringComparison.Ordinal);
    }

    /// <summary>The (severity, code) of each diagnostic, after checking the envelope's shape.</summary>
    private static (string Severity, string Code)[] Diagnostics(Run run, bool ok)
    {
        var json = run.Json;
        Assert.Equal(ok, json.GetProperty("ok").GetBoolean());
        return [.. json.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
        {
            Assert.Equal(new[] { "severity", "code", "path", "message" }, diagnostic.EnumerateObject().Select(property => property.Name));
            Assert.All(diagnostic.EnumerateObject(), property => Assert.Equal(JsonValueKind.String, property.Value.ValueKind));
            return (diagnostic.GetProperty("severity").GetString()!, diagnostic.GetProperty("code").GetString()!);
        })];
    }

    private static Dictionary<string, byte[]> FilesIn(string directory) =>
        Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>The names of a directory's files and subdirectories, in ordinal order.</summary>
    private static string[] Entries(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    private string WriteMini(Func<string, byte[], byte[]>? edit = null, GamePack? pack = null)
    {
        var directory = Path.Combine(root, "mini");
        Directory.CreateDirectory(directory);
        foreach (var (name, bytes) in PackWriter.Write(pack ?? Mini))
        { File.WriteAllBytes(Path.Combine(directory, name), edit is null ? bytes.ToArray() : edit(name, bytes.ToArray())); }
        return directory;
    }

    [Fact]
    public async Task VersionReportsTheInformationalVersion()
    {
        var expected = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        foreach (var args in new[] { new[] { "version" }, new[] { "--json", "version" } })
        {
            var run = await RunAsync(args);
            Assert.Equal(CliApp.Success, run.ExitCode);
            Assert.Equal("", run.Error);
            Assert.EndsWith(Environment.NewLine, run.Output, StringComparison.Ordinal);
            Assert.True(run.Json.GetProperty("ok").GetBoolean());
            Assert.Equal(expected, run.Json.GetProperty("result").GetProperty("version").GetString());
        }
    }

    [Theory]
    [InlineData]
    [InlineData("help")]
    [InlineData("version", "extra")]
    [InlineData("pack")]
    [InlineData("pack", "validate")]
    [InlineData("pack", "validate", "a", "b")]
    [InlineData("pack", "convert-v1", "a")]
    [InlineData("pack", "convert-v1", "a", "b", "--force", "--force")]
    [InlineData("pack", "convert-v1", "a", "b", "--provenance")]
    [InlineData("pack", "convert-v1", "a", "b", "--provenance", "p", "--provenance", "q")]
    [InlineData("pack", "convert-v1", "a", "b", "--unknown")]
    [InlineData("share")]
    [InlineData("share", "convert", "a")]
    [InlineData("share", "convert", "a", "b", "c")]
    public async Task UsageErrorsExitWithTwoAnswerInJsonAndPrintUsage(params string[] args) => AssertInvalidCommand(await RunAsync(args));

    [Theory]
    [InlineData("Unknown command 'frobnicate'", "frobnicate")]
    [InlineData("Unknown command 'catalog frob'", "catalog", "frob", "x")]
    [InlineData("Invalid arguments for 'catalog list'", "catalog", "list")]
    [InlineData("Invalid arguments for 'catalog'", "catalog")]
    public async Task UsageErrorsSayWhetherTheCommandOrItsArgumentsAreWrong(string message, params string[] args)
    {
        var run = await RunAsync(args);
        AssertInvalidCommand(run);
        Assert.Equal(message, run.Json.GetProperty("diagnostics")[0].GetProperty("message").GetString());
        if (args.Length >= 2 && args[0] == "catalog")
        { Assert.All(run.Json.GetProperty("usage").EnumerateArray(), grammar => Assert.StartsWith("catalog ", grammar.GetString(), StringComparison.Ordinal)); }
    }

    [Fact]
    public async Task ValidateReportsAValidPack()
    {
        var directory = WriteMini();
        var run = await RunAsync("pack", "validate", directory);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, ok: true));
        var result = run.Json.GetProperty("result");
        Assert.Equal(Path.GetFullPath(directory), result.GetProperty("directory").GetString());
        Assert.Equal("mini", result.GetProperty("id").GetString());
        Assert.Equal("1", result.GetProperty("version").GetString());
        Assert.Equal(PackFiles.Sha256(File.ReadAllBytes(Path.Combine(directory, PackFiles.Manifest))), result.GetProperty("manifestSha256").GetString());
    }

    [Fact]
    public async Task ValidateFailsWithThreeOnABrokenFile()
    {
        var directory = WriteMini((name, bytes) => name == PackFiles.Rules ? [.. bytes, (byte)' '] : bytes);
        var run = await RunAsync("pack", "validate", directory);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Equal([("error", "file-size-mismatch")], Diagnostics(run, ok: false));
        Assert.Equal("mini", run.Json.GetProperty("result").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ValidateFailsWithFourOnAMissingDirectory()
    {
        var run = await RunAsync("pack", "validate", Path.Combine(root, "missing"));
        Assert.Equal(CliApp.IoFailure, run.ExitCode);
        Assert.Equal([("error", "io")], Diagnostics(run, ok: false));
        Assert.Equal("", run.Error);
    }

    [Fact]
    public async Task ConvertV1ReproducesThePinnedConversionReference()
    {
        var output = Path.Combine(root, "division-2");
        var run = await RunAsync("pack", "convert-v1", DivisionV1, output, "--provenance", DivisionProvenance);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Diagnostics(run, ok: true);
        var result = run.Json.GetProperty("result");
        Assert.Equal(Path.GetFullPath(output), result.GetProperty("output").GetString());
        var expected = FilesIn(ConversionReference);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), result.GetProperty("files").EnumerateArray().Select(file => file.GetString()));
        Assert.Equal(Entries(ConversionReference), Entries(output));
        var actual = FilesIn(output);
        Assert.All(expected, pair => Assert.True(pair.Value.AsSpan().SequenceEqual(actual[pair.Key]), $"{pair.Key} differs from the pinned conversion reference"));
        // Nothing is left beside the output, such as a staging directory.
        Assert.Equal([output], Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task ConvertV1RefusesANonEmptyOutputAndForceReplacesIt()
    {
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(Path.Combine(output, "stale"));
        var stale = Path.Combine(output, "stale.txt");
        File.WriteAllText(stale, "stale");

        var refused = await RunAsync("pack", "convert-v1", DivisionV1, output);
        Assert.Equal(CliApp.InvalidInput, refused.ExitCode);
        Assert.Equal([("error", "output-not-empty")], Diagnostics(refused, ok: false));
        Assert.Equal(Path.GetFullPath(output), refused.Json.GetProperty("diagnostics")[0].GetProperty("path").GetString());
        Assert.Equal(["stale", "stale.txt"], Entries(output));

        // Without --provenance no provenance.md is written or listed in the manifest.
        var forced = await RunAsync("pack", "convert-v1", DivisionV1, output, "--force");
        Assert.Equal(CliApp.Success, forced.ExitCode);
        Diagnostics(forced, ok: true);
        string[] expected = [PackFiles.Manifest, PackFiles.Presentation, PackFiles.Rules, PackFiles.ShareCodes, PackFiles.Simulation];
        Assert.Equal(expected, Entries(output));
        Assert.Equal(expected, forced.Json.GetProperty("result").GetProperty("files").EnumerateArray().Select(file => file.GetString()));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, PackFiles.Manifest)));
        Assert.False(manifest.RootElement.GetProperty("files").TryGetProperty(PackFiles.Provenance, out _));
        // The rules match the pinned conversion reference; only the manifest and provenance differ.
        Assert.Equal(File.ReadAllBytes(Path.Combine(ConversionReference, PackFiles.Rules)), File.ReadAllBytes(Path.Combine(output, PackFiles.Rules)));
        Assert.Equal([output], Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task ConvertV1ReportsInvalidAndMissingSources()
    {
        var output = Path.Combine(root, "out");
        var invalid = Path.Combine(root, "invalid.json");
        File.WriteAllText(invalid, "{");
        var schema = await RunAsync("pack", "convert-v1", invalid, output);
        Assert.Equal(CliApp.InvalidInput, schema.ExitCode);
        Assert.Equal([("error", "v1-schema")], Diagnostics(schema, ok: false));

        var missing = await RunAsync("pack", "convert-v1", Path.Combine(root, "missing.json"), output);
        Assert.Equal(CliApp.IoFailure, missing.ExitCode);
        Assert.Equal([("error", "io")], Diagnostics(missing, ok: false));

        var provenance = await RunAsync("pack", "convert-v1", DivisionV1, output, "--provenance", Path.Combine(root, "missing.md"));
        Assert.Equal(CliApp.IoFailure, provenance.ExitCode);
        Assert.Equal([("error", "io")], Diagnostics(provenance, ok: false));
        Assert.False(Directory.Exists(output));
    }

    /// <summary>A Go-era v1 code: a positional tuple, raw DEFLATE, base64url. Equipment is in the pack's legacy slot order.</summary>
    private static string LegacyCode(string packId, string mask)
    {
        var tuple = $$"""[1,"{{packId}}","old",40,[],["{{mask}}",null,null,null,null,null,null,null,null],"Imported","Go notes"]""";
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal)) { deflate.Write(Encoding.UTF8.GetBytes(tuple)); }
        return Base64Url.EncodeToString(compressed.ToArray());
    }

    /// <summary>The committed Division pack has no artwork; converting a code needs only its structure.</summary>
    [Fact]
    public async Task ShareConvertTurnsAGoCodeIntoTheAppsFormat()
    {
        var run = await RunAsync("share", "convert", DivisionV2, LegacyCode("division_2", "mask_n_5_11_tactical_mask"));
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, ok: true));
        var result = run.Json.GetProperty("result");
        Assert.Equal("legacy", result.GetProperty("format").GetString());
        Assert.Equal("Imported", result.GetProperty("name").GetString());
        Assert.Equal("old", result.GetProperty("packVersion").GetString());
        Assert.Equal(0, result.GetProperty("droppedFields").GetArrayLength());
        var code = result.GetProperty("code").GetString()!;
        var build = ShareCode.Decode(code);
        Assert.Equal("mask_n_5_11_tactical_mask", build.Equipment["mask"]);
        Assert.Equal("", build.Equipment["sidearm"]);
        Assert.Equal("Go notes", build.Meta.Notes);

        // An app code converts to itself.
        var again = await RunAsync("share", "convert", DivisionV2, code);
        Assert.Equal(CliApp.Success, again.ExitCode);
        Assert.Equal("app", again.Json.GetProperty("result").GetProperty("format").GetString());
        Assert.Equal(code, again.Json.GetProperty("result").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ShareConvertRejectsUnreadableAndForeignCodes()
    {
        var invalid = await RunAsync("share", "convert", DivisionV2, "not-a-share-code");
        Assert.Equal(CliApp.InvalidInput, invalid.ExitCode);
        Assert.Equal([("error", "share-code-invalid")], Diagnostics(invalid, ok: false));

        var foreign = await RunAsync("share", "convert", DivisionV2, LegacyCode("other_pack", "mask_a"));
        Assert.Equal(CliApp.InvalidInput, foreign.ExitCode);
        Assert.Equal([("error", "share-code-wrong-pack")], Diagnostics(foreign, ok: false));

        var missing = await RunAsync("share", "convert", Path.Combine(root, "absent"), "code");
        Assert.Equal(CliApp.IoFailure, missing.ExitCode);
    }

    [Fact]
    public async Task DescribeReportsTheImplementedSurfaceAndExitCodes()
    {
        var run = await RunAsync("describe");
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        var result = run.Json.GetProperty("result");
        Assert.Equal("d2p-cli-v1", result.GetProperty("contract").GetString());
        var commands = result.GetProperty("commands").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(25, commands.Distinct().Count());
        Assert.Equal(CharacterPlanner.Application.Commands.OperationKinds.Supported,
            result.GetProperty("operationKinds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(CharacterPlanner.Application.Commands.OperationKinds.Removed,
            result.GetProperty("removedOperationKinds").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("build new <pack-directory>", commands);
        Assert.Contains("build simulate <pack-directory> <build.json> [--options <options.json>] [--context <context.json>]", commands);
        Assert.Contains("build share <pack-directory> <build.json>", commands);
        Assert.Contains("library save <build.json> [--replace <id> --expected-version <version>] [--library <directory>]", commands);
        Assert.Contains("build compare <pack-directory> <a.json> <b.json> [--context <context.json>]", commands);
        Assert.Contains("build rank <pack-directory> <build.json> <slot> <stat> [--context <context.json>]", commands);
        Assert.Equal(new[] { "items", "slots", "attributes", "mods", "talents", "skills", "specializations", "upgrades" },
            result.GetProperty("catalogKinds").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("build resolve <pack-directory> <build.json> [--context <context.json>]", commands);
        Assert.Equal(3, result.GetProperty("exitCodes").GetProperty("invalidInput").GetInt32());
        Assert.Equal(5, result.GetProperty("exitCodes").GetProperty("internal").GetInt32());
        Assert.Equal(6, result.GetProperty("exitCodes").GetProperty("staleRevision").GetInt32());
        Assert.Equal(BuildDocument.SchemaVersion, result.GetProperty("buildSchemaVersion").GetInt32());
    }

    [Fact]
    public async Task NewBuildReturnsACanonicalV2DocumentWithoutWritingAFile()
    {
        var directory = WriteMini();
        var before = Entries(root);
        var run = await RunAsync("build", "new", directory);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        var build = BuildDocument.Decode(Encoding.UTF8.GetBytes(run.Json.GetProperty("result").GetRawText()));
        Assert.Equal(Mini.ID, build.PackId);
        Assert.Equal(Mini.Version, build.PackVersion);
        Assert.Equal("", build.Equipment["mask"]);
        Assert.Equal(before, Entries(root));
    }

    private string WriteBuild(BuildSnapshot? build = null)
    {
        var path = Path.Combine(root, "build.json");
        File.WriteAllBytes(path, BuildDocument.Encode(build ?? BuildSnapshot.Create(Mini).WithEquipment("mask", "mask_a")));
        return path;
    }

    private static GamePack WithContext => Mini with
    {
        Stats = [new() { ID = "armor", Name = "Armor", Formula = "context_value('bonus')" }],
        NumericContextInputs = [new() { ID = "bonus", Name = "Bonus", Min = 0, Max = 10, Step = 0.5 }],
        Selectors = [new() { ID = "enabled", Name = "Enabled", Values = ["true", "false"] }]
    };

    [Fact]
    public async Task ResolveUsesCoreAndApplicationContextValidationWithoutChangingTheDocument()
    {
        var directory = WriteMini(pack: WithContext);
        var path = WriteBuild();
        var original = File.ReadAllBytes(path);
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, "{\"bonus\":3.5,\"enabled\":\"true\"}", new UTF8Encoding(true));
        var run = await RunAsync("build", "resolve", directory, path, "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        var result = run.Json.GetProperty("result");
        Assert.Equal(13.5, result.GetProperty("stats").GetProperty("armor").GetDouble());
        Assert.Equal(3.5, result.GetProperty("assumptions").GetProperty("numeric").GetProperty("bonus").GetDouble());
        Assert.Equal("true", result.GetProperty("assumptions").GetProperty("explicit").GetProperty("enabled").GetString());
        Assert.Contains("bonus", result.GetProperty("readAssumptions").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(original, File.ReadAllBytes(path));
        var fixedRun = await RunAsync("build", "resolve", directory, path);
        Assert.Equal(10, fixedRun.Json.GetProperty("result").GetProperty("stats").GetProperty("armor").GetDouble());
    }

    [Theory]
    [InlineData("[]", "context-invalid")]
    [InlineData("{\"bonus\":1,\"bonus\":2}", "context-invalid")]
    [InlineData("{\"bonus\":true}", "context-invalid")]
    [InlineData("{\"bonus\":1e400}", "context-invalid")]
    [InlineData("{\"unknown\":1}", "unknown-context")]
    [InlineData("{\"bonus\":\"2\"}", "context-needs-value")]
    [InlineData("{\"bonus\":3.25}", "context-value-invalid")]
    [InlineData("{\"enabled\":\"bad\"}", "context-value-invalid")]
    [InlineData("{\"weapon_slot\":\"mask\"}", "context-value-invalid")]
    [InlineData("{\"\\ud800\":\"x\"}", "context-invalid")]
    public async Task ResolveRejectsMalformedOrInvalidAssumptionsWithoutAPartialResult(string json, string code)
    {
        var directory = WriteMini(pack: WithContext);
        var path = WriteBuild();
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, json);
        var run = await RunAsync("build", "resolve", directory, path, "--context", context);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(("error", code), Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ResolverWarningsRemainSuccessfulAndKeepTheirProvenance()
    {
        var pack = Mini with
        {
            ActiveSkillSlots = [new() { ID = "skill_slot", Name = "Skill", Choices = ["test_skill"] }],
            ActiveSkills = [new() { ID = "test_skill", Name = "Test", Family = "test" }]
        };
        var directory = WriteMini(pack: pack);
        var run = await RunAsync("build", "resolve", directory, WriteBuild());
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Contains(("warning", "active-skill-unconfigured"), Diagnostics(run, true));
        Assert.Equal("skill_slot", run.Json.GetProperty("diagnostics").EnumerateArray()
            .Single(value => value.GetProperty("code").GetString() == "active-skill-unconfigured").GetProperty("path").GetString());
        Assert.Equal(10, run.Json.GetProperty("result").GetProperty("stats").GetProperty("armor").GetDouble());
    }

    [Theory]
    [InlineData("new")]
    [InlineData("resolve")]
    public async Task EmptyPackPathsAreStructuredInputFailures(string command)
    {
        var run = command == "new" ? await RunAsync("build", command, "") : await RunAsync("build", command, "", "unused");
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Equal([("error", "pack-path-invalid")], Diagnostics(run, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedBuildOrContextInputIsBounded(bool contextTooLarge)
    {
        var directory = WriteMini();
        var path = WriteBuild();
        var large = Path.Combine(root, "large.json");
        File.WriteAllBytes(large, new byte[BuildDocument.MaxBytes + 1]);
        var run = contextTooLarge ? await RunAsync("build", "resolve", directory, path, "--context", large)
            : await RunAsync("build", "resolve", directory, large);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Equal([("error", "input-too-large")], Diagnostics(run, false));
    }

    [Theory]
    [InlineData("invalid", "build-invalid", 3)]
    [InlineData("foreign", "build-wrong-pack", 3)]
    [InlineData("equipment", "unknown-item", 3)]
    [InlineData("missing", "io", 4)]
    [InlineData("empty", "input-path-invalid", 3)]
    public async Task ResolveReportsBuildAndIoErrors(string kind, string code, int exitCode)
    {
        var directory = WriteMini();
        var path = kind == "empty" ? "" : Path.Combine(root, "build.json");
        if (kind == "invalid") { File.WriteAllText(path, "{invalid"); }
        if (kind == "foreign") { WriteBuild(BuildSnapshot.Create(Mini) with { PackId = "elsewhere" }); }
        if (kind == "equipment") { WriteBuild(BuildSnapshot.Create(Mini).WithEquipment("mask", "missing_item")); }
        var run = await RunAsync("build", "resolve", directory, path);
        Assert.Equal(exitCode, run.ExitCode);
        Assert.Contains(("error", code), Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("resolve")]
    public async Task BuildCommandsRequireAValidPack(string command)
    {
        var directory = WriteMini((name, bytes) => name == PackFiles.Rules ? [.. bytes, (byte)' '] : bytes);
        var run = command == "new" ? await RunAsync("build", command, directory) : await RunAsync("build", command, directory, WriteBuild());
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Equal([("error", "file-size-mismatch")], Diagnostics(run, false));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("build", "new")]
    [InlineData("build", "resolve", "pack")]
    [InlineData("build", "resolve", "pack", "build", "--unknown", "value")]
    [InlineData("build", "resolve", "pack", "build", "--context")]
    [InlineData("describe", "extra")]
    public async Task BuildCommandGrammarErrorsAnswerInJson(params string[] args)
    {
        AssertInvalidCommand(await RunAsync(args));
    }


    private static GamePack CatalogPack => Mini with
    {
        NumericContextInputs = [new() { ID = "bonus", Name = "Bonus", Min = 0, Max = 10, Step = 1 }],
        Attributes = [new() { ID = "armor_roll", Name = "Armor roll", Stat = "armor", Min = 0, Max = 20, Step = 1 }],
        Mods = [new() { ID = "mod", Name = "Test mod", RollAttributeID = "armor_roll", Modifiers = [new() { Stat = "armor", Op = "flat", Value = new(2) }] }],
        Talents = [new() { ID = "talent", Name = "Test talent", Modifiers = [new() { Stat = "armor", Op = "flat", Value = new(1) }] }],
        Items = [new() { ID = "mask_z", Name = "Z mask", Slot = "mask", Description = "Configurable mask",
            Modifiers = [new() { Stat = "armor", Op = "flat", Value = new("2 + context_value('bonus')") }],
            AttributeSlots = [new() { ID = "minor", Name = "Minor", Choices = [new() { AttributeID = "armor_roll", Min = 2, Max = 10, Step = 2 }] }],
            ModSlots = [new() { ID = "mod_slot", Name = "Mod", Choices = ["mod"] }],
            TalentSlots = [new() { ID = "talent_slot", Name = "Talent", Choices = ["talent"] }] }, Mini.Items[0]],
        ActiveSkillSlots = [new() { ID = "skill_slot", Name = "Skill", Choices = ["skill"] }],
        ActiveSkills = [new() { ID = "skill", Name = "Test skill", Family = "test",
            PreviewOutputs = [new() { ID = "damage", Name = "Damage", Formula = "10", Kind = PreviewOutputKinds.Damage }] }],
        Specializations = [new() { ID = "spec", Name = "Test specialization", Budgets = [new() { ID = "points", Name = "Points", Points = 5 }],
            Nodes = [new() { ID = "node", Name = "Node", BudgetID = "points", Cost = 1 }] }],
        AccountUpgrades = [new() { ID = "watch", Name = "Test upgrade", MaxRank = 10, EffectCap = 5,
            ModifiersPerRank = [new() { Stat = "armor", Op = "flat", Value = new(1) }] }]
    };

    [Theory]
    [InlineData("items", "mask_z")]
    [InlineData("slots", "mask")]
    [InlineData("attributes", "armor_roll")]
    [InlineData("mods", "mod")]
    [InlineData("talents", "talent")]
    [InlineData("skills", "skill")]
    [InlineData("specializations", "spec")]
    [InlineData("upgrades", "watch")]
    public async Task CatalogListsAndShowsEachPublishedKind(string kind, string id)
    {
        var directory = WriteMini(pack: CatalogPack);
        var listed = await RunAsync("catalog", "list", directory, kind);
        Assert.Equal(CliApp.Success, listed.ExitCode);
        Assert.Empty(Diagnostics(listed, true));
        var result = listed.Json.GetProperty("result");
        Assert.Equal(kind, result.GetProperty("kind").GetString());
        var entries = result.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(entries.Length, result.GetProperty("count").GetInt32());
        var ids = entries.Select(entry => entry.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        var selected = entries.Single(entry => entry.GetProperty("id").GetString() == id);
        var shown = await RunAsync("catalog", "show", directory, kind, id);
        Assert.Equal(CliApp.Success, shown.ExitCode);
        Assert.Empty(Diagnostics(shown, true));
        var detail = shown.Json.GetProperty("result").GetProperty("entry");
        Assert.Equal(id, detail.GetProperty("id").GetString());
        Assert.Equal(selected.GetProperty("name").GetString(), detail.GetProperty("name").GetString());
        if (kind == "skills")
        {
            var slot = Assert.Single(result.GetProperty("skillSlots").EnumerateArray());
            Assert.Equal("skill_slot", slot.GetProperty("id").GetString());
            Assert.Contains(id, slot.GetProperty("choices").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(slot.GetRawText(), shown.Json.GetProperty("result").GetProperty("skillSlots")[0].GetRawText());
        }
        else { Assert.Equal(JsonValueKind.Null, result.GetProperty("skillSlots").ValueKind); }
    }

    [Fact]
    public async Task CatalogShowExposesExpandedChoicesAndMeaningfulModifierValues()
    {
        var directory = WriteMini(pack: CatalogPack);
        var shown = await RunAsync("catalog", "show", directory, "items", "mask_z");
        Assert.Equal(CliApp.Success, shown.ExitCode);
        var item = shown.Json.GetProperty("result").GetProperty("entry");
        Assert.Equal("2 + context_value('bonus')", item.GetProperty("modifiers")[0].GetProperty("value").GetString());
        var choice = item.GetProperty("attributeSlots")[0].GetProperty("choices")[0];
        Assert.Equal("armor_roll", choice.GetProperty("attributeID").GetString());
        Assert.Equal(2, choice.GetProperty("min").GetDouble());
        Assert.Equal(10, choice.GetProperty("max").GetDouble());
        Assert.Equal("mod", item.GetProperty("modSlots")[0].GetProperty("choices")[0].GetString());
        Assert.Equal("talent", item.GetProperty("talentSlots")[0].GetProperty("choices")[0].GetString());
        var mod = await RunAsync("catalog", "show", directory, "mods", "mod");
        Assert.Equal(2, mod.Json.GetProperty("result").GetProperty("entry").GetProperty("modifiers")[0].GetProperty("value").GetDouble());
        var skill = await RunAsync("catalog", "show", directory, "skills", "skill");
        Assert.Equal("10", skill.Json.GetProperty("result").GetProperty("entry").GetProperty("previewOutputs")[0].GetProperty("formula").GetString());
        var spec = await RunAsync("catalog", "show", directory, "specializations", "spec");
        Assert.Equal("node", spec.Json.GetProperty("result").GetProperty("entry").GetProperty("nodes")[0].GetProperty("id").GetString());
        var upgrade = await RunAsync("catalog", "show", directory, "upgrades", "watch");
        Assert.Equal(10, upgrade.Json.GetProperty("result").GetProperty("entry").GetProperty("maxRank").GetInt64());
    }

    [Theory]
    [InlineData("attributes")]
    [InlineData("mods")]
    [InlineData("talents")]
    [InlineData("skills")]
    [InlineData("specializations")]
    [InlineData("upgrades")]
    public async Task EmptyCatalogCollectionsAreSuccessful(string kind)
    {
        var run = await RunAsync("catalog", "list", WriteMini(), kind);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        Assert.Equal(0, run.Json.GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(0, run.Json.GetProperty("result").GetProperty("entries").GetArrayLength());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Items")]
    public async Task UnknownCatalogKindsReturnStructuredQueryErrorsBeforeIo(string kind)
    {
        var run = await RunAsync("catalog", "list", Path.Combine(root, "missing"), kind);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        Assert.Equal([("error", "catalog-kind-unknown")], Diagnostics(run, false));
        Assert.Empty(run.Error);
    }

    [Theory]
    [InlineData("items", "absent")]
    [InlineData("slots", "absent")]
    [InlineData("attributes", "absent")]
    [InlineData("mods", "absent")]
    [InlineData("talents", "absent")]
    [InlineData("skills", "absent")]
    [InlineData("specializations", "absent")]
    [InlineData("upgrades", "absent")]
    [InlineData("items", "MASK_Z")]
    public async Task CatalogShowRequiresAnExactId(string kind, string id)
    {
        var run = await RunAsync("catalog", "show", WriteMini(pack: CatalogPack), kind, id);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        Assert.Equal([("error", "catalog-id-not-found")], Diagnostics(run, false));
        Assert.Equal(id, run.Json.GetProperty("diagnostics")[0].GetProperty("path").GetString());
        Assert.False(run.Json.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogRequiresAValidPackForListsAndDetails(bool show)
    {
        var directory = WriteMini((name, bytes) => name == PackFiles.Rules ? [.. bytes, (byte)' '] : bytes);
        var run = show ? await RunAsync("catalog", "show", directory, "items", "mask_a") : await RunAsync("catalog", "list", directory, "items");
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Equal([("error", "file-size-mismatch")], Diagnostics(run, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogPathFailuresKeepTheirInputOrIoExitCode(bool empty)
    {
        var run = await RunAsync("catalog", "list", empty ? "" : Path.Combine(root, "missing"), "items");
        Assert.Equal(empty ? CliApp.InvalidInput : CliApp.IoFailure, run.ExitCode);
        Assert.Equal([("error", empty ? "pack-path-invalid" : "io")], Diagnostics(run, false));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("catalog", "list", "pack")]
    [InlineData("catalog", "show", "pack", "items")]
    [InlineData("catalog", "list", "pack", "items", "--query", "name")]
    [InlineData("catalog", "show", "pack", "items", "id", "extra")]
    public async Task CatalogGrammarErrorsAnswerInJson(params string[] args)
    {
        AssertInvalidCommand(await RunAsync(args));
    }

}
