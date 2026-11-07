# Portable build document contracts

`inputs.json` contains 135 raw UTF-8 JSON inputs. `legacy-v1.json` records complete
responses from the checksum-pinned C0 release for strict portable schema v1,
duplicate keys (including escaped aliases), exact casing, unknown fields, required
nested records, omitted/null values, integer boundaries, Unicode and party rules.

Capture deliberately:

```powershell
python baseline/capture_documents.py PATH/TO/character-planner-windows-amd64.zip
```

The tool verifies the release ZIP and executable version, uses an isolated Ashen
Vale pack and scratch directory, and runs only `build export` with stdin. It never
uses library commands. Snapshot provenance pins input, pack and release hashes.
Normal .NET verification consumes committed files without Python or the reference.

The frozen CLI resolves builds after decoding. `decodeAccepted` distinguishes a
document rejection from a successfully decoded but invalid build reported later
by the engine. Such invalid-but-retained ranks and levels are not codec failures.
Tests compare exact rejection messages; successful export documents are compared
semantically with C# output. Encoding is deterministic within C#, but JSON object
order, string escapes and number spelling are not required to be byte-identical
between runtimes. Every accepted case also passes deterministic decode/encode
round trips. All 192 resolver fixtures now pass through the production decoder;
successful normalized documents compare every field to the frozen response.

The document policy is distinct from pack decoding. Missing/null collections
normalize to empty collections, while required nested records reject null or
missing fields. Party members/effects have additional validation. Skill-mod
identities use the frozen custom-record rules. All rank/level fields retain
64-bit integer values for later build diagnostics. Non-finite values fail encoding
instead of being dropped. View assumptions are never part of the document.
