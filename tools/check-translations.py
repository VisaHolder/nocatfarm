"""Check that every language pack covers every string the app can show.

    python tools/check-translations.py

English is the key, so an untranslated string never breaks anything - it quietly stays English. That is
exactly why this needs checking: nothing else will ever notice. It finds every translatable string the way the
app itself uses them, then reports, per language:

  - strings the pack is missing
  - translations whose {0}/{1} placeholders don't match the English (a dropped {1} silently loses a value)
  - settings with no translated label or explanation
  - text typed with its accents stripped ("fuer" for "für") - still a translation, still wrong

and, across the code, English that reaches a translated sentence as one of its VALUES. The frame translates and
the value doesn't - "rep4rep: 休息 23h47m (manual reset)". A value only translates if it is itself a Said, and
English finds four ways in:

  literal   written straight into the arguments
  variable  a local holding English          string frees = "until ~" + ...
  field     a field or property holding it   _state.BlockReason = "manual reset"
  helper    a method that returns English    Word(persona) -> "invisible"

Exits non-zero if anything is found, so it can gate a release.
"""

import io
import json
import pathlib
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT = pathlib.Path(__file__).resolve().parent.parent / "src" / "NocatFarm"
LANG = ROOT / "wwwroot" / "lang"

# Words that read the same in every language, or are not prose at all.
PROTOCOL = {"GET", "POST", "PUT", "DELETE", "HEAD", "OK"}

# Words that are only ever a misspelling: the same word with its accents typed as plain ASCII. A batch of setting
# explanations went in like this in all six Latin-script languages - "fuer", "kazdy", "nao", "gunun" - and every
# presence check passed, because a stripped translation is still a translation. Chosen so that no correctly
# written word matches: "premier" and "ze" are real words, "fuer" and "moze" never are.
STRIPPED = {
    "de": r"fuer|ueber|waehrend|naechste[nrs]?|moeglich\w*|laenger|aender\w*|heisst|zurueck|spaeter|taeglich\w*|ungefaehr|stueck|koennen|muessen|wuerde\w*|hoeher|laeuft|haelt|standardmaessig|bloecke\w*",
    "fr": r"journee|differente?s?|etre|deja|tres|apres|parametres?|periode|systeme|ecran|reessai\w*|desactive|recoivent|etroite|epuisement",
    "pl": r"kazd\w*|wiecej|dluzej|roznych|dzialac|miedzy|czekac|laduje|wylaczone|domyslnie|zeby|moze|juz|rowniez|wlasna|jesli|sie",
    "pt-BR": r"nao|voce|entao|tambem|sao|estao|ate|numero|padrao|sessoes|horarios|preco",
    "es": r"tambien|ademas|despues|numero|asi|dia|dias|codigo|rapido|segun",
    "tr": r"gunun|gunde|esya\w*|calis\w*|yukle\w*|dosyasini|klasor\w*|kisit\w*|icin|degil|cunku|guven\w*|ozel",
}


# ── reading literals ─────────────────────────────────────────────────────

def cs_literal(src, i):
    """Decode the ordinary C# string literal starting at src[i] == '"'. Returns (value, index after it)."""
    out, j = [], i + 1
    escapes = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", '"': '"', "\\": "\\", "'": "'"}

    while j < len(src):
        c = src[j]

        if c == "\\":
            nxt = src[j + 1]

            if nxt == "u":
                out.append(chr(int(src[j + 2:j + 6], 16)))
                j += 6
                continue

            out.append(escapes.get(nxt, nxt))
            j += 2
            continue

        if c == '"':
            return "".join(out), j + 1

        out.append(c)
        j += 1

    raise ValueError("unterminated literal")


def skip_cs_string(src, i):
    """Index just past the C# string starting at src[i] (a quote, or $ before one). Tracks interpolation holes,
    so a hole containing its own literals - $"{string.Join(", ", x)}" - does not end the string early."""
    interp = src[i] == "$"
    j = i + (2 if interp else 1)
    depth = 0

    while j < len(src):
        c = src[j]

        if c == "\\":
            j += 2
            continue

        if interp and (depth > 0) and (c == '"'):
            j = skip_cs_string(src, j)
            continue

        if interp and (c == "{"):
            if src[j + 1:j + 2] == "{":
                j += 2
                continue
            depth += 1
        elif interp and (c == "}"):
            if (depth == 0) and (src[j + 1:j + 2] == "}"):
                j += 2
                continue
            depth -= 1
        elif (c == '"') and (depth <= 0):
            return j + 1

        j += 1

    return j


