"""Write each official add-on's built DLL into addons/addons.json: its version, its release address and its SHA-256.

    python tools/addons-index.py <app version> <folder with the built DLLs>
    python tools/addons-index.py --bump        only the serial, for a list re-signed without a new build

Run by tools/package-release.ps1 after it builds the add-ons, before the index is signed. The file is written with LF
line endings and no BOM - the signature (addons.json.sig) is over these exact bytes, and .gitattributes keeps git from
changing them.

Every run raises "serial" by one. The app remembers the highest serial it has taken and refuses a list with a lower
one, so an old signed list served again can't take an add-on back to an older (fixed) version. Never lower it by hand:
every copy that has seen the higher number would refuse the list.
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


def write(data):
    # The serial first in the file, one higher than the last list's.
    serial = int(data.get("serial", 0)) + 1
    data = {"serial": serial, **{k: v for k, v in data.items() if k != "serial"}}
    io.open(INDEX, "w", encoding="utf-8", newline="\n").write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")
    print(f"  serial {serial}")


def main():
    data = json.load(io.open(INDEX, encoding="utf-8"))

    if sys.argv[1:] == ["--bump"]:
        write(data)
        return

    app, built = sys.argv[1], pathlib.Path(sys.argv[2])

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

    write(data)


if __name__ == "__main__":
    main()
