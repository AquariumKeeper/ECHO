using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A build read from a share code, with the removed Go-era fields the code held values for (none for an app code).</summary>
public sealed record ImportedBuild(BuildSnapshot Build, ImmutableArray<string> DroppedFields)
{
    /// <summary>Reads this app's codes only; the app itself is given a reader that also takes Go-era codes.</summary>
    public static ImportedBuild ReadAppCode(string code) => new(ShareCode.Decode(code), []);
}

/// <summary>What Share shows: the build's code, or why it can't be shared.</summary>
public sealed record SharedCode(string Code, string? Problem);

/// <summary>
/// Share and Import (F51, F93), as the Go app's <c>sharedialog.go</c>: the build as a code to copy, and a pasted code
/// loaded as the build. An import is opened as Open opens a saved build: a fresh history, after asking before
/// unsaved changes are discarded.
/// </summary>
public sealed class SharingViewModel
{
    public const string ShareTitle = "Share Build";
    public const string ShareLabel = "Share this code so someone else can load your build:";
    public const string CopyLabel = "Copy to Clipboard";
    public const string ImportTitle = "Import Build";
    public const string ImportLabel = "Paste a share code to load a build:";
    public const string ImportPlaceholder = "Paste a share code";

    private readonly EditorViewModel editor;

    internal SharingViewModel(EditorViewModel editor) => this.editor = editor;

    /// <summary>The build as a share code (its party isn't part of it), or why it can't be shared.</summary>
    public SharedCode Share()
    {
        try { return new(ShareCode.Encode(editor.Session.Snapshot.State.Build), null); }
        catch (ArgumentException exception) { return new("", $"This build can't be shared: {exception.Message}"); }
    }

    /// <summary>Reads a pasted code in either format; null with <paramref name="problem"/> saying why it can't be loaded.</summary>
    public ImportedBuild? Read(string? code, out string problem)
    {
        problem = "";
        ImportedBuild import;
        try { import = editor.ReadShareCode(code ?? ""); }
        catch (ShareCodeException exception) { problem = exception.Message; return null; }
        if (import.Build.PackId != editor.Pack.ID)
        {
            problem = $"The share code is for pack \"{import.Build.PackId}\", not \"{editor.Pack.ID}\"";
            return null;
        }
        return import;
    }

    /// <summary>
    /// Loads a read code as the build, as Open does, and says what a Go-era code held that this planner leaves out.
    /// Declining to discard unsaved changes leaves the build as it was, with the editor's message saying so.
    /// </summary>
    public async Task ImportAsync(ImportedBuild import)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (!await editor.OpenImportedAsync(import.Build) || import.DroppedFields.IsDefaultOrEmpty) { return; }
        editor.Message = $"Loaded. The share code also held {string.Join(", ", import.DroppedFields.Select(Field))}, which this planner no longer models.";
    }

    private static string Field(string field) => field switch
    {
        "allocations" => "attribute allocations",
        "activeScenarios" => "active scenarios",
        "allocatedSkills" => "passive skill points",
        _ => field
    };
}
