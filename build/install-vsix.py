"""Install or replace the TSVN extension without the VSIX installer.

VSIXInstaller.exe is a GUI program: it shows a progress window, then a result
dialog, and it hands the real work to VSIXInstaller.SessionHost.exe. /quiet only
suppresses the interaction, not those windows.

None of it is needed. A VSIX is a zip archive and an installed extension is just
a folder under

    %LOCALAPPDATA%\\Microsoft\\VisualStudio\\17.0_<instance>\\Extensions\\<folder>

holding extension.vsixmanifest, the assembly, its pkgdef and its dependencies.
Visual Studio builds its component cache from every folder it finds there, which
is all the "installation" a user extension needs.

Doing that in process is what makes this script windowless: it starts no child
process and opens no window, so it can run from a shell, a build step, or an
agent session without a command prompt appearing.

    python build/install-vsix.py                 # replace the installed extension
    python build/install-vsix.py --dry-run       # report the plan, change nothing
    python build/install-vsix.py --uninstall     # take it out again, the same way

The previous folder is moved to %LOCALAPPDATA%\\TSVN\\backups before the swap, and
is restored automatically if anything fails half way through. Backups are kept
out of Extensions on purpose: a second copy of extension.vsixmanifest there would
be discovered as a second installation of the same package.
"""

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# Identity of the extension, as declared in build/extension.vsixmanifest.
EXTENSION_ID = "ddccf913-105f-4243-80c6-16e9cc5bade5"

# Without both of these Visual Studio never composes the extension.
REQUIRED_FILES = ("extension.vsixmanifest", "TSVN.VS2022.dll")

# Names this script creates next to an extension folder. Anything matching one of
# them is a leftover, not an installation.
TEMP_MARKERS = (".bak-", ".new-")

# CREATE_NO_WINDOW: tasklist is a console program, and without this flag Python
# hands it a console of its own.
CREATE_NO_WINDOW = 0x08000000


def sha256(path):
    digest = hashlib.sha256()

    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)

    return digest.hexdigest().upper()


def is_temp(name):
    return any(marker in name for marker in TEMP_MARKERS)


def instance_roots():
    """Visual Studio 2022 instance folders, i.e. the ones that hold Extensions/."""
    local = os.environ.get("LOCALAPPDATA")

    if not local:
        return []

    parent = os.path.join(local, "Microsoft", "VisualStudio")

    if not os.path.isdir(parent):
        return []

    roots = []

    for name in sorted(os.listdir(parent)):
        path = os.path.join(parent, name)

        if not name.startswith("17.0_") or not os.path.isdir(path):
            continue

        if os.path.isdir(os.path.join(path, "Extensions")):
            roots.append(path)

    return roots


def find_installed(roots):
    """(instance root, extension folder) of an installed copy, or (None, None)."""
    for root in roots:
        extensions = os.path.join(root, "Extensions")

        for name in sorted(os.listdir(extensions)):
            if is_temp(name):
                continue

            folder = os.path.join(extensions, name)
            manifest = os.path.join(folder, "extension.vsixmanifest")

            if not os.path.isfile(manifest):
                continue

            try:
                with open(manifest, "r", encoding="utf-8", errors="replace") as handle:
                    text = handle.read()
            except OSError:
                continue

            if EXTENSION_ID.lower() in text.lower():
                return root, folder

    return None, None


def wide_strings(data, start, end):
    """(offset, text) of every UTF-16LE printable run inside a byte range."""
    index = max(0, start)
    end = min(len(data), end)

    while index < end - 1:
        if data[index + 1] != 0 or not 0x20 <= data[index] <= 0x7E:
            index += 1
            continue

        begin = index
        text = bytearray()

        while index < len(data) - 1 and data[index + 1] == 0 and 0x20 <= data[index] <= 0x7E:
            text.append(data[index])
            index += 2

        if len(text) >= 8:
            yield begin, text.decode("ascii")


