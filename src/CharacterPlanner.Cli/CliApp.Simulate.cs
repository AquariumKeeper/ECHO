using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static readonly JsonSerializerOptions ResultJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static (string? Options, string? Context)? SimulateArguments(string[] rest)
    {
        string? options = null, context = null;
        for (var i = 0; i < rest.Length; i++)
        {
            if (i + 1 >= rest.Length) { return null; }
            if (rest[i] == "--options" && options is null) { options = rest[++i]; }
            else if (rest[i] == "--context" && context is null) { context = rest[++i]; }
            else { return null; }
        }
        return (options, context);
    }

    private static async Task<int> SimulateBuildAsync(string directory, string path, string? optionsPath, string? contextPath, TextWriter output, CancellationToken cancellationToken = default)
    {
        var validation = await LoadPackAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var build = await ReadBuildAsync(path, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        var request = optionsPath is null ? new SimulationRequest(new DummyFightOptions()) : await ReadSimulateOptionsAsync(optionsPath, cancellationToken).ConfigureAwait(false);
        var initialized = await ReadStateAsync(build, contextPath, new OperationApplier(compiled), cancellationToken).ConfigureAwait(false);
        if (!initialized.Succeeded) { return OperationFailure(output, contextPath ?? path, initialized.Errors); }
        var state = initialized.State;
        if (request.Options.Rotation is { } rotation) { CheckRotation(compiled.Pack, state, rotation.SecondSlot, optionsPath!); }
        DummyFightResult fight;
        // A refusal depends on the build, context and options together, so it names no single file.
        try { fight = request.Run(compiled, state, cancellationToken); }
        catch (SimulationException exception)
        { return Fail(output, Diagnostics(validation.Diagnostics).Append(new { severity = "error", code = exception.Code, path = "", message = exception.Message })); }
        var diagnostics = Diagnostics(validation.Diagnostics).Concat(fight.Diagnostics.Select(diagnostic =>
            new { severity = diagnostic.Severity, code = diagnostic.Code, path = diagnostic.Subject, message = diagnostic.Message }));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                pack = new { id = compiled.Pack.ID, version = compiled.Pack.Version },
                contract = fight.Contract,
                mode = JsonNamingPolicy.CamelCase.ConvertName(fight.Mode.ToString()),
                iterations = fight.Iterations,
                duration = fight.Duration,
                seed = fight.Seed,
                targets = request.UseTargets,
                weaponDps = fight.WeaponDps,
                skillDps = fight.SkillDps,
                statusDps = fight.StatusDps,
                talentDps = fight.TalentDps,
                totalDps = fight.TotalDps,
                shotsFired = fight.ShotsFired,
                reloads = fight.Reloads,
                swaps = fight.Swaps,
                kills = fight.Kills,
                killTimes = fight.KillTimes,
                timeToKill = fight.TimeToKill,
                killedFraction = fight.KilledFraction,
                timeToClear = fight.TimeToClear,
                clearedFraction = fight.ClearedFraction,
                timeline = fight.Timeline,
                incoming = fight.Incoming,
                uptime = new
                {
                    targetStatus = Sorted(fight.TargetStatusUptime),
                    timedTriggers = Sorted(fight.TimedTriggerUptime),
                    cyclePhases = fight.CyclePhaseUptime.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => Sorted(pair.Value), StringComparer.Ordinal),
                    magazinePhases = fight.MagazinePhaseUptime.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => Sorted(pair.Value), StringComparer.Ordinal)
                },
                events = request.Options.CaptureFirstFightEvents
                    ? fight.FirstFightEvents.Select(entry => new
                    {
                        time = entry.Time,
                        kind = JsonNamingPolicy.CamelCase.ConvertName(entry.Kind.ToString()),
                        amount = entry.Kind.CarriesAmount() ? entry.Damage : (double?)null,
                        slot = entry.WeaponSlot
                    })
                    : null,
                traceTruncated = fight.TraceTruncated,
                coverage = CoverageValue(fight.Coverage),
                limitations = fight.Limitations
            },
            diagnostics
        }, ResultJson));
        return Success;
    }

    /// <summary>The second weapon must be another equipped weapon slot; the engine would otherwise refuse it as a build error.</summary>
    private static void CheckRotation(GamePack pack, PlannerState state, string slot, string optionsPath)
    {
        if (!pack.Slots.Any(definition => definition.ID == slot && definition.Weapon))
        { throw new CliInputException("simulation-options-invalid", optionsPath, $"'rotation.secondSlot' \"{slot}\" is not a weapon slot"); }
        if (state.Build.Equipment.GetValueOrDefault(slot, "").Length == 0)
        { throw new CliInputException("simulation-options-invalid", optionsPath, $"'rotation.secondSlot' \"{slot}\" has no weapon equipped"); }
        if (state.Assumptions.GetValueOrDefault(GamePack.WeaponSlotSelector) == slot)
        { throw new CliInputException("simulation-options-invalid", optionsPath, $"'rotation.secondSlot' \"{slot}\" is already the active weapon"); }
    }

    private static Dictionary<string, T> Sorted<T>(IEnumerable<KeyValuePair<string, T>> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    // Strict like operation JSON: unknown, repeated and mistyped fields are refused, and every bound is checked here
    // so invalid options exit 3 instead of reaching the engine's argument checks.
    private static async Task<SimulationRequest> ReadSimulateOptionsAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = WithoutBom(await ReadInputAsync(path, cancellationToken).ConfigureAwait(false));
        CliInputException Invalid(string message) => new("simulation-options-invalid", path, message);
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw Invalid("Simulation options must be a JSON object"); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var options = new DummyFightOptions();
            var targets = true;
            double? retargetDelay = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var name = property.Name;
                var value = property.Value;
                if (!seen.Add(name)) { throw Invalid($"Options repeat '{name}'"); }
                switch (name)
                {
                    case "duration": options = options with { Duration = Number(value, name, 0, SimulationLimits.MaximumFightDuration, exclusiveMinimum: true) }; break;
                    case "iterations": options = options with { Iterations = (int)Whole(value, name, 1, 10000) }; break;
                    case "seed": options = options with { Seed = value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var seed) ? seed : throw Invalid("'seed' needs a whole number from 0") }; break;
                    case "mode":
                        options = options with
                        {
                            Mode = (value.ValueKind == JsonValueKind.String ? value.GetString() : null) switch
                            {
                                "random" => SimulationMode.Random,
                                "average" => SimulationMode.Average,
                                _ => throw Invalid("'mode' must be \"random\" or \"average\"")
                            }
                        };
                        break;
                    case "captureFirstFightEvents": options = options with { CaptureFirstFightEvents = Flag(value, name) }; break;
                    case "includeSkills": options = options with { IncludeSkills = Flag(value, name) }; break;
                    case "driveSimpleBuffs": options = options with { DriveSimpleBuffs = Flag(value, name) }; break;
                    case "includeIncoming": options = options with { IncludeIncoming = Flag(value, name) }; break;
                    case "includeShield": options = options with { IncludeShield = Flag(value, name) }; break;
                    // Deprecated 2026-11-02: overlapping Preservation repairs always run independently (R41). Still checked, so a saved
                    // request keeps working.
                    case "approximatePreservationOverlap": Flag(value, name); break;
                    case "distance": options = options with { Distance = Number(value, name, 0, DummyFightOptions.MaximumDistance) }; break;
                    case "pickupDelay": options = options with { PickupDelay = Number(value, name, 0, 60) }; break;
                    // A percentage, as the desktop's "Trophies collected (%)" field takes it.
                    case "pickupPercent": options = options with { PickupChance = Number(value, name, 0, 100) / 100 }; break;
                    case "rotation": options = options with { Rotation = Rotation(value) }; break;
                    case "targets": targets = Flag(value, name); break;
                    case "retargetDelay": retargetDelay = Number(value, name, 0, 60); break;
                    case "clearKills": options = options with { ClearKills = (int)Whole(value, name, 1, 10000) }; break;
                    case "targetSequence":
                        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > DummyFightOptions.MaximumTargetSequence)
                        { throw Invalid($"'targetSequence' needs a list of 1 to {DummyFightOptions.MaximumTargetSequence} enemy target profile numbers"); }
                        options = options with { TargetSequence = [.. value.EnumerateArray().Select(entry => (int)Whole(entry, name, 1, 1000))] };
                        break;
                    case "targetDistances":
                        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > DummyFightOptions.MaximumTargetSequence)
                        { throw Invalid($"'targetDistances' needs a list of 1 to {DummyFightOptions.MaximumTargetSequence} distances in metres"); }
                        options = options with { TargetDistances = [.. value.EnumerateArray().Select(entry => Number(entry, name, 0, DummyFightOptions.MaximumDistance))] };
                        break;
                    case "peekSeconds": options = options with { PeekSeconds = Number(value, name, DummyFightOptions.MinimumCoverPhase, DummyFightOptions.MaximumCoverPhase) }; break;
                    case "coverSeconds": options = options with { CoverSeconds = Number(value, name, DummyFightOptions.MinimumCoverPhase, DummyFightOptions.MaximumCoverPhase) }; break;
                    // A percentage of the normal enemy hit chance, as the desktop's "Hit chance while peeking" field takes it.
                    case "peekHitPercent": options = options with { PeekHitShare = Number(value, name, 0, 100) / 100 }; break;
                    case "enemiesAtOnce": options = options with { EnemiesAtOnce = (int)Whole(value, name, DummyFightOptions.MinimumEnemiesAtOnce, DummyFightOptions.MaximumEnemiesAtOnce) }; break;
                    case "enemiesShrink": options = options with { EnemiesShrink = Flag(value, name) }; break;
                    case "focusSeconds": options = options with { FocusSeconds = Number(value, name, DummyFightOptions.MinimumFocusSeconds, DummyFightOptions.MaximumFocusSeconds) }; break;
                    case "armorKits": options = options with { ArmorKits = (int)Whole(value, name, DummyFightOptions.MinimumArmorKits, DummyFightOptions.MaximumArmorKits) }; break;
                    case "armorKitAtPercent": options = options with { ArmorKitAtPercent = Number(value, name, DummyFightOptions.MinimumArmorKitPercent, DummyFightOptions.MaximumArmorKitPercent) }; break;
                    default: throw Invalid($"Unknown option '{name}'");
                }
            }
            if (!targets && retargetDelay is not null) { throw Invalid("'retargetDelay' applies only with targets"); }
            if (!targets && options.ClearKills is not null) { throw Invalid("'clearKills' applies only with targets"); }
            if (!targets && !options.TargetSequence.IsDefaultOrEmpty) { throw Invalid("'targetSequence' applies only with targets"); }
            if (!targets && !options.TargetDistances.IsDefaultOrEmpty) { throw Invalid("'targetDistances' applies only with targets"); }
            if ((options.ArmorKits is null) != (options.ArmorKitAtPercent is null))
            { throw Invalid("Armor kits need 'armorKits' and 'armorKitAtPercent' together"); }
            if ((options.PeekSeconds is null) != (options.CoverSeconds is null))
            { throw Invalid("A cover cycle needs 'peekSeconds' and 'coverSeconds' together"); }
            if (options.PeekHitShare is not null && options.PeekSeconds is null)
            { throw Invalid("'peekHitPercent' needs a cover cycle"); }
            if (options.IncludeIncoming && options.PeekSeconds is not null && options.PeekHitShare is null)
            { throw Invalid("A cover cycle with 'includeIncoming' needs 'peekHitPercent'"); }
            return new(options, targets, retargetDelay ?? 1);
        }
        catch (JsonException exception) { throw Invalid("Invalid simulation options JSON: " + exception.Message); }
        // Property names and strings throw on lone-surrogate escapes and invalid UTF-8.
        catch (InvalidOperationException) { throw Invalid("Simulation options contain text that is not valid Unicode"); }

        bool Flag(JsonElement value, string name) => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"'{name}' must be true or false")
        };
        double Number(JsonElement value, string name, double minimum, double maximum, bool exclusiveMinimum = false) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) &&
            (exclusiveMinimum ? number > minimum : number >= minimum) && number <= maximum
                ? number
                : throw Invalid($"'{name}' must be a number from {minimum}{(exclusiveMinimum ? " (exclusive)" : "")} to {maximum}");
        long Whole(JsonElement value, string name, long minimum, long maximum) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= minimum && number <= maximum
                ? number
                : throw Invalid($"'{name}' must be a whole number from {minimum} to {maximum}");
        RotationOptions Rotation(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) { throw Invalid("'rotation' must be an object"); }
            string? slot = null;
            var mode = WeaponSwapMode.WhenEmpty;
            var swapTime = 1d;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name)) { throw Invalid($"'rotation' repeats '{field.Name}'"); }
                switch (field.Name)
                {
                    case "secondSlot":
                        slot = field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is { Length: > 0 } text ? text : throw Invalid("'rotation.secondSlot' must be a slot id");
                        break;
                    case "mode":
                        mode = (field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null) switch
                        {
                            "whenEmpty" => WeaponSwapMode.WhenEmpty,
                            "afterKill" => WeaponSwapMode.AfterKill,
                            _ => throw Invalid("'rotation.mode' must be \"whenEmpty\" or \"afterKill\"")
                        };
                        break;
                    case "swapTime": swapTime = Number(field.Value, "rotation.swapTime", 0, 10, exclusiveMinimum: true); break;
                    default: throw Invalid($"Unknown rotation option '{field.Name}'");
                }
            }
            return new(slot ?? throw Invalid("'rotation' needs a secondSlot"), mode, swapTime);
        }
    }
}
