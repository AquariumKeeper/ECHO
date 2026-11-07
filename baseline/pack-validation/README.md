# C2 pack validation contracts

`inputs.json` authors 692 pack inputs. `legacy-v1.json` records their raw input,
CLI exit code and complete JSON response from accepted release `0.1.0-beta.6`.
The release ZIP SHA-256 and source commit match `baseline/manifest.json`; the
snapshot also records the input file's SHA-256. The original C0 fixtures and
their index are unchanged.

The cases exercise structural failures, numeric bounds, formula references,
scopes/cycles, catalog references, Prototype/upgrades, skill previews, simulation
rules, artwork/layout/theme, duplicate field merging and replacement, nulls,
case-sensitive custom readers and multi-error diagnostic traversal order. Field
matrices cover missing/null/wrong-type values; boundary cases cover numeric limits,
JSON depth, malformed syntax and Unicode repair. Selected diagnostics also run
under German, French and Arabic cultures.
They are a representative regression corpus, not an exhaustive proof of every
malformed input or every expression's numerical accuracy.

Normal verification is `./scripts/verify.ps1`. `LegacyPackContractTests` reads
the committed data and compares acceptance, pack identity, slot catalog identity
and complete diagnostic lists, including their text and order. It also verifies
the snapshot provenance and that every authored case was captured exactly once.
No legacy checkout, executable, Go toolchain or Python is required for those tests.

To add reference cases, edit `inputs.json`, then explicitly capture them with the
accepted external release ZIP:

```powershell
python baseline/capture_pack_validation.py PATH/TO/character-planner-windows-amd64.zip
```

The script refuses a ZIP whose checksum differs from C0. It extracts only the
fixed CLI archive member into a temporary directory under ignored `artifacts/`,
checks its version, then supplies each pack as exact UTF-8 bytes on stdin to
`--json --pack - catalog list slots`. It does not open a desktop session or
touch a build library. Review input and snapshot changes together. Fix the C#
implementation when a captured result differs; do not alter expected results to
make the tests pass.

The CLI omits advisory pack warnings on successful catalog commands. Warning
semantics are tested directly in `ValidationTests.UnreachableSetBonusIsAdvisory`.
Keep warning-only pack cases out of this exact CLI-diagnostic comparison unless
a separate reference capture exposes the validator's warning list.