def registered_install_path(root):
    """
    The install path Visual Studio recorded for this extension, or None.

    Visual Studio does not scan Extensions/ for manifests. It loads the extensions
    named in its private registry and composes their MEF parts from the folder that
    entry points at, so a package that is not on that list is never loaded however
    well formed its folder is. Deleting the folder by hand leaves the entry behind,
    pointing at nothing, and the extension silently disappears.

    The registry is a hive file, privateregistry.bin, which only an administrator
    can open, so the path is read straight out of it: the hive stores the install
    path as a wide string next to the extension's identity, and nothing else in it
    holds a path of that shape under this instance.
    """
    hive = os.path.join(root, "privateregistry.bin")

    if not os.path.isfile(hive):
        return None

    try:
        with open(hive, "rb") as handle:
            data = handle.read()
    except OSError:
        return None

    extensions = os.path.join(root, "Extensions")
    pattern = re.compile(
        r"^[A-Za-z]:\\.*\\Extensions\\([A-Za-z0-9]{8}\.[A-Za-z0-9]{3})\\?$",
        re.IGNORECASE,
    )
    marker = EXTENSION_ID.encode("ascii")

    best = None
    best_distance = None
    start = 0

    while True:
        anchor = data.find(marker, start)

        if anchor < 0:
            break

        start = anchor + 1

        for offset, text in wide_strings(data, anchor - 2048, anchor + 2048):
            match = pattern.match(text)

            if not match:
                continue

            candidate = os.path.join(extensions, match.group(1))

            if os.path.dirname(candidate).lower() != extensions.lower():
                continue

            distance = abs(offset - anchor)

            if best_distance is None or distance < best_distance:
                best, best_distance = candidate, distance

    return best


def hive_readable(root):
    """
    False while Visual Studio holds its private registry open.

    The hive is written when Visual Studio exits, so while it runs the file on disk
    is both locked and out of date: a copy installed since it started is not in it
    yet. "No registration found" then means "cannot look", not "not installed".
    """
    try:
        with open(os.path.join(root, "privateregistry.bin"), "rb"):
            return True
    except OSError:
        return False


def stray_copies(root, keep):
    """Folders holding this extension that Visual Studio will not load, because they are not the registered one."""
    found = []
    extensions = os.path.join(root, "Extensions")

    if not os.path.isdir(extensions):
        return found

    for name in sorted(os.listdir(extensions)):
        folder = os.path.join(extensions, name)

        if is_temp(name) or not os.path.isdir(folder):
            continue

        if keep and os.path.normcase(folder) == os.path.normcase(keep):
            continue

        manifest = os.path.join(folder, "extension.vsixmanifest")

        if not os.path.isfile(manifest):
            continue

        try:
            with open(manifest, "r", encoding="utf-8", errors="replace") as handle:
                text = handle.read()
        except OSError:
            continue

        if EXTENSION_ID.lower() in text.lower():
            found.append(folder)

    return found


def leftovers(roots):
    """Backups an older version of this script left inside Extensions/."""
    found = []

    for root in roots:
        extensions = os.path.join(root, "Extensions")

        for name in sorted(os.listdir(extensions)):
            if ".bak-" in name and os.path.isdir(os.path.join(extensions, name)):
                found.append(os.path.join(extensions, name))

    return found


def newest_root(roots):
    """A first install has no folder to go on, so use the instance that ran last."""
    best = None
    best_time = -1.0

    for root in roots:
        try:
            stamp = os.path.getmtime(os.path.join(root, "privateregistry.bin"))
        except OSError:
            continue

        if stamp > best_time:
            best, best_time = root, stamp

    return best


def new_folder(root):
    """A folder name in the shape Visual Studio uses for user extensions."""
    extensions = os.path.join(root, "Extensions")

    for attempt in range(1, 100):
        folder = os.path.join(extensions, "tsvninst.%03d" % attempt)

        if not os.path.exists(folder):
            return folder

    raise RuntimeError("no free extension folder name under " + extensions)


def visual_studio_running():
    try:
        result = subprocess.run(
            ["tasklist", "/fi", "imagename eq devenv.exe", "/nh"],
            capture_output=True,
            text=True,
            creationflags=CREATE_NO_WINDOW,
        )
    except OSError:
        return False

    return "devenv.exe" in (result.stdout or "").lower()