def js_literal(src, i):
    """Decode the JS string literal starting at src[i] (quote or backtick). None if it interpolates."""
    q, out, j = src[i], [], i + 1

    while j < len(src):
        c = src[j]

        if c == "\\":
            out.append({"n": "\n", "t": "\t"}.get(src[j + 1], src[j + 1]))
            j += 2
            continue

        if (q == "`") and src.startswith("${", j):
            return None, j                     # a template with a hole is not a fixed key

        if c == q:
            return "".join(out), j + 1

        out.append(c)
        j += 1

    return None, j


def call_args(src, after_open):
    """Split the argument list that starts just past an open paren. Literals are kept whole."""
    depth, i, args, cur = 1, after_open, [], ""

    while (i < len(src)) and depth:
        c = src[i]

        if (c == '"') or ((c == "$") and (src[i + 1:i + 2] == '"')):
            j = skip_cs_string(src, i)
            cur += src[i:j]
            i = j
            continue

        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1

            if depth == 0:
                break
        elif (c == ",") and (depth == 1):
            args.append(cur.strip())
            cur = ""
            i += 1
            continue

        cur += c
        i += 1

    if cur.strip():
        args.append(cur.strip())

    return args


def source_files():
    return {p: io.open(p, encoding="utf-8").read() for p in sorted(ROOT.rglob("*.cs"))
            if not p.relative_to(ROOT).as_posix().startswith(("obj/", "bin/"))}


# ── what needs translating ───────────────────────────────────────────────

def used_keys(files):
    """Every English string the app passes to the translator, with where it was first seen."""
    keys = {}

    for path, src in files.items():
        rel = path.relative_to(ROOT).as_posix()

        # Only a plain literal is a key. `new Said(reason)` translates whatever reason holds at run time, and
        # an interpolated $"..." is a bug in its own right - it can never match a pack entry.
        for m in re.finditer(r'(?:new Said|Loc\.T|Loc\.Is)\(\s*(?=")', src):
            value, _ = cs_literal(src, m.end())

            if value:
                keys.setdefault(value, f"{rel}:{src.count(chr(10), 0, m.start()) + 1}")

    js = ROOT / "wwwroot" / "app.js"
    src = io.open(js, encoding="utf-8").read()

    for m in re.finditer(r"\b(?:t|tf)\(\s*(?=['\"`])", src):
        value, _ = js_literal(src, m.end())

        if value:
            keys.setdefault(value, f"wwwroot/app.js:{src.count(chr(10), 0, m.start()) + 1}")

    for html in (ROOT / "wwwroot").glob("*.html"):
        text = io.open(html, encoding="utf-8").read()

        for m in re.finditer(r'data-t(?:-ph|-tip)?="([^"]*)"', text):
            value = m.group(1).replace("&quot;", '"').replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">")

            if value:
                keys.setdefault(value, f"wwwroot/{html.name}")

    # A key with no words in it - "{0}", "{0} {1} -> {2}" - reads the same in every language, and the English
    # fallback is already the right answer. Nothing to translate, so nothing to report.
    return {k: v for k, v in keys.items() if re.search(r"[^\W\d_]", re.sub(r"\{\d+\}", "", k))}


def settings():
    """Setting names declared in Settings.cs - each needs a translated label and explanation."""
    src = io.open(ROOT / "Config" / "Settings.cs", encoding="utf-8").read()

    return sorted(set(re.findall(r'\bnew\("(\w+)",\s*"', src)))


def slots(text):
    return sorted(set(re.findall(r"\{(\d+)\}", text)))


# ── English riding inside a translated sentence ─────────────────────────

