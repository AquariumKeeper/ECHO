using System.Text.Json;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Core.Packs.Wire;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static readonly string[] CatalogKinds = ["items", "slots", "attributes", "mods", "talents", "skills", "specializations", "upgrades"];
    private static readonly JsonSerializerOptions CatalogJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ModifierValueConverter() }
    };
    private sealed record CatalogEntry(string Id, string Name, string Description, object Definition);

    private static async Task<int> CatalogAsync(string directory, string kind, string? id, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (!CatalogKinds.Contains(kind, StringComparer.Ordinal))
        { throw new CliInputException("catalog-kind-unknown", kind, $"Unknown catalog kind '{kind}'; choose {string.Join(", ", CatalogKinds)}", UsageError); }
        var validation = await LoadPackAsync(directory, metadataOnly: true, cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var pack = compiled.Pack;
        var skillSlots = kind == "skills" ? pack.ActiveSkillSlots.OrderBy(slot => slot.ID, StringComparer.Ordinal)
            .Select(slot => new { id = slot.ID, name = slot.Name, choices = slot.Choices }).ToArray() : null;
        var entries = CatalogEntries(pack, kind);
        if (id is null)
        {
            var listed = entries.OrderBy(entry => entry.Id, StringComparer.Ordinal)
                .Select(entry => new { id = entry.Id, name = entry.Name, description = entry.Description }).ToArray();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                result = new { pack = new { id = pack.ID, version = pack.Version }, kind, count = listed.Length, entries = listed, skillSlots },
                diagnostics = Diagnostics(validation.Diagnostics)
            }));
        }
        else
        {
            var entry = entries.FirstOrDefault(entry => entry.Id == id);
            if (entry is null) { throw new CliInputException("catalog-id-not-found", id, $"Catalog '{kind}' has no id '{id}'", UsageError); }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                result = new { pack = new { id = pack.ID, version = pack.Version }, kind, entry = CatalogDefinition(entry.Definition), skillSlots },
                diagnostics = Diagnostics(validation.Diagnostics)
            }, CatalogJson));
        }
        return Success;
    }

    private static IEnumerable<CatalogEntry> CatalogEntries(GamePack pack, string kind) => kind switch
    {
        "items" => pack.Items.Select(value => new CatalogEntry(value.ID, value.Name, value.Description, value)),
        "slots" => pack.Slots.Select(value => new CatalogEntry(value.ID, value.Name, "", value)),
        "attributes" => pack.Attributes.Select(value => new CatalogEntry(value.ID, value.Name, "", value)),
        "mods" => pack.Mods.Select(value => new CatalogEntry(value.ID, value.Name, value.Description, value)),
        "talents" => pack.Talents.Select(value => new CatalogEntry(value.ID, value.Name, value.Description, value)),
        "skills" => pack.ActiveSkills.Select(value => new CatalogEntry(value.ID, value.Name, value.Description, value)),
        "specializations" => pack.Specializations.Select(value => new CatalogEntry(value.ID, value.Name, value.Description, value)),
        "upgrades" => pack.AccountUpgrades.Select(value => new CatalogEntry(value.ID, value.Name, value.Description, value)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // Sort nested objects as well as top-level collections, without changing authored array order.
    private static JsonElement CatalogDefinition(object definition)
    {
        var element = JsonSerializer.SerializeToElement(definition, CatalogJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { WriteOrdered(writer, element); }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        return document.RootElement.Clone();
    }

    private static void WriteOrdered(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteOrdered(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var value in element.EnumerateArray()) { WriteOrdered(writer, value); }
            writer.WriteEndArray();
        }
        else { element.WriteTo(writer); }
    }
}