def backup_root():
    local = os.environ.get("LOCALAPPDATA") or HERE
    return os.path.join(local, "TSVN", "backups")


def check_stale(vsix, assembly):
    """True when the archive holds a different assembly than the one just built."""
    if not assembly or not os.path.isfile(assembly):
        return False

    with zipfile.ZipFile(vsix) as archive:
        for name in archive.namelist():
            if name.replace("\\", "/").endswith("TSVN.VS2022.dll"):
                inside = hashlib.sha256(archive.read(name)).hexdigest().upper()
                return inside != sha256(assembly)

    return False


def install(vsix, target, dry_run):
    """Unpacks the archive into target, keeping the previous folder as a backup."""
    stamp = time.strftime("%Y%m%d-%H%M%S")
    staging = target + ".new-" + stamp
    backup_dir = backup_root()
    backup = os.path.join(backup_dir, os.path.basename(target) + ".bak-" + stamp)

    if dry_run:
        return backup, None

    os.makedirs(backup_dir, exist_ok=True)

    if os.path.isdir(staging):
        shutil.rmtree(staging)

    os.makedirs(staging)

    try:
        with zipfile.ZipFile(vsix) as archive:
            archive.extractall(staging)

        missing = [name for name in REQUIRED_FILES if not os.path.isfile(os.path.join(staging, name))]

        if missing:
            raise RuntimeError("archive is incomplete, missing: " + ", ".join(missing))

        if os.path.isdir(target):
            shutil.move(target, backup)

        os.rename(staging, target)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)

        # Put the previous version back if the swap got as far as moving it away.
        if os.path.isdir(backup) and not os.path.isdir(target):
            shutil.move(backup, target)

        raise

    return backup, os.path.join(target, "TSVN.VS2022.dll")


def clear_caches(root, dry_run):
    """Forces Visual Studio to rebuild the MEF composition on its next start."""
    cleared = []

    for relative in ("ComponentModelCache", os.path.join("Extensions", "ExtensionMetadataCache.sqlite")):
        path = os.path.join(root, relative)

        if not os.path.exists(path):
            continue

        if not dry_run:
            if os.path.isdir(path):
                shutil.rmtree(path, ignore_errors=True)
            else:
                os.remove(path)

        cleared.append(path)

    return cleared


def resolve_target(roots):
    """
    (root, folder, message, first_time) for the copy Visual Studio actually loads.

    The registration decides this: a well formed folder anywhere else is never
    composed, so replacing - or removing - the wrong one changes nothing on screen.
    """
    for candidate in roots:
        registered = registered_install_path(candidate)

        if not registered:
            continue

        message = (
            "Replacing installed extension"
            if os.path.isdir(registered)
            else "Repairing the registration: the registered folder was missing"
        )
        return candidate, registered, message, False

    # Nothing registered. Before falling back to a new folder, check whether the
    # reason is that the registry cannot be read: an extension installed from
    # inside Visual Studio after it started is registered in memory only, and
    # writing a second copy somewhere else would be one more that is ignored.
    locked = [candidate for candidate in roots if not hive_readable(candidate)]
    instance, existing = find_installed(roots)

    if locked and existing is not None:
        message = "Replacing installed extension (registry locked, found by manifest)"
        return instance, existing, message, False

    root = newest_root(roots) or roots[0]

    return root, new_folder(root), "Installing first time", True


def remove(folder, dry_run):
    """Moves the installed folder out of Extensions, so it can be put back."""
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup_dir = backup_root()
    backup = os.path.join(backup_dir, "removed-" + os.path.basename(folder) + "-" + stamp)

    if dry_run:
        return backup

    os.makedirs(backup_dir, exist_ok=True)
    shutil.move(folder, backup)

    return backup


