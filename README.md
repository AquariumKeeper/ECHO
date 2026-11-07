# Character Planner

A character planner with combat simulation for Tom Clancy's The Division 2: a Windows
desktop app and a command-line tool, written in C# and Avalonia.

> Unofficial fan tool. Not affiliated with, endorsed by or sponsored by Ubisoft or
> Massive Entertainment. Tom Clancy's The Division 2 is a trademark of Ubisoft
> Entertainment; game content and artwork belong to their owners.

## Status

Beta. The desktop app edits, compares, saves and shares builds, and simulates fights:
weapons, talents, skills, incoming fire, shields, armor kits, cover and several enemies.
The CLI does the same in JSON, and can drive a running desktop window. Results are
estimates from community data; [Attribution](docs/ATTRIBUTION.md) lists the sources and
what the planner doesn't model.

## Building

Windows x64 with the .NET SDK pinned in [global.json](global.json). Package versions are
pinned in `Directory.Packages.props` and restored from the committed lock files.

```powershell
./scripts/verify.ps1      # locked restore, formatting, Release build with warnings as errors, all tests
./packaging/package.ps1   # self-contained single-file ZIP in artifacts/, with a native window smoke test
```

Both scripts take `-Dotnet <path>` to use a specific SDK executable.

## Layout

| Folder | Contents |
| --- | --- |
| `src/CharacterPlanner.Core` | Packs, build documents and the stat resolver. No UI, filesystem or transport. |
| `src/CharacterPlanner.Simulation` | Combat simulation on Core's resolver. |
| `src/CharacterPlanner.Application` | Commands and sessions. |
| `src/CharacterPlanner.Infrastructure` | Storage and transport. |
| `src/CharacterPlanner.Desktop`, `src/CharacterPlanner.Cli` | The two programs. |
| `src/CharacterPlanner.Legacy` | One-way import and conversion of the older app's formats. |
| `data/packs/division-2` | The game data pack (schema v2) the programs load. |
| `baseline/` | Frozen results of the older app, used as regression references. |

## Documentation

- [CLI](docs/CLI.md): commands and JSON output.
- [Pack schema v2](docs/PACK_SCHEMA_V2.md) and the [decision records](docs/adr/).
- [User artwork](docs/USER_ARTWORK.md) and the [artwork pack format](docs/ARTWORK_PACK_FORMAT.md).
- [In-game checklist](docs/IN_GAME_CHECKLIST.md): readings that would settle open values.
- [Clean machine check](docs/CLEAN_MACHINE_CHECK.md): release acceptance on a fresh PC.

## Licenses

The code is under the [MIT License](LICENSE). The game data is adapted from the Division 2
Community Data Hub under [CC BY 4.0](licenses/division2-data-CC-BY-4.0.txt); see
[Attribution](docs/ATTRIBUTION.md). The slot artwork and application icon are original
work under MIT. Bundled libraries and fonts keep their own licenses, listed in
[packaging/licenses](packaging/licenses/).
