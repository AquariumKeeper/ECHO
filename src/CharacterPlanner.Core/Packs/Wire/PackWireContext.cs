using System.Text.Json.Serialization;

namespace CharacterPlanner.Core.Packs.Wire;

/// <summary>
/// Compile-time JSON metadata for the pack files and the definitions the writer names by content. The strict settings
/// (<see cref="WireJson.Options"/>) still apply: this context only supplies how each type is built and read, so it
/// generates metadata alone, not serialization code those settings would bypass.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ManifestWire))]
[JsonSerializable(typeof(RulesWire))]
[JsonSerializable(typeof(SimulationWire))]
[JsonSerializable(typeof(PresentationWire))]
[JsonSerializable(typeof(ShareCodesWire))]
[JsonSerializable(typeof(List<AttributeSlotWire>))]
[JsonSerializable(typeof(List<ChoiceSlotWire>))]
[JsonSerializable(typeof(UpgradeWire))]
internal sealed partial class PackWireContext : JsonSerializerContext;
