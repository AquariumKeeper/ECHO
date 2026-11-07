# C3 basic build resolution contracts

`inputs.json` describes 14 Ashen Vale builds. `legacy-v1.json` contains their
complete input documents, explicit contexts, exit codes and responses from the
accepted `0.1.0-beta.6` release pinned in `../manifest.json`.

The optional `../capture_resolution.py` verifies the release ZIP SHA-256, extracts
only its fixed CLI member into an isolated scratch directory, checks its version,
and supplies the committed pack bytes. Each input starts with the reference's new
build document, then replaces the specified build fields. No library command runs.
Inputs are UTF-8 bytes. The snapshot records input and pack hashes, command shape,
release and commit. To regenerate deliberately:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip
```

Ordinary .NET tests read these committed files. They require no legacy executable,
checkout, Go toolchain or Python. This corpus supplements the original C0 fixtures;
it does not modify them. It covers the supported basic resolver, not Division 2's
configured equipment and previews.

Successful cases compare all primary and derived stats, point budgets, breakdown
bases/finals, clamping, contribution order and provenance, and active set bonuses.
Primary values and point summaries are exact; calculated stats, formula bases and
contribution values use an absolute tolerance of `1e-12` plus relative `1e-12`.
There is no global rounding. The separate cross-language golden preserves its
reference four-decimal comparison. IDs, ordering and diagnostic fields are exact.

The reference CLI returns `invalid-build` and exit code 3 without computed results
when resolution reports errors. Those cases compare the full ordered diagnostic
list; synthetic tests separately exercise the Core results retained for invalid
builds. Unknown context-key validation belongs to the command layer and is not
claimed by the basic resolver's numeric assumption normalization.
