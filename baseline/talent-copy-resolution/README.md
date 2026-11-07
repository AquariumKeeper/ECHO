# Talent-copy contracts

The synthetic pack and 19 captured cases isolate Abridged-style copying: active
copier and target gates, shared weapon-type tags, excluded tags/talents, known and
permitted source selections, missing/inactive endpoints, multiple sources/copiers,
formula assumptions and single-pass copying of a copying talent.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite talent-copy-resolution
```

The [basic corpus policy](../resolution/README.md) applies: isolated executable,
pack and build inputs, no library commands, recorded hashes and complete responses.
`defaultContext` supplies common explicit assumptions before case overrides. Normal
.NET tests consume committed files only. Successful cases compare all numerical
channels and ordered provenance; invalid cases compare exact ordered diagnostics.

Copied ordinary effects use the original talent's expression paths and gates;
holstered effects are not copied. Provenance names the copied talent and copier.
Copying adds no talent selection or set-piece credit and does not recursively
expand copied rules. The cases include these distinctions; focused Core tests
also cover ordinal tag-prefix comparison and endpoint-only tag exclusions.
