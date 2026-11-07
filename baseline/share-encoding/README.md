# Historical share encoding evidence

These captures record the retired compatibility-first C3 encoder. ADR 0002
(`docs/adr/0002-retire-go-compatibility.md`) retires Go-format encoding compatibility.
The encoder and its optional C# probe have been removed; these files remain
unchanged as historical evidence, outside the current test contract.

`pack.json` and 21 captured cases cover layouts v1 through v12. `legacy-v1.json`
records reference responses and inflated tuples. `csharp-import-v1.json` records
27 C# codes imported by the frozen reader, with assembly and input hashes.
The original capture scripts remain for provenance; they are not normal
verification commands. The future app-owned share codec and legacy decoder are
still pending. Never regenerate these files as part of routine verification.
