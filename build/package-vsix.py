#!/usr/bin/env python3
"""Package the built TSVN extension into an installable VSIX.

The Visual Studio 2022 installer does not read a plain OPC package: it resolves the
extension through the VS Installer pipeline, which requires ``manifest.json`` and
``catalog.json`` next to ``extension.vsixmanifest``. Without them VSIXInstaller reports
"The file is not a valid VSIX package" while pointing at the manifest.

Entry layout (order matters only for readability):

    extension.vsixmanifest
    TSVN.VS2022.dll
    TSVN.VS2022.pkgdef
    <runtime dependencies>
    Resources/...
    [Content_Types].xml
    manifest.json
    catalog.json

Usage:
    python build/package-vsix.py --assembly <path to TSVN.VS2022.dll> [--deps <dir>] [--out <vsix>]
"""

import argparse
import io
import json
import os
import sys
import zipfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
NS = "{http://schemas.microsoft.com/developer/vsx-schema/2011}"

# Runtime dependencies shipped next to the extension assembly. Optional .pdb files are
# picked up when present so a debug build produces a debuggable package.
DEPENDENCIES = [
    "Caliburn.Micro.Core",
    "Community.VisualStudio.Toolkit",
    "Microsoft.ApplicationInsights",
]

# (source in repo, entry name in the package). The license lives at the repo root but is
# shipped as Resources/LICENSE, matching what extension.vsixmanifest declares.
RESOURCES = [
    (os.path.join(REPO, "Resources", "Package.ico"), "Resources/Package.ico"),
    (os.path.join(REPO, "Resources", "tortoisesvn.png"), "Resources/tortoisesvn.png"),
    (os.path.join(REPO, "LICENSE"), "Resources/LICENSE"),
]

CONTENT_TYPES = (
    '<?xml version="1.0" encoding="utf-8"?>'
    '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
    '<Default Extension="vsixmanifest" ContentType="text/xml" />'
    '<Default Extension="dll" ContentType="application/octet-stream" />'
    '<Default Extension="pdb" ContentType="application/octet-stream" />'
    '<Default Extension="pkgdef" ContentType="text/plain" />'
    '<Default Extension="ico" ContentType="application/octet-stream" />'
    '<Default Extension="png" ContentType="application/octet-stream" />'
    '<Default Extension="json" ContentType="application/json" />'
    '<Override PartName="/Resources/LICENSE" ContentType="application/octet-stream" />'
    "</Types>"
)


def read_manifest():
    path = os.path.join(HERE, "extension.vsixmanifest")
    with open(path, "rb") as fh:
        raw = fh.read()
    root = ET.fromstring(raw)
    identity = root.find(f"{NS}Metadata/{NS}Identity")
    display = root.find(f"{NS}Metadata/{NS}DisplayName")
    description = root.find(f"{NS}Metadata/{NS}Description")
    return {
        "raw": raw,
        "id": identity.get("Id"),
        "version": identity.get("Version"),
        "name": display.text,
        "description": description.text,
    }


def collect(deps_dir, assembly):
    """Return an ordered list of (entry name, bytes) for the payload."""
    items = [("TSVN.VS2022.dll", open(assembly, "rb").read())]

    pkgdef = os.path.join(HERE, "TSVN.VS2022.pkgdef")
    items.append(("TSVN.VS2022.pkgdef", open(pkgdef, "rb").read()))

    for base in DEPENDENCIES:
        for ext in (".dll", ".pdb"):
            path = os.path.join(deps_dir, base + ext)
            if os.path.exists(path):
                items.append((base + ext, open(path, "rb").read()))

    for src, name in RESOURCES:
        items.append((name, open(src, "rb").read()))

    return items


def build_package(out_path, meta, payload, vsix_size_hint, target_drive):
    """Write the VSIX. Sizes inside the catalog are informational, so a hint is enough."""
    # The manifest lists every part it describes, including itself.
    files = [{"fileName": "/extension.vsixmanifest", "sha256": None}]
    files += [{"fileName": "/" + name, "sha256": None} for name, _ in payload]

    manifest_json = {
        "id": meta["id"],
        "version": meta["version"],
        "type": "Vsix",
        "vsixId": meta["id"],
        "extensionDir": "[installdir]\\Common7\\IDE\\Extensions\\tsvn",
        "files": files,
        "installSizes": {"targetDrive": target_drive},
        "dependencies": {"Microsoft.VisualStudio.Component.CoreEditor": "15.0"},
    }

    catalog_json = {
        "manifestVersion": "1.1",
        "info": {"id": f"{meta['id']},version={meta['version']}", "manifestType": "Extension"},
        "packages": [
            {
                "id": "Component." + meta["id"],
                "version": meta["version"],
                "type": "Component",
                "extension": True,
                "dependencies": {
                    meta["id"]: meta["version"],
                    "Microsoft.VisualStudio.Component.CoreEditor": "15.0",
                },
                "localizedResources": [
                    {
                        "language": "en-US",
                        "title": meta["name"],
                        "description": meta["description"],
                    }
                ],
            },
            {
                "id": meta["id"],
                "version": meta["version"],
                "type": "Vsix",
                "payloads": [{"fileName": "TSVN.VS2022.vsix", "size": vsix_size_hint}],
                "vsixId": meta["id"],
                "extensionDir": "[installdir]\\Common7\\IDE\\Extensions\\tsvn",
                "installSizes": {"targetDrive": target_drive},
            },
        ],
    }

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("extension.vsixmanifest", meta["raw"])
        for name, data in payload:
            zf.writestr(name, data)
        zf.writestr("[Content_Types].xml", CONTENT_TYPES.encode("utf-8"))
        zf.writestr("manifest.json", json.dumps(manifest_json, separators=(",", ":")))
        zf.writestr("catalog.json", json.dumps(catalog_json, separators=(",", ":")))


def main():
    parser = argparse.ArgumentParser(description="Package TSVN into a VSIX.")
    parser.add_argument("--assembly", required=True, help="TSVN.VS2022.dll to package")
    parser.add_argument(
        "--deps",
        default=os.path.join(HERE, "bin", "Release", "net48"),
        help="folder holding the runtime dependencies (default: build output)",
    )
    parser.add_argument(
        "--out",
        default=os.path.join(HERE, "bin", "TSVN.VS2022.vsix"),
        help="output .vsix path (default: build\\bin, which is git-ignored)",
    )
    args = parser.parse_args()

    for path in (args.assembly, args.deps):
        if not os.path.exists(path):
            sys.exit("not found: " + path)

    meta = read_manifest()
    payload = collect(args.deps, args.assembly)
    target_drive = sum(len(data) for _, data in payload)

    # The catalog records its own package size, so build once to measure, then rebuild.
    hint = 0
    for _ in range(2):
        build_package(args.out, meta, payload, hint, target_drive)
        hint = os.path.getsize(args.out)

    print(f"id      : {meta['id']} {meta['version']}")
    print(f"name    : {meta['name']}")
    print(f"deps    : {args.deps}")
    print(f"out     : {args.out}")
    print(f"size    : {os.path.getsize(args.out)}")
    print("entries :")
    with zipfile.ZipFile(args.out) as zf:
        for info in zf.infolist():
            print("  {:<40} {:>9}".format(info.filename, info.file_size))


if __name__ == "__main__":
    main()
