# Party effect contracts

The synthetic pack and 14 captured cases isolate saved ticked values, strongest
total contribution per stack, earliest ties, ordinal stack order, missing/unknown
saved fields, inactive/unknown effects, negative or empty buffs and party-before-
equipment contribution order. Receiving never reevaluates a saved formula from
the member's armor, code or current build.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite party-resolution
```

The [basic corpus policy](../resolution/README.md) applies: isolated executable,
pack/build inputs, no library commands, complete responses and recorded hashes.
Normal tests consume committed files only. Cases compare all calculation channels
and ordered contribution provenance. Repeated effects across members are valid;
duplicate effect IDs within one member are a separate portable-document rejection.

Six focused Core tests additionally cover giver-side snapshots using resolved
stats without the giver's own party, item activity/context gates, manual giver
scopes, stable stack/tie order and empty buffs winning against negative totals.
The original C0 party-buff build is now included in both context modes, completing
the engine-channel comparison for all six original Division 2 builds.
