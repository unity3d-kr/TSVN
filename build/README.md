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
| `VSSDK1013` naming a stale `net472\_EmptyResource.resources` | `Microsoft.VsSDK.targets` is imported from the project body, before the SDK computes `IntermediateOutputPath`, so its default state files resolve to `$(IntermediateOutputPath)\resources.json` with an empty prefix — that is `J:\resources.json`, shared by every build and never invalidated | `ResourceManifest`, `CtoFileManifest` and `CtoCacheFile` are pinned inside `obj\` (see the `VsSdkStateDirectory` property group) |

`net48` must stay: retargeting to `net472` leaves the toolkit package with no compile assets.

If a build still fails in `MergeCtoResource`, delete the drive-root leftovers
(`<drive>:\resources.json`, `ctoFiles.json`, `mergeCto.cache`) and `obj\Release\net48\TSVN.cto`;
they cache the previous CTO merge and will keep pointing at an intermediate folder that no
longer exists.

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

`extension.vsixmanifest` declares two assets:

```xml
<Asset Type="Microsoft.VisualStudio.VsPackage"    Path="TSVN.VS2022.pkgdef" />
<Asset Type="Microsoft.VisualStudio.MefComponent" Path="TSVN.VS2022.dll" />
```

The `MefComponent` asset is what makes Visual Studio compose `DiffMarginProvider` and
`DiffOverviewMarginProvider` out of this assembly. Drop it and the package still loads, the menu
still works, and only the editor margins silently never appear. `[ProvideBindingPath]` on
`TsvnPackage` is required alongside it.

## Installing

Double-click `reinstall.vbs` in this folder - it builds, packages and installs, and opens no
window - or from a terminal:

```
python reinstall.py                    # build, package, install
python install-vsix.py                 # install only
python install-vsix.py --uninstall     # remove it again
```

To remove the extension, double-click `uninstall.vbs`.

Do **not** install by double-clicking the `.vsix`, and do not use Install or Uninstall in Manage
Extensions. Both go through `VSIXInstaller.exe`, and the copy inside a Visual Studio instance
starts a second copy of itself from
`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\resources\app\ServiceHub\Services\Microsoft.VisualStudio.Setup.Service\`
- that one is a console program, so a black window opens whatever switches are passed. It is a
Microsoft defect with a report from May 2026 still open, and nothing here can suppress a window
another program creates. `install-vsix.py` instead unpacks the archive into the folder Visual
Studio already loads from, so the installer is never started.

The first install on a machine with no registration entry yet still needs `VSIXInstaller` once,
because writing that entry in `privateregistry.bin` is what it exists for; every install after it
can come from `install-vsix.py`.

If the menu does not refresh, the script already deletes
`%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_*\ComponentModelCache`; starting Visual Studio then
rebuilds the composition.
