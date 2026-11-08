"""Write each official add-on's built DLL into addons/addons.json: its version, its release address and its SHA-256.

    python tools/addons-index.py <app version> <folder with the built DLLs>

Run by tools/package-release.ps1 after it builds the add-ons, before the index is signed. The file is written with LF
line endings and no BOM, byte for byte the same every time for the same input - the signature (addons.json.sig) is over
these exact bytes, and .gitattributes keeps git from changing them.
"""

import hashlib
import io
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
INDEX = ROOT / "addons" / "addons.json"
RELEASE = "https://github.com/VisaHolder/nocatfarm/releases/download/v{app}/{file}"


def main():
    app, built = sys.argv[1], pathlib.Path(sys.argv[2])
    data = json.load(io.open(INDEX, encoding="utf-8"))

    for entry in data["addons"]:
        dll = built / entry["file"]

        if not dll.exists():
            sys.exit(f"addons-index: {dll} wasn't built")

        # The add-on's own version, from its project file (addons/<id>/*.csproj).
        proj = next((ROOT / "addons" / entry["id"]).glob("*.csproj"))
        version = re.search(r"<Version>([^<]+)</Version>", proj.read_text(encoding="utf-8")).group(1)

        entry["version"] = version
        entry["url"] = RELEASE.format(app=app, file=entry["file"])
        entry["sha256"] = hashlib.sha256(dll.read_bytes()).hexdigest()
        print(f"  {entry['id']} {version}  sha256 {entry['sha256']}")

    io.open(INDEX, "w", encoding="utf-8", newline="\n").write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main()
