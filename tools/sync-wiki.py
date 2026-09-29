"""
Copies the wiki in docs/ to the repo's GitHub Wiki tab, so the two never drift apart.

docs/ is the source: it's versioned with the code and reviewed with it. The Wiki tab is a copy made from it -
edits made there are overwritten by the next run.

    python tools/sync-wiki.py <path to a clone of nocatfarm.wiki.git>

Then commit and push in that clone. What it does to each page:
  - names it for the wiki (docs/human-mode.md -> Human-Mode.md, docs/README.md -> Home.md)
  - rewrites links between pages to wiki links (human-mode.md#x -> Human-Mode#x)
  - points links and pictures outside docs/ at the repo on GitHub
  - drops each page's own top/bottom navigation line - the wiki's sidebar (_Sidebar.md) does that job
"""
import os
import re
import sys

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DOCS = os.path.join(REPO, "docs")
BLOB = "https://github.com/VisaHolder/nocatfarm/blob/main/"
RAW = "https://raw.githubusercontent.com/VisaHolder/nocatfarm/main/"

# Wiki page names, in contents order. Pages not listed here aren't copied (GUIDE.md is only a signpost for old links).
PAGES = [
    ("README.md", "Home"),
    ("start-here.md", "Start-Here"),
    ("dashboard.md", "Dashboard"),
    ("accounts.md", "Accounts"),
    ("cards-and-idling.md", "Cards-and-Idling"),
    ("human-mode.md", "Human-Mode"),
    ("trades.md", "Trades"),
    ("achievements.md", "Achievements"),
    ("phone-and-notifications.md", "Phone-and-Notifications"),
    ("rep4rep.md", "rep4rep"),
    ("settings.md", "Settings"),
    ("COMMANDS.md", "Commands"),
    ("linux-docker-vps.md", "Linux-Docker-and-VPS"),
    ("plugins.md", "Plugins"),
    ("safety-and-faq.md", "Safety-and-FAQ"),
]
WIKI = {src: name for src, name in PAGES}


def title_of(text):
    m = re.search(r"^# (.+)$", text, re.M)
    return m.group(1).strip() if m else ""


def link(target):
    """A markdown link target from docs/, as it should read in the wiki."""
    if re.match(r"^[a-z]+:", target) or target.startswith("#"):
        return target
    path, _, anchor = target.partition("#")
    anchor = "#" + anchor if anchor else ""
    name = os.path.basename(path)
    if os.path.dirname(path) in ("", ".") and name in WIKI:
        return WIKI[name] + anchor
    full = os.path.normpath(os.path.join("docs", path)).replace("\\", "/")
    if name in ("GUIDE.md",):
        return "Home" + anchor
    if re.search(r"\.(png|jpe?g|gif|svg|webp)$", name, re.I):
        return RAW + full
    return BLOB + full + anchor


def convert(text):
    lines = text.splitlines()
    # The page's own nav lines: the first line with "Wiki home" and the last line with an arrow.
    lines = [l for l in lines if not re.match(r"^\s*\[(Wiki home|The wiki)\]\(README\.md\)", l)
             and not re.match(r"^\s*(\[?←|←|.*→\s*$)", l.strip()) or not l.strip()]
    text = "\n".join(lines).strip()
    text = re.sub(r"(\n\s*---\s*)+$", "", text).strip() + "\n"   # the divider that sat above the nav line
    text = re.sub(r"(!?\[[^\]]*\])\(([^)\s]+)\)", lambda m: m.group(1) + "(" + link(m.group(2)) + ")", text)
    text = re.sub(r'(<img[^>]*\bsrc=")([^"]+)(")', lambda m: m.group(1) + link(m.group(2)) + m.group(3), text)
    return text


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    out = os.path.abspath(sys.argv[1])
    written = []
    sidebar = ["**[nocat.farm](Home)**", ""]
    for src, name in PAGES:
        path = os.path.join(DOCS, src)
        if not os.path.exists(path):
            print("missing", src)
            continue
        with open(path, encoding="utf-8-sig") as f:
            text = f.read()
        if src != "README.md":
            label = "Every command" if src == "COMMANDS.md" else title_of(text) or name.replace("-", " ")
            sidebar.append(f"- [{label}]({name})")
        with open(os.path.join(out, name + ".md"), "w", encoding="utf-8", newline="\n") as f:
            f.write(convert(text))
        written.append(name)
    sidebar += ["", f"[Download]({'https://github.com/VisaHolder/nocatfarm/releases/latest'}) · [nocat.lol/nocatfarm](https://nocat.lol/nocatfarm)"]
    with open(os.path.join(out, "_Sidebar.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(sidebar) + "\n")
    with open(os.path.join(out, "_Footer.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"These pages are copied from [docs/ in the repo]({BLOB}docs/README.md) with every release - change them there.\n")
    # Pages the wiki had that docs/ no longer does.
    for f in os.listdir(out):
        if f.endswith(".md") and not f.startswith("_") and f[:-3] not in written:
            os.remove(os.path.join(out, f))
            print("removed", f)
    print("wrote", len(written), "pages + sidebar and footer")


if __name__ == "__main__":
    main()
