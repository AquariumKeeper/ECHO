# Build output contracts

The synthetic pack and ten captured cases isolate build-level values evaluated
after skill previews. A slot's marked headline rate supplies `<slot>_dps`; its
successful shield outputs sum into `<slot>_shield`. Empty, rejected, hidden and
failed previews contribute zero for the absent channel. Unmarked rates remain
visible in previews but do not become headline DPS.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite build-output-resolution
```

The [basic corpus policy](../resolution/README.md) applies: isolated executable,
pack/build inputs, no library commands, complete responses and recorded hashes.
Normal tests consume committed files only. Successful cases compare ordered build
output IDs, names, descriptions, formats and raw values alongside all existing
stat/preview channels. Invalid cases compare exact diagnostics; focused Core tests
check retained output values, missing formulas and error precedence separately.

The same test runner also checks the original C0 resolve and resolve-context
fixtures for all six Division 2 builds without altering their bytes.
Those comparisons cover character stats, primary values, point summaries, ordered
breakdown provenance, skill previews, sets and build outputs. CLI catalog names,
assumption discovery and alternate weapon projections remain separate contracts.
The party-buff fixture is included now that party-effect selection is implemented.
