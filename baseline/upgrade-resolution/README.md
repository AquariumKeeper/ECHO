# Ranked upgrade contracts

`pack.json` is a synthetic pack accepted by the frozen validator. The 21 cases in
`inputs.json` isolate capped account ranks, additive/increased bonuses, zero ranks,
item-bound upgrade identity, legal rank bounds, unknown slots/items, primary-slot
ownership, missing upgrade definitions, inactive requirements and item context gates.

`legacy-v1.json` records complete input documents, explicit contexts, exit codes
and responses from the checksum-pinned C0 release. Regenerate deliberately with:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite upgrade-resolution
```

The same isolated capture and numeric comparison policy as the
[basic resolution corpus](../resolution/README.md) applies. The snapshot pins
pack/input/release hashes. Ordinary .NET tests require only committed files.
Successful cases compare all numerical results and ordered contributions,
including source rank, selected-rank labels and equipment slot identity. Invalid
cases compare exact diagnostic fields and order. Reference CLI errors omit
computed results; separate Core tests verify retained partial calculations.

Ranked `override` modifiers are rejected by the full pack validator and are not
part of this accepted-pack corpus. Prototype and active-skill upgrades are outside
this stage's scope.
