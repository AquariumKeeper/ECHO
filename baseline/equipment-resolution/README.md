# Configured equipment contracts

`pack.json` is a small, synthetic pack accepted by the frozen reference validator.
It isolates item-slot attribute overrides, count stats, fixed and rolled mods,
talent set-piece credit, item context gates, and ordinary/holstered talent effects.
`inputs.json` applies common configuration followed by each case's overrides to a
fresh reference build. `legacy-v1.json` records 21 complete inputs and responses
from the checksum-pinned C0 release. Capture with:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite equipment-resolution
```

The capture uses the same isolated, version-checked executable and cleanup rules
as the [Ashen Vale corpus](../resolution/README.md). No library commands run. The
snapshot records input, pack and release hashes. Normal .NET tests use only the
committed files; no Python, legacy executable or Go toolchain is required.

Cases compare exact warning/error codes, messages, subjects, targets and ordering.
Successful and warning-only builds additionally compare all stats, point summaries,
set bonuses, formula bases/finals and ordered contribution provenance. Numeric
channels use the same `1e-12` absolute plus `1e-12` relative tolerance as the Ashen
Vale corpus; primary values, budgets, IDs and applicability remain exact. Invalid
builds return exit code 3 and no computed result from the reference CLI. Separate
Core tests pin retained effects for those builds, including fixed mod effects
surviving invalid configured rolls.

This corpus does not cover Prototype ranges/normalization, talent copying,
upgrades, active skills or the complete Division 2 character result.