def prose(expr):
    """The first English literal in an expression: a run of 3+ letters outside any {hole}. None if there isn't."""
    i = 0

    while i < len(expr):
        if (expr[i] == '"') or ((expr[i] == "$") and (expr[i + 1:i + 2] == '"')):
            j = skip_cs_string(expr, i)
            lit = expr[i:j]
            body = lit[2:-1] if lit.startswith("$") else lit[1:-1]
            bare = re.sub(r"\{[^}]*\}", "", body)

            # Not prose: a JSON field name handed to a lookup ("tag_name"), or a URL or path ("/tradeoffer/...").
            machine = re.fullmatch(r"[a-z0-9]+(?:_[a-z0-9]+)+", body) or body.startswith("/") or ("://" in body)

            if (body.strip() not in PROTOCOL) and (not machine) and re.search(r"[A-Za-z]{3}", bare):
                return body

            i = j
            continue

        i += 1

    return None


def translated(expr):
    return ("new Said(" in expr) or ("Loc.T(" in expr) or (re.match(r"new\s*\(", expr) is not None)


def english_values(files):
    keys = set()

    for pack in LANG.glob("*.json"):
        keys |= set(json.load(io.open(pack, encoding="utf-8")).get("ui", {}))

    # Helpers whose body returns a pack key as a plain string. Bot.Word is the model: deliberately English,
    # because code compares against it, and so every display has to wrap it.
    helpers = {}

    for src in files.values():
        for m in re.finditer(r"\b(?:private|public|internal|protected)[\w\s]*?\bstring\??\s+(\w+)\s*\(", src):
            body = src[m.end():m.end() + 1500]
            end = body.find("\n\t}")
            body = body[:end if end > 0 else len(body)]
            hits = [x for x in re.findall(r'"((?:[^"\\]|\\.)*)"', body)
                    if (x in keys) and re.fullmatch(r"[a-z][a-z' ]{3,}", x)]

            if hits:
                helpers[m.group(1)] = hits[0]

    found = []

    for path, src in files.items():
        rel = path.relative_to(ROOT).as_posix()

        for m in re.finditer(r"(?:new Said|Loc\.T)\(", src):
            args = call_args(src, m.end())
            line = src.count("\n", 0, m.start()) + 1

            if (not args) or (not args[0].startswith('"')):
                continue

            for a in args[1:]:
                if translated(a):
                    continue

                lit = prose(a)

                if lit is not None:
                    found.append((rel, line, "literal", f'"{lit[:50]}"'))
                    continue

                call = re.match(r"(?:[\w.]*\.)?(\w+)\s*\(", a)

                if call and (call.group(1) in helpers):
                    found.append((rel, line, "helper", f'{call.group(1)}() returns e.g. "{helpers[call.group(1)]}"'))
                    continue

                # A local: look back through the enclosing method for what was put in it.
                if re.fullmatch(r"[a-z_]\w*", a):
                    before = src[max(0, m.start() - 5000):m.start()]
                    top = before.rfind("\n\t}")
                    scope = before[top if top > 0 else 0:]

                    for d in re.finditer(r"\b(?:string\??|var)\s+" + re.escape(a) + r"\s*=\s*([^;]+);", scope):
                        if (not translated(d.group(1))) and ((lit := prose(d.group(1))) is not None):
                            found.append((rel, line, "variable", f'{a} = "{lit[:44]}"'))
                            break

                    continue

                # A field or property: anything that assigns English to the same dotted name, anywhere.
                if re.fullmatch(r"\w+(?:\.\w+)+", a) and (len(a.split(".")[-1]) >= 4):
                    for opath, osrc in files.items():
                        for w in re.finditer(r"\b" + re.escape(a) + r'\s*=\s*("(?:[^"\\]|\\.)*")', osrc):
                            if (lit := prose(w.group(1))) is not None:
                                found.append((rel, line, "field",
                                              f'{a} = "{lit[:40]}" in {opath.relative_to(ROOT).as_posix()}'))

    # Parameters: a method that puts a string parameter into a sentence raw, and a caller that passes English.
    # RestFullDayAsync("manual reset") -> BlockReason -> "resting 23h (manual reset)" was this shape.
    decl = re.compile(r"\b(?:public|private|internal|protected)[\w\s<>,\[\]?]*?\b(\w+)\s*\(([^)]*)\)\s*\{")

    for path, src in files.items():
        for m in decl.finditer(src):
            method, params = m.group(1), m.group(2)
            body = src[m.end():m.end() + 6000]
            end = body.find("\n\t}")
            body = body[:end if end > 0 else len(body)]
            order = [p.strip().split("=")[0].split() for p in params.split(",") if p.strip()]

            for idx, parts in enumerate(order):
                if (len(parts) < 2) or (not parts[-2].rstrip("?").endswith("string")):
                    continue

                name = parts[-1]
                raw = False

                for sm in re.finditer(r"(?:new Said|Loc\.T)\(", body):
                    if any(re.fullmatch(re.escape(name), a) for a in call_args(body, sm.end())[1:]):
                        raw = True
                        break

                if not raw:
                    continue

                for opath, osrc in files.items():
                    for c in re.finditer(r"\b" + re.escape(method) + r"\s*\(", osrc):
                        cargs = call_args(osrc, c.end())

                        if (idx < len(cargs)) and cargs[idx].startswith('"') and ((lit := prose(cargs[idx])) is not None):
                            found.append((opath.relative_to(ROOT).as_posix(), osrc.count("\n", 0, c.start()) + 1,
                                          "parameter", f'{method}("{lit[:40]}") -> {name}, used raw in a sentence'))

    return found


