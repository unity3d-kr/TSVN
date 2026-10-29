# Building the extension

`TSVN.VS2022.csproj` is a classic (non-SDK) VSIX project. It needs Desktop MSBuild and
`Microsoft.VsSDK.targets` resolved through `$(VSToolsPath)`, so `dotnet build` cannot load it.
This folder contains an SDK-style harness that compiles the same sources, XAML and command
table with the .NET SDK CLI, plus a script that packages the result into an installable VSIX.

## Requirements

| Tool | Why |
|---|---|
| .NET SDK 6 or later | runs `dotnet build` |
| .NET Framework 4.8 targeting pack (or Windows SDK) | provides `resgen.exe` |
| Python 3 | `package-vsix.py` |

## Usage

```
build\build.cmd
```

This performs three steps:

1. compiles `TSVN.Shared\Options\OptionsDialog.resx` into `obj\OptionsDialog.resources`
2. builds `bin\Release\net48\TSVN.VS2022.dll`
3. packages `<repo>\TSVN.VS2022.vsix`

To package an already built assembly elsewhere:

```
python build\package-vsix.py --assembly <path to TSVN.VS2022.dll> [--deps <folder>] [--out <vsix>]
```

## Why the harness exists

| Problem | Cause | Handling here |
|---|---|---|
| `CS0246` for every VS type | `PackageReference` assemblies are never resolved without the VS SDK targets | SDK-style project with explicit `PackageReference` items |
| `MSB3822` / `MSB3823` | the .NET Framework `.resx` holds binary (WinForms) entries that Core MSBuild cannot serialize | pre-compiled with the Framework `resgen.exe`, then embedded under a fixed `LogicalName` |
| `VSCT1118` / `VSCT1103` | `VSGlobals.vsct` missing from the `.vsct` include path | `Community.VisualStudio.VSCT` package adds it |
| `CS0246` on `Command` | `Community.VisualStudio.Toolkit` only ships `net48` assets | `TargetFramework` is `net48`, not `net472` |

`net48` must stay: retargeting to `net472` leaves the toolkit package with no compile assets.

## VSIX packaging notes

`package-vsix.py` writes these entries in order:

```
extension.vsixmanifest      ->  build\extension.vsixmanifest (identity, targets [17.0,18.0))
TSVN.VS2022.dll             ->  the built assembly
TSVN.VS2022.pkgdef          ->  package registration (CodeBase / Menus / ToolWindows)
Caliburn.Micro.Core.dll     ->  runtime dependencies, .pdb copied when present
Community.VisualStudio.Toolkit.dll
Microsoft.ApplicationInsights.dll
Resources\...               ->  Package.ico, tortoisesvn.png, LICENSE
[Content_Types].xml
manifest.json
catalog.json
```

**`manifest.json` and `catalog.json` are mandatory.** The VS 2022 installer resolves the
package through the VS Installer pipeline; without those two files `VSIXInstaller.exe` fails
with `InvalidExtensionPackageException: The file is not a valid VSIX package`, which misleadingly
points at the manifest. They are generated from `extension.vsixmanifest`, so bump the version in
one place only.

Do not reuse the `extension.vsixmanifest` found inside an installed extension folder: the
installer rewrites it after installation, duplicating `InstallationTarget` entries, and
`VSIXInstaller` rejects that copy.

VS 2017 and later use `Microsoft.VisualStudio.Community` as the installation target for every
edition, so a single `InstallationTarget` covers Community, Professional and Enterprise.

## Installing

Double-click the `.vsix`, or:

```
"%ProgramFiles%\Microsoft Visual Studio\2022\<edition>\Common7\IDE\VSIXInstaller.exe" TSVN.VS2022.vsix
```

`/quiet` does not work from a non-elevated shell: the process exits with code 4 before it
writes a log. The GUI path is the reliable one.

If the menu does not refresh after installing, close Visual Studio and delete
`%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_*\ComponentModelCache`, then start it again.
