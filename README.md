# TSVN

TortoiseSVN inside Visual Studio 2022: a menu, a toolbar, a pending changes window, and change
markers in the editor. A fork of [sboulema/TSVN](https://github.com/sboulema/TSVN) - the commands,
the menu layout and the keyboard shortcuts are the original's, so only the changes are below.

* **TortoiseSVN's own icons** - the XPStyle set instead of Octicons, menu renamed `TortoiseSVN`
* **Diff markers** - lines that differ from `BASE` marked in the gutter and beside the scroll bar
* **File buttons on the toolbar** - one per file command, off by default and switched on in Options
* **Cancellable scan** - the pending changes `svn status` scan reports progress and can be stopped
* **Silent build** - `build\reinstall.vbs` and `build\uninstall.vbs` install and remove without a console

## Diff markers

The lines that differ from `BASE` are marked between the line numbers and the text, with a map of the
whole file next to the scroll bar. Off by default, switched on per file from the editor context menu,
and session scoped, so opening a large solution never reads `BASE` for files nobody asked about.

The palette is WinMerge's - amber for added lines, washed out amber for edited, grey for deleted.
Recomputation runs 500 ms after the last keystroke against a cached `svn cat -r BASE`, in memory, so
typing never touches the disk. A change inside a fold is drawn on the fold's header, and the map asks
`IVerticalScrollBar` where the thumb would put each line rather than modelling folds itself.

## Toolbar

A second group on the `TortoiseSVN` toolbar adds one button per file command - Show Log, Blame,
Differences, Commit, Revert, Update, Add, Delete, Rename, Lock, Properties, diff markers. Off by
default: `Options` → **Toolbar** → *Show buttons for the current file on the TortoiseSVN toolbar*.
A main window toolbar cannot hide a button already on it, so they are gated by a visibility
constraint on a UI context of the extension's own.

## Building and installing

Close Visual Studio, then double-click `build\reinstall.vbs`: it builds, packages and installs with no
window at any point, logging to `build\install.log`. `build\reinstall.cmd` shows the output instead.

```
build\reinstall.vbs   build and install, silently
build\uninstall.vbs   remove, silently
```

The console that appears when installing from Visual Studio is Microsoft's - their installer starts a
second, console copy of itself. These scripts write into the folder named in `privateregistry.bin`,
keeping the previous one in `%LOCALAPPDATA%\TSVN\backups`.

Needs [TortoiseSVN](https://tortoisesvn.net) with the *command line client tools* option, and Visual
Studio 2022. The menu icons are TortoiseSVN's, which is GPLv2; this repository is MIT.

## Links

[Releases](https://github.com/unity3d-kr/TSVN/releases) · [Original](https://github.com/sboulema/TSVN) · [Marketplace](https://marketplace.visualstudio.com/items?itemName=SamirBoulema.TSVN)
