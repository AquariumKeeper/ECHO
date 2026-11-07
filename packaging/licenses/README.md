# Licenses and notices

Character Planner's own code, documentation and drawn symbols are under the MIT
license in `LICENSE.txt`. Everything else in this release keeps its own terms:

| Component | Used for | License | File |
| --- | --- | --- | --- |
| Original artwork (Shade-Tech contributors, 2026) | The application icon and the pack's eight slot images | MIT | `shadetech-artwork-MIT.txt` |
| Division 2 Community Data Hub game data | The bundled pack's compiled game data (adapted; see `ATTRIBUTION.md`) | CC BY 4.0 | `division2-data-CC-BY-4.0.txt` |
| .NET runtime 10.0.14 | The self-contained runtime | MIT, with third-party notices | `dotnet-runtime-MIT.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| Avalonia 12.1.5 (all its packages below) | The desktop user interface | MIT | `Avalonia-MIT.txt` |
| Inter typeface (bundled by Avalonia.Fonts.Inter) | The interface font | SIL Open Font License 1.1 | `Inter-OFL-1.1.txt` |
| ANGLE (Avalonia.Angle.Windows.Natives 2.1.27548.20260419) | Graphics on Windows | BSD-3-Clause | `ANGLE-LICENSE.txt` |
| SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3 | Drawing and text shaping | MIT, with third-party notices | `SkiaSharp-HarfBuzzSharp-MIT.txt`, `SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` |
| CommunityToolkit.Mvvm 8.4.2 | View models | MIT, with third-party notices | `CommunityToolkit.Mvvm-MIT.txt`, `CommunityToolkit.Mvvm-THIRD-PARTY-NOTICES.txt` |
| MicroCom.Runtime 0.11.6 | Avalonia's COM interop | MIT | `MicroCom-MIT.txt` |
| Tmds.DBus.Protocol 0.94.1 | Avalonia's Linux support (bundled, unused on Windows) | MIT | `Tmds.DBus-MIT.txt` |

The release ships no game artwork: its only images are that original artwork. Tom Clancy's The Division 2 is a trademark of
Ubisoft Entertainment; game content and names belong to their owners.

The texts come from each package's NuGet download or, where a package carries none,
its project's repository: Avalonia, MicroCom, Tmds.DBus and the Inter typeface.

## Covered packages

The packaging script requires this list to match, exactly, the packages and runtime
the published apps carry (their `.deps.json` files). A changed version or a new
package stops packaging until its license and notices here are reviewed.

- `Avalonia/12.1.5`
- `Avalonia.Angle.Windows.Natives/2.1.27548.20260419`
- `Avalonia.Desktop/12.1.5`
- `Avalonia.Fonts.Inter/12.1.5`
- `Avalonia.FreeDesktop/12.1.5`
- `Avalonia.FreeDesktop.AtSpi/12.1.5`
- `Avalonia.HarfBuzz/12.1.5`
- `Avalonia.Native/12.1.5`
- `Avalonia.Remote.Protocol/12.1.5`
- `Avalonia.Skia/12.1.5`
- `Avalonia.Themes.Fluent/12.1.5`
- `Avalonia.Win32/12.1.5`
- `Avalonia.X11/12.1.5`
- `CommunityToolkit.Mvvm/8.4.2`
- `HarfBuzzSharp/8.3.1.3`
- `HarfBuzzSharp.NativeAssets.Win32/8.3.1.3`
- `MicroCom.Runtime/0.11.6`
- `SkiaSharp/3.119.4`
- `SkiaSharp.NativeAssets.Win32/3.119.4`
- `Tmds.DBus.Protocol/0.94.1`
- `runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.14`
