"""Normalize generated VSIX template manifest paths.

VSSDK expands the template zip under ProjectTemplates/ but may write that
same prefix into RelativePathOnDisk.  The VSIX asset path is already
ProjectTemplates, so Visual Studio resolves the template relative to that
root.  A repeated ProjectTemplates/ prefix points outside the actual files.

Usage: python tools/normalize_vsix_template_paths.py <path-to-vsix>
"""
import os
import re
import sys
import tempfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

VSIX_NS = "{http://schemas.microsoft.com/developer/vsx-schema/2011}"
MAN_NS = "{http://schemas.microsoft.com/developer/vstemplatemanifest/2015}"


def normalize(vsix_path: Path) -> bool:
    with zipfile.ZipFile(vsix_path) as z:
        names = set(z.namelist())
        v = ET.fromstring(z.read("extension.vsixmanifest"))
        assets = [a.attrib["Path"].strip("/") for a in v.findall(
            f".//{VSIX_NS}Asset") if a.attrib.get("Type")
            == "Microsoft.VisualStudio.ProjectTemplate"]
        if len(assets) != 1:
            raise ValueError(f"Expected one project-template asset, got {assets}")
        root = assets[0]
        index = f"{root}/templateManifest0.noloc.vstman"
        raw = z.read(index).decode("utf-8-sig")
        parsed = ET.fromstring(raw)
        fixes = []

        for template in parsed.findall(f".//{MAN_NS}VSTemplateContainer"):
            folder = template.find(f"{MAN_NS}RelativePathOnDisk")
            filename = template.find(f"{MAN_NS}TemplateFileName")
            if folder is None or filename is None:
                raise ValueError("Incomplete .vstman template entry")
            rel = folder.text.replace("\\", "/").strip("/")
            fname = filename.text
            # Resolution is relative to <Asset Path="ProjectTemplates">.
            expected_file = f"{root}/{rel}/{fname}"
            if expected_file in names:
                continue
            if not rel.lower().startswith(root.lower() + "/"):
                raise ValueError(f"Template absent: {expected_file}")
            adjusted = rel[len(root) + 1:]
            if f"{root}/{adjusted}/{fname}" not in names:
                raise ValueError(f"Corrected template path absent: {root}/{adjusted}/{fname}")
            fixes.append((folder.text, adjusted.replace("/", "\\")))

        for old, new in fixes:
            src = f"<RelativePathOnDisk>{old}</RelativePathOnDisk>"
            dst = f"<RelativePathOnDisk>{new}</RelativePathOnDisk>"
            if raw.count(src) != 1:
                raise ValueError(f"Unexpected RelativePathOnDisk entry: {src}")
            raw = raw.replace(src, dst)

        if not fixes:
            print("All VSIX template paths are already relative to the asset root")
            return False

        fd, tempname = tempfile.mkstemp(prefix="rhino-dock-vsix-", suffix=".vsix",
                                       dir=vsix_path.parent)
        os.close(fd)
        try:
            with zipfile.ZipFile(tempname, "w") as out:
                for info in z.infolist():
                    data = raw.encode("utf-8") if info.filename == index else z.read(info.filename)
                    out.writestr(info, data)
            os.replace(tempname, vsix_path)
        finally:
            if os.path.exists(tempname):
                os.remove(tempname)
    print("Normalized VSIX template path(s): " +
          ", ".join(f"{old} -> {new}" for old, new in fixes))
    return True


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: normalize_vsix_template_paths.py <VSIX>")
    normalize(Path(sys.argv[1]))
