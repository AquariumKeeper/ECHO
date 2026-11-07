# Active skill selection and preview contracts

The synthetic pack and 20 complete captured cases isolate accepted skill choices,
authored slot order, family conflicts, missing/unknown/forbidden selections, grade
identity and rank bounds, conditional outputs, local preview inputs, explicit
numeric assumptions and non-finite formula/condition diagnostics.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite active-skill-resolution
```

The [basic corpus policy](../resolution/README.md) applies: isolated executable,
pack/build inputs, no library commands, complete responses and recorded hashes.
Normal tests consume committed files only. Successful cases compare all character
stats and breakdowns plus preview slot/skill identity, ordered output metadata,
formula/condition strings, input maps and values. Invalid cases compare exact
diagnostics because the reference CLI omits computed results on errors.

Focused Core tests additionally verify retained previews after errors, rejected
family grades, local input isolation, false-condition short-circuiting and clamped
level behavior. The suite does not cover skill mods, party effects or build outputs.