# ── report ───────────────────────────────────────────────────────────────

def main():
    files = source_files()
    keys = used_keys(files)
    names = settings()
    packs = sorted(LANG.glob("*.json"))
    problems = 0

    print(f"{len(keys)} translatable strings, {len(names)} settings, {len(packs)} language packs\n")

    for pack in packs:
        data = json.load(io.open(pack, encoding="utf-8"))
        ui, st = data.get("ui", {}), data.get("settings", {})

        missing = [k for k in keys if not ui.get(k)]
        bad_slots = [k for k in keys if ui.get(k) and (slots(ui[k]) != slots(k))]
        no_label = [n for n in names if not (st.get(n) or {}).get("label")]
        no_tip = [n for n in names if not (st.get(n) or {}).get("tip")]
        backslash = [k for k in ui if "\\" in k]

        texts = [(k, v) for k, v in ui.items()] + [(f"setting {n}.{f}", e.get(f, "")) for n, e in st.items() for f in ("label", "tip")]
        # A hit only counts if the matched text is itself plain ASCII - a stripped word always is. Case-insensitive
        # matching folds Turkish dotless "ı" into "i", so the correct "dosyasını" matches "dosyasini"; and ASCII-only
        # word boundaries are no better, splitting "Paramètres" at the è and finding "tres".
        rx = re.compile(r"\b(" + STRIPPED[pack.stem] + r")\b", re.I) if pack.stem in STRIPPED else None
        stripped = [(k, hit) for k, v in texts if rx
                    for hit in [next((m.group(0) for m in rx.finditer(v) if m.group(0).isascii()), None)] if hit]

        issues = len(missing) + len(bad_slots) + len(no_label) + len(no_tip) + len(backslash) + len(stripped)
        problems += issues

        print(f"  {'OK ' if issues == 0 else '-- '} {pack.stem:6}  {len(keys) - len(missing)}/{len(keys)} strings"
              + (f", {len(missing)} missing" if missing else "")
              + (f", {len(bad_slots)} placeholder mismatch" if bad_slots else "")
              + (f", {len(no_label)} labels / {len(no_tip)} tips missing" if (no_label or no_tip) else "")
              + (f", {len(stripped)} with accents stripped" if stripped else ""))

        for k in missing[:40]:
            print(f"         missing      {k!r}   ({keys[k]})")

        for k in bad_slots:
            print(f"         placeholders {k!r}  en={slots(k)}  {pack.stem}={slots(ui[k])}")

        for n in (no_label + no_tip)[:20]:
            print(f"         setting      {n}")

        for k in backslash:
            print(f"         backslash    {k!r}  (a key with a backslash can never match)")

        for k, word in stripped[:20]:
            print(f"         accents      {word!r} in {k[:60]!r}  (typed without its accents)")

    values = english_values(files)
    print(f"\nEnglish reaching a translated sentence as a value: {len(values) or 'none'}")

    for rel, line, route, what in values:
        print(f"  {route:9} {rel}:{line}  {what}")

    problems += len(values)
    print("\nall clear" if problems == 0 else f"\n{problems} problem(s)")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
