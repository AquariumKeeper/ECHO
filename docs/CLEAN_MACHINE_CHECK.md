# Clean-machine check

The C12 release gate: the ZIP must work on a Windows x64 PC that has never had .NET, an
SDK or this project. Record the result with the release (the machine, the
Windows version, the ZIP's SHA-256, and each step's outcome).

## Before you start

- Use a Windows 10 or 11 x64 machine (or a fresh VM) without .NET installed.
  `dotnet --info` should fail or be missing.
- Copy the release ZIP (`character-planner-<version>-windows-x64.zip`; the
  `0.2.0-beta.1` tester build was `character-planner-windows-x64.zip`) and its
  `.sha256` file to it.
  In PowerShell, `Get-FileHash` on the ZIP must match the
  `.sha256` file.

## Steps

1. **Unzip** to any folder, for example `Downloads\CharacterPlanner`. If Windows
   blocks files from the internet, right-click the ZIP first, choose Properties, and
   tick Unblock. From `0.2.0-beta.2`, the folder holds exactly twelve entries: the two
   programs, seven documents, `release-manifest.json` and the `licenses` and `packs`
   folders. The beta.1 tester build has about 270 files in one folder.
2. **Desktop app:** run `CharacterPlanner.Desktop.exe`. Check that:
   - the window opens within a few seconds, with the orange card icon in the title bar
     and taskbar;
   - the Build tab shows slot tiles with grey slot images;
   - About shows the release's version (for example `0.2.0-beta.6`) and the bundled pack
     (`r100.candidate.1`);
   - equipping an item, then Save, then closing and reopening the app shows the saved
     build in Saved builds;
   - the Simulate tab runs a simulation and shows results.
3. **CLI:** in PowerShell, `cd` to the unzipped folder and run:
   - `.\CharacterPlanner.Cli.exe version`, which should print JSON with `"ok":true` and
     the release's version;
   - `.\CharacterPlanner.Cli.exe pack validate .\packs\division-2`, which should give
     `"ok":true` with no diagnostics;
   - `.\CharacterPlanner.Cli.exe library list`, which should list the build saved in
     step 2.
4. **Live session:** with the desktop app open, run
   `.\CharacterPlanner.Cli.exe session list`. It should list the window as available.
5. **SmartScreen:** note whether Windows warned about an unrecognised app. The build is
   unsigned, so a warning is expected; record it.
6. **User artwork:** copy a prepared artwork ZIP separately (for example the locally
   converted ShadeTech historical collection). Open **Artwork** and install it.
   Confirm that installation leaves the bundled images selected, previews and
   source notices are readable, and **Use this pack** changes the artwork without
   changing the build. Close/reopen the app to check the choice is restored, then
   choose **Use bundled and remove**. Repeat the dialog at 100%, 150% and 200%
   Windows display scale, including moving between differently scaled monitors
   when available. Use Tab/Shift+Tab, arrows, Space/Enter and Escape to check the
   picker, pack list, credit selector, actions and cancellation without a mouse.
   A file-picker interaction and a real monitor change still require this manual
   check even though automated native rendering and keyboard action tests pass.

## On the build machine

Run the library check with the last release's ZIP. It runs both releases on a temporary library:

```powershell
pwsh -File scripts/check-library-rollback.ps1 -OldZip <last-release.zip> -NewZip <this-release.zip>
```

## Where it writes

Builds and live-session files are saved under `%LOCALAPPDATA%\CharacterPlanner\alpha`.
Deleting that folder and the unzipped folder removes everything the app wrote.

If anything fails, keep the error text or a screenshot. The CLI's JSON output and
the window's error message are enough to diagnose most problems.
