# Prototype contracts

This synthetic pack and its 23 complete captured cases isolate Prototype slot/tag
eligibility, exact item binding, augment identity/levels, effect activity, count
stats, ordinary/Prototype roll bounds, fallback bounds and normalized activities.
The pack includes a fixed Skill Tier attribute, account and equipment upgrades,
and two active Prototype slots to check contribution order and source provenance.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite prototype-resolution
```

The [basic corpus capture and comparison policy](../resolution/README.md) applies:
isolated executable and pack, no library commands, UTF-8 input, recorded release,
pack and input hashes. Normal .NET tests read committed files only. Successful
cases compare raw numerical channels, ordered contributions and source metadata;
invalid cases compare exact ordered diagnostic fields because the reference CLI
withholds computed results. Existing C0 fixtures remain unchanged.

Synthetic Core tests separately check retained calculations with invalid augments,
raw normalization strings, decimal/hexadecimal rounding boundaries, unsupplied
numeric defaults and diagnostic precedence between stages. Those cases derive
from the inspected frozen source contracts, not extra captured CLI result values.
In particular, normalization reads the explicit raw string using ParseFloat rules;
numeric assumption validation can reject a string that still enables normalization.
