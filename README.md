# TSVN
Control TortoiseSVN from within Visual Studio

This fork replaces the built-in Octicons with **TortoiseSVN's own XPStyle icon set**, so the extension looks like TortoiseSVN itself. The menu is named `TortoiseSVN` instead of `Tsvn`. See [Icon set](#icon-set).

# Getting started
1. Install [TortoiseSVN](http://www.tortoisesvn.net) with "command line client tools"
2. Install the [TSVN](https://marketplace.visualstudio.com/items?itemName=SamirBoulema.TSVN) extension
3. Enjoy! 

# Features
All the SVN functions quickly accessible from the `TortoiseSVN` menu. It is available in the main menu bar and in the item context menu, and every command uses the same icon as its TortoiseSVN counterpart.

A customizable toolbar with all your favorite commands.

A pending changes window with the most important commands.

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

To change or add one: replace or append a 16x16 cell in the strip, declare its position with a new `<IDSymbol>` (next free value), extend `usedList`, then point the button's `<Icon id="..."/>` at that symbol. `TSVN.VS2019/TSVN.vsct` carries the same layout.

The `TortoiseSVN` submenu itself has no icon. Visual Studio cannot render one: `VSCT.exe` silently discards `<Icon>` nested inside a `<Menu>` element, so this is a platform limitation rather than a missing feature. `Resources/TSVN.vsct` is an obsolete copy kept for history and is not compiled.

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
