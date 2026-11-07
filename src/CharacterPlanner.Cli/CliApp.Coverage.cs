using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static object CoverageValue(EffectCoverageReport report) => new
    {
        note = report.Note,
        inputBasis = report.InputBasis,
        inputs = report.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(),
        party = report.Party.Select(buff => new
        {
            member = buff.Member,
            effect = buff.Effect.Id,
            name = buff.Effect.Name,
            source = buff.Effect.Source,
            basis = "Saved giver snapshot; not automatic trigger or recipient cooldown simulation.",
            modifiers = buff.Effect.Modifiers.Select(modifier => new { stat = modifier.Stat, value = modifier.Value })
        }),
        talents = report.Talents.Select(talent => new
        {
            slot = talent.Slot,
            talent = talent.Talent,
            name = talent.Name,
            active = talent.Active,
            modeled = talent.Modeled,
            auditStatus = talent.AuditStatus,
            description = talent.Description,
            channels = talent.Channels.Select(channel => new { kind = channel.Kind, sources = channel.Sources, note = channel.Note })
        })
    };
}
