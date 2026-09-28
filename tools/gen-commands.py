"""
Writes docs/COMMANDS.md - every command nocat.farm has - straight from the command list in src/NocatFarm/Commands.cs,
so the docs can never drift from the app. Run it whenever a command changes (package-release.ps1 runs it too):

    python tools/gen-commands.py
"""
import io, os, re

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
SRC = os.path.join(ROOT, "src", "NocatFarm", "Commands.cs")
OUT = os.path.join(ROOT, "docs", "COMMANDS.md")

s = io.open(SRC, encoding="utf-8-sig").read().replace("\r\n", "\n")
groups = dict(re.findall(r'public const string (Group\w+) = "([^"]*)";', s))
body = s[s.index("IReadOnlyList<CommandDef> All = ["):]
body = body[:body.index("\n\t];")]

STR = r'"((?:[^"\\]|\\.)*)"'


def unescape(x):
    return x.replace('\\"', '"').replace("\\\\", "\\")


commands = []
for m in re.finditer(r'new\(\s*' + STR + r',\s*' + STR + r',\s*(Group\w+),\s*' + STR + r'(?:,\s*' + STR + r')?\s*\)', body):
    name, args, group, help_, aliases = m.groups()
    commands.append({"name": unescape(name), "args": unescape(args), "group": groups[group],
                     "help": unescape(help_), "aliases": [a for a in unescape(aliases or "").split("|") if a]})

assert len(commands) > 40, "the command list didn't parse"

order = []
for c in commands:
    if c["group"] not in order:
        order.append(c["group"])


def slug(text):
    return re.sub(r"[^\w\- ]", "", text.lower()).strip().replace(" ", "-")


def cell(text):
    return text.replace("|", "\\|")


lines = [
    "# nocat.farm commands",
    "",
    "Every command nocat.farm has, and what it does. You can type them in the app window, in the dashboard's "
    "**Console** tab, on **Telegram** (start with `/`), on **Discord** (`/nocat command:` runs any of them), or send them to one of your accounts in **Steam chat** "
    "(start with `/` or `!`, from an account listed under *Accept commands from*).",
    "",
    "In the app, `help` lists them all and `help <command>` explains one - it also explains any setting: "
    "`help FarmCards`.",
    "",
    "**How to read them:** `<account>` is something you must type (an account's name in nocat.farm); `[count]` "
    "is optional; `a|b` means one or the other. Most commands that take an account also take `all`, for every "
    "account.",
    "",
    "*This page is generated from the app's own command list, so it always matches the version you download.*",
    "",
    "## All commands at a glance",
    "",
    "| | |",
    "|---|---|",
]
for g in order:
    names = " ".join(f"`{c['name']}`" for c in commands if c["group"] == g)
    lines.append(f"| **[{g}](#{slug(g)})** | {names} |")
lines += ["", f"{len(commands)} commands in all.", ""]

for g in order:
    lines += [f"## {g}", "", "| Command | What it does |", "|---|---|"]
    for c in (c for c in commands if c["group"] == g):
        usage = f"`{c['name']}{(' ' + c['args']) if c['args'] else ''}`"
        also = (" <br>also " + ", ".join(f"`{a}`" for a in c["aliases"])) if c["aliases"] else ""
        lines.append(f"| {cell(usage)}{also} | {cell(c['help'])} |")
    lines.append("")

lines += [
    "## More",
    "",
    "- [The full guide](GUIDE.md) - how everything works, every setting, Linux and Docker.",
    "- [Plugins](../PLUGINS.md) - add your own commands in a few lines of C#. A plugin's commands are listed by "
    "`plugins` and on the dashboard's Plugins page.",
    "",
]

text = "\n".join(lines)
old = io.open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else ""
if old != text:
    io.open(OUT, "w", encoding="utf-8", newline="\n").write(text)
    print(f"wrote docs/COMMANDS.md ({len(commands)} commands)")
else:
    print(f"docs/COMMANDS.md is up to date ({len(commands)} commands)")
