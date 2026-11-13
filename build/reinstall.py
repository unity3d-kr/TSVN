"""Build TSVN.VS2022.dll, package it, and install it, without opening a window.

This is the whole procedure in one process. Every step is a console program -
resgen, dotnet, python - so each of them would flash a window if it were started
normally, and a .cmd wrapper would hand them one to inherit on top of its own.
Each child is started with CREATE_NO_WINDOW instead, and this script never is a
console program itself when it is launched through pythonw.exe, which is what
reinstall.vbs does. The result is a run with no window at any point.

Output goes to install.log next to this file, and is echoed as well when there is
a console to echo to. A failed run therefore still leaves a readable record on
disk after the window that ran it has gone.

    python build/reinstall.py               # build, package, install
    python build/reinstall.py --build-only  # stop after packaging
    python build/reinstall.py --install-only

The exit code is the first failing step's, or 0.
"""

import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

LOG_PATH = os.path.join(HERE, "install.log")

# Same flag Visual Studio's own build uses: the child gets no console at all.
CREATE_NO_WINDOW = 0x08000000

ASSEMBLY = os.path.join(HERE, "bin", "Release", "net48", "TSVN.VS2022.dll")

# resgen is only shipped with the .NET Framework targeting pack / SDK, and the
# version installed varies, so the same list build.cmd used is tried in order.
RESGEN_CANDIDATES = (
    r"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\resgen.exe",
    r"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7.2 Tools\resgen.exe",
    r"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.6.1 Tools\resgen.exe",
)


class Log(object):
    """Append to install.log, and echo to the console when there is one."""

    def __init__(self, path):
        self._handle = open(path, "w", encoding="utf-8", errors="replace")

    def write(self, text):
        self._handle.write(text)
        self._handle.flush()

        # sys.stdout is None when this runs under pythonw.exe.
        if sys.stdout is None:
            return

        try:
            sys.stdout.write(text)
            sys.stdout.flush()
        except (OSError, ValueError):
            pass

    def close(self):
        self._handle.close()


def python_executable():
    """python.exe, not pythonw.exe: a GUI interpreter cannot be used as a tool host."""
    executable = sys.executable or ""

    if not executable:
        return "python"

    folder, name = os.path.split(executable)

    if name.lower() == "pythonw.exe":
        console = os.path.join(folder, "python.exe")

        if os.path.isfile(console):
            return console

    return executable


def find_resgen():
    roots = [
        os.environ.get("ProgramFiles(x86)"),
        os.environ.get("ProgramFiles"),
    ]

    for root in roots:
        if not root:
            continue

        for relative in RESGEN_CANDIDATES:
            candidate = os.path.join(root, relative)

            if os.path.isfile(candidate):
                return candidate

    return None


def run(log, title, command):
    """Run one step and log everything it printed. Returns its exit code."""
    log.write(title + "\n")

    started = time.time()

    try:
        result = subprocess.run(
            command,
            cwd=HERE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            encoding="utf-8",
            errors="replace",
            creationflags=CREATE_NO_WINDOW,
        )
    except OSError as error:
        log.write("    cannot start %s: %s\n\n" % (command[0], error))
        return 1

    output = result.stdout or ""

    for line in output.splitlines():
        log.write("    " + line + "\n")

    elapsed = time.time() - started

    if result.returncode == 0:
        log.write("    ok (%.1fs)\n\n" % elapsed)
    else:
        log.write("    FAILED with exit code %d (%.1fs)\n\n" % (result.returncode, elapsed))

    return result.returncode


def build(log):
    resgen = find_resgen()

    if resgen is None:
        log.write("[ERROR] resgen.exe not found. Install the .NET Framework 4.8\n")
        log.write("        targeting pack, or the Windows SDK.\n")
        return 1

    resources = os.path.join(HERE, "obj", "OptionsDialog.resources")

    code = run(
        log,
        "==> [1/3] Compiling OptionsDialog.resx",
        [resgen, os.path.join(REPO, "TSVN.Shared", "Options", "OptionsDialog.resx"), resources],
    )

    if code != 0:
        return code

    code = run(
        log,
        "==> [2/3] Building TSVN.VS2022.dll",
        ["dotnet", "build", "TSVN.Build.csproj", "-c", "Release", "--nologo"],
    )

    if code != 0:
        return code

    return run(
        log,
        "==> [3/3] Packaging VSIX",
        [python_executable(), "package-vsix.py", "--assembly", ASSEMBLY],
    )


def install(log):
    return run(log, "==> Installing into Visual Studio 2022", [python_executable(), "install-vsix.py"])


def main():
    build_only = "--build-only" in sys.argv[1:]
    install_only = "--install-only" in sys.argv[1:]

    log = Log(LOG_PATH)

    try:
        log.write("TSVN - %s\n" % time.strftime("%Y-%m-%d %H:%M:%S"))
        log.write("repo: %s\n\n" % REPO)

        code = 0

        if not install_only:
            code = build(log)

        if code == 0 and not build_only:
            code = install(log)

        if code == 0:
            log.write("=== DONE ===\n")
            log.write("Start Visual Studio 2022 and open a versioned file: ")
            log.write("TortoiseSVN > Show Diff Markers.\n")
        else:
            log.write("=== FAILED - nothing was installed ===\n")

            # The usual reason the install step refuses: the assembly is loaded.
            log.write("If Visual Studio is running, close it and run this again.\n")

        return code
    finally:
        log.close()


if __name__ == "__main__":
    sys.exit(main())
