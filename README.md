# TSVN
Control TortoiseSVN from within Visual Studio

This fork replaces the built-in Octicons with **TortoiseSVN's own XPStyle icon set**, so the extension looks like TortoiseSVN itself. The menu is named `TortoiseSVN` instead of `Tsvn`. See [Icon set](#icon-set).

It also adds **diff markers**: the lines that differ from the `BASE` revision are marked in the editor gutter, VisualSVN style. See [Diff markers](#diff-markers).

# Getting started
1. Install [TortoiseSVN](http://www.tortoisesvn.net) with "command line client tools"
2. Install the [TSVN](https://marketplace.visualstudio.com/items?itemName=SamirBoulema.TSVN) extension
3. Enjoy! 

# Features
All the SVN functions quickly accessible from the `TortoiseSVN` menu. It is available in the main menu bar and in the item context menu, and every command uses the same icon as its TortoiseSVN counterpart.

A customizable toolbar with all your favorite commands.

A pending changes window with the most important commands. On a large working copy the
`svn status` scan runs with a progress bar and a **Cancel** button, so a slow repository never
freezes the window.

Change indicators in the editor, see [Diff markers](#diff-markers).

## Diff markers

The lines that differ from the `BASE` revision are marked with vertical colour blocks between
the line numbers and the text, and a map of the whole file is drawn next to the vertical scroll
bar.

The markers are **off by default and switched on per file**. Reading the `BASE` revision out of
the working copy is cheap but not free, so a file only pays for it once it is switched on:

> Right-click in the editor → `TortoiseSVN` → `Show Diff Markers`

The command is a checkbox. The choice lasts for the current Visual Studio session only: every
file starts hidden when Visual Studio launches, so opening a large solution never reads `BASE`
for files that were switched on in an earlier session.

### How it works

1. On first activation, and again at most once a minute, `svn cat -r BASE <file>` reads the
   pristine revision out of the working copy.
2. That text and the editor buffer are compared in memory. Nothing is saved first, so the
   markers follow what you type, unsaved changes included.
3. Recomputation runs 500 ms after the last keystroke, off the UI thread. Typing never touches
   the disk and never starts a process.
4. A file whose markers were never switched on starts no subprocess at all.

### Colours

The palette comes from WinMerge's `Options > Colors` dialog. WinMerge paints a changed line with
the `Difference` background and a line that exists on only one side with that row's `Deleted`
swatch, so two base colours are enough:

| WinMerge row | Background | Deleted |
|---|---|---|
| Difference | `#EFCB05` | `#C0C0C0` |
| Selected Difference | `#EF7774` | `#F0C0C0` |
| Ignored Difference | `#FBF2BF` | `#E9E9E9` |
| Moved | `#E49B52` | `#C0C0C0` |
| Selected Moved | `#F8704E` | `#FCB5A3` |
| Same As Next (3 panes) | `#FBFADF` | `#E9E9E9` |
| Same As Next (Selected) | `#EFB7B4` | `#F0E0E0` |
| Word Difference | `#F1E2AD` | `#FFAA82` |
| Selected Word Diff | `#FFA0A0` | `#C8816C` |

Which becomes:

| State | Marker colour | Opacity |
|---|---|---|
| Added | `#EFCB05` | 100 % |
| Modified | `#EFCB05` | 45 % |
| Deleted | `#C0C0C0` light theme, `#8A8A8A` dark theme | 90 % |

Added and modified lines share the amber and are told apart by weight: a solid bar is a line
that does not exist in `BASE`, a washed out bar is an edited line. There is deliberately no
green, matching WinMerge.

### Folded code

Outlining removes the lines it hides from the editor's layout, so a changed run inside a
collapsed region has no line of its own to sit on. Such a run is drawn on the region's **header**
line instead - the line the editor keeps on screen in place of the folded text - and merged with
any other hidden run on the same header. A run that is merely scrolled out of view produces
nothing, so there is never a mark floating in the empty space below the end of the file.

The overview bar measures the document in **displayed** lines for the same reason: a fold costs
the scroll bar nothing but its header, which is why the thumb grows while code is collapsed.
Both bars are repainted by moving the rectangles that are already on screen rather than rebuilding
them, because an empty strip inside a single frame is what reads as a flicker while scrolling.

### Options

`TortoiseSVN` → `Windows` → `Options` in the main menu bar. The dialog ends with a **Diff
markers** group:

* *Ignore changes that only touch whitespace*
* *Also mark changed regions next to the scroll bar*

### Source layout

| File | Role |
|---|---|
| `TSVN.Shared/Diff/DiffMargin.cs` | The left gutter: `IWpfTextViewMargin` that paints the blocks |
| `TSVN.Shared/Diff/DiffMarginProvider.cs` | MEF provider, places the gutter after the line numbers |
| `TSVN.Shared/Diff/DiffOverviewMargin.cs` | The right hand overview bar and its provider |
| `TSVN.Shared/Diff/DiffFolds.cs` | Collapsed outlining regions, merged and queried by line |
| `TSVN.Shared/Diff/MarkerStrip.cs` | The rectangle pool both margins paint through |
| `TSVN.Shared/Diff/DiffMarkersController.cs` | Per view: debounce, refresh, publish |
| `TSVN.Shared/Diff/SvnBaseProvider.cs` | `svn cat -r BASE` with a one minute cache |
| `TSVN.Shared/Diff/LineDiff.cs` | Trim prefix and suffix, then LCS over what is left |
| `TSVN.Shared/Diff/DiffColorProvider.cs` | WinMerge palette, light and dark variants |
| `TSVN.Shared/Diff/DiffMarkersState.cs` | Per file opt-in and the two global switches |
| `TSVN.Shared/Commands/ToggleDiffMarkersCommand.cs` | The menu item |

Because these are the only MEF exports in the assembly, two pieces of registration are required
and easy to lose: `[ProvideBindingPath]` on `TsvnPackage`, and the
`Microsoft.VisualStudio.MefComponent` asset in `build/extension.vsixmanifest`. Without them the
margins are never composed and the command appears to do nothing.

## Icon set

Menu icons are taken from TortoiseSVN's XPStyle resources (`src/Resources/menu*.ico`) and packed into a single 16x16 bitmap strip, [`Resources/tsvn-menu-icons.png`](Resources/tsvn-menu-icons.png). The order of that strip is declared by the `usedList` attribute of the `<Bitmap>` element in `TSVN.VS2022/TSVN.vsct`:

| # | Command | TortoiseSVN icon | # | Command | TortoiseSVN icon |
|---:|---|---|---:|---|---|
| 1 | `showChanges` | `menushowchanged` | 14 | `disk` | `explorer` |
| 2 | `pull` | `menuupdate` | 15 | `switchImage` | `menuswitch` |
| 3 | `push` | `menucommit` | 16 | `cleanup` | `menucleanup` |
| 4 | `showLog` | `menulog` | 17 | `stash` | `menupatch` |
| 5 | `revert` | `menurevert` | 18 | `fetch` | `menuupdate` |
| 6 | `branch` | `menucopy` | 19 | `add` | `menuadd` |
| 7 | `merge` | `menumerge` | 20 | `properties` | `menuproperties` |
| 8 | `differences` | `menudiff` | 21 | `delete` | `menudelete` |
| 9 | `blame` | `menublame` | 22 | `shelve` | `menushelve` |
| 10 | `repo` | `menurepobrowse` | 23 | `lockImage` | `menulock` |
| 11 | `resolve` | `menuresolve` | 24 | `unlock` | `menuunlock` |
| 12 | `sync` | `sync` | 25 | `rename` | `menurename` |
| 13 | `commit` | `menucommit` | 26 | `unshelve` | `menuunshelve` |
| | | | 27 | `diffMarkers` | drawn for this repo, see below |

To change or add one: replace or append a 16x16 cell in the strip, declare its position with a new `<IDSymbol>` (next free value), extend `usedList`, then point the button's `<Icon id="..."/>` at that symbol. `TSVN.VS2019/TSVN.vsct` carries the same layout.

Slot 27 is the one exception to "taken from TortoiseSVN": none of its menu icons reads as a diff gutter, so the cell is drawn in the same flat XP style — a framed strip of three blocks (solid amber, washed out amber, grey) next to five text lines, mirroring the marker colours below.

The `TortoiseSVN` submenu itself has no icon. Visual Studio cannot render one: `VSCT.exe` silently discards `<Icon>` nested inside a `<Menu>` element, so this is a platform limitation rather than a missing feature.

TortoiseSVN is licensed under the GPLv2, while this repository is MIT. Keep that in mind when redistributing the icon assets.

## Keyboard shortcuts

* `(S)VN (C)ommit` - Ctrl+Shift+Alt+S, C
* `(S)VN Sho(w) Changes` - Ctrl+Shift+Alt+S, W
* `(S)VN (U)pdate` - Ctrl+Shift+Alt+S, U
* `(S)VN (L)og` - Ctrl+Shift+Alt+S, L
* `(S)VN Create P(a)tch` - Ctrl+Shift+Alt+S, A
* `(S)VN Apply Patc(h)` - Ctrl+Shift+Alt+S, H
* `(S)VN (S)witch` - Ctrl+Shift+Alt+S, S
* `(S)VN (M)erge` - Ctrl+Shift+Alt+S, M
* `(S)VN (R)evert` - Ctrl+Shift+Alt+S, R
* `(S)VN Clea(n)up` - Ctrl+Shift+Alt+S, N
* `(S)VN File (B)lame` - Ctrl+Shift+Alt+S, B
* `(S)VN File (D)iff` - Ctrl+Shift+Alt+S, D
* `(S)VN Sh(e)lve` - Ctrl+Shift+Alt+S, E
* `(S)VN Unshelve` - Ctrl+Shift+Alt+S, P
* `(S)VN L(o)ck` - Ctrl+Shift+Alt+S, O
* `(S)VN Unloc(k)` - Ctrl+Shift+Alt+S, K

# Links
[Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=SamirBoulema.TSVN)

[Github Releases](https://github.com/unity3d-kr/TSVN/releases)

[Open VSIX Gallery](http://vsixgallery.com/extension/ddccf913-105f-4243-80c6-16e9cc5bade5/)

[TortoiseSVN source](https://svn.code.sf.net/p/tortoisesvn/code/trunk)
