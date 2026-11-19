"""Write each official add-on's built DLL into addons/addons.json: its version, its release address and its SHA-256.

2.0.1: they go in each entry's "current" - the build for gamer.farm 2.0.1 and later, under its own file name
(gamerfarm.<id>.dll). The fields outside "current" are left exactly as they are: they're the build every copy from before
2.0.1 takes (those read nothing else), made for the program's name then - the last one, attached to the v2.0.0 release.
A copy of 2.0.1 or later takes "current".

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
# Where "current" builds are: this repository's releases (2.0.0 and later take this address).
RELEASE = "https://github.com/VisaHolder/gamerfarm/releases/download/v{app}/{file}"
# The first version that takes "current".
CURRENT_FROM = "2.0.1"


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
        current = entry.get("current", {})
        file = current.get("file") or f"gamerfarm.{entry['id']}.dll"
        dll = built / file

        if not dll.exists():
            sys.exit(f"addons-index: {dll} wasn't built")

        # The add-on's own version, from its project file (addons/<id>/*.csproj).
        proj = next((ROOT / "addons" / entry["id"]).glob("*.csproj"))
        version = re.search(r"<Version>([^<]+)</Version>", proj.read_text(encoding="utf-8")).group(1)

        entry["current"] = {
            "version": version,
            "minAppVersion": current.get("minAppVersion") or CURRENT_FROM,
            "file": file,
            "url": RELEASE.format(app=app, file=file),
            "sha256": hashlib.sha256(dll.read_bytes()).hexdigest(),
        }
        # "source" last, as it was.
        if "source" in entry:
            entry["source"] = entry.pop("source")
        print(f"  {entry['id']} {version}  sha256 {entry['current']['sha256']}  (copies before {CURRENT_FROM} keep {entry['file']} {entry['version']})")

    write(data)


if __name__ == "__main__":
    main()
