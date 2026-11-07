# Skill mod input contracts

The synthetic pack and 20 captured cases isolate exact variant binding, supported
mod slots, per-slot choice compatibility, finite bounds and step validation,
shared-input accumulation, grade interaction and per-skill preview isolation.
Large finite choices exercise overflow and withholding of an entire input.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite skill-mod-resolution
```

The [basic corpus policy](../resolution/README.md) applies: isolated executable,
pack/build inputs, no library commands, complete responses and recorded hashes.
Normal tests consume committed files only. Successful cases compare all character
stats and previews, including local input maps; invalid cases compare exact
diagnostic fields and ordering because the CLI omits computed results on errors.

Focused Core tests additionally verify retained previews after invalid rolls,
non-finite inputs, floating-point step tolerances, blocked input overflow, local
input isolation and the distinction between a null and an empty legacy mod map.
Missing mods produce zero-valued declared inputs without configuration warnings.