def main():
    parser = argparse.ArgumentParser(description="Install TSVN into Visual Studio 2022.")

    parser.add_argument(
        "--vsix",
        default=os.path.join(HERE, "bin", "TSVN.VS2022.vsix"),
        help="archive to install (default: build/bin/TSVN.VS2022.vsix)",
    )
    parser.add_argument(
        "--assembly",
        default=os.path.join(HERE, "bin", "Release", "net48", "TSVN.VS2022.dll"),
        help="newly built assembly, compared against the archive to catch a stale package",
    )
    parser.add_argument("--dry-run", action="store_true", help="report the plan and change nothing")
    parser.add_argument("--force", action="store_true", help="install even while Visual Studio runs")
    parser.add_argument(
        "--uninstall",
        action="store_true",
        help="remove the installed copy instead of installing one",
    )
    parser.add_argument(
        "--log",
        default=None,
        help="also write this report to a file, for the windowless launchers",
    )
    args = parser.parse_args()

    if args.log:
        # Under pythonw.exe there is no stdout at all, so a file is the only way a
        # failed run can say why. Redirecting keeps every message below unchanged.
        sys.stdout = open(args.log, "w", encoding="utf-8", errors="replace")

    roots = instance_roots()

    if not roots:
        print("[ERROR] no Visual Studio 2022 instance found under %LOCALAPPDATA%\\Microsoft\\VisualStudio")
        return 1

    if args.uninstall:
        return uninstall_flow(roots, args)

    return install_flow(roots, args)


def install_flow(roots, args):
    vsix = os.path.abspath(args.vsix)

    if not os.path.isfile(vsix):
        print("[ERROR] not found: %s" % vsix)
        print("        run build/build.cmd first, or pass --vsix")
        return 1

    if check_stale(vsix, args.assembly):
        print("[ERROR] the archive does not contain the assembly that was just built.")
        print("        repackage it with build/package-vsix.py so the two match.")
        return 1

    root, folder, message, first_time = resolve_target(roots)

    print("%s:" % message)
    print("  instance : %s" % root)
    print("  folder   : %s" % folder)
    print("  archive  : %s" % vsix)

    for stray in stray_copies(root, folder):
        print("  stray    : %s  (not registered, Visual Studio ignores it)" % stray)

    for stale in leftovers(roots):
        print("  leftover : %s  (older script left a second copy here)" % stale)

    if visual_studio_running() and not args.force and not args.dry_run:
        print("[ERROR] Visual Studio is running. Close it first, or pass --force.")
        return 1

    backup, installed = install(vsix, folder, args.dry_run)

    if args.dry_run:
        print("  backup   : would keep the previous folder as %s" % backup)
    else:
        if os.path.isdir(backup):
            print("  previous : %s" % backup)
        else:
            print("  previous : (none)")

        print("  installed: %s" % installed)
        print("  sha256   : %s" % sha256(installed))

    for path in clear_caches(root, args.dry_run):
        print("  cleared  : %s" % path)

    print()

    if args.dry_run:
        print("Dry run: nothing was changed.")
    else:
        print("Done. Start Visual Studio 2022 and open a versioned file.")

        if first_time and registered_install_path(root) is None:
            print()
            print("NOTE: this instance has no registration for the extension yet, so Visual")
            print("      Studio will probably not compose it. The first install needs")
            print("      VSIXInstaller once, which is what writes that entry; every install")
            print("      after it can come from here.")

    return 0


def uninstall_flow(roots, args):
    """Takes the extension back out, leaving the registration for the next install."""
    root, folder, _, _ = resolve_target(roots)

    print("Removing the installed extension:")
    print("  instance : %s" % root)
    print("  folder   : %s" % folder)

    for stray in stray_copies(root, folder):
        print("  stray    : %s  (not registered, Visual Studio ignores it)" % stray)

    if not os.path.isdir(folder):
        print()
        print("Nothing to remove: no folder is installed at that path.")
        return 0

    if visual_studio_running() and not args.force and not args.dry_run:
        print("[ERROR] Visual Studio is running. Close it first, or pass --force.")
        return 1

    backup = remove(folder, args.dry_run)

    if args.dry_run:
        print("  backup   : would move it to %s" % backup)
    else:
        print("  removed  : %s" % backup)

    for path in clear_caches(root, args.dry_run):
        print("  cleared  : %s" % path)

    print()

    if args.dry_run:
        print("Dry run: nothing was changed.")
    else:
        print("Done. Visual Studio starts without the extension.")
        print("The registration entry is deliberately left in place: it costs nothing,")
        print("and the next install writes to the same folder again.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
