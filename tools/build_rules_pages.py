"""Builds the rules pages (one for the Edison brain, one per deck) from docs/reglas/*.md.

Usage: python tools/build_rules_pages.py
Output: docs/pages/<page>.html, ready to publish as artifacts (the URLs are listed in CLAUDE.md).

Each markdown file is split into entries: every `###` heading is one entry (a card or rule), and the text
under a `#` or `##` heading before its first subheading is an entry of its own. Entries with the same
normalized title on a deck page and on the Edison page are shown side by side ("base" vs "in this deck").
"""
import json
import re
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RULES = ROOT / "docs" / "reglas"
OUT = ROOT / "docs" / "pages"
TEMPLATE = Path(__file__).with_name("rules_page_template.html")

# To add a deck: write docs/reglas/<key>.md and add a line here.
DECKS = [
    ("blackwings", "Blackwings"),
    ("lightsworn", "Lightsworn"),
]


def fold(text):
    return unicodedata.normalize("NFD", text).encode("ascii", "ignore").decode().lower()


def key_of(title):
    """Matching key between pages: lowercase, no accents, no parenthetical notes."""
    return re.sub(r"\s+", " ", re.sub(r"\(.*?\)", "", fold(title))).strip()


def slug(text):
    return re.sub(r"[^a-z0-9]+", "-", fold(text)).strip("-")[:60]


def parse(path, source):
    entries, part, group = [], "", ""
    title, level, buf = None, 0, []

    def flush():
        body = "\n".join(line for line in buf if line.strip() != "---").strip()
        if title is not None and body:
            ident = slug(f"{source}-{title}")
            n = sum(1 for e in entries if e["id"].startswith(ident))
            entries.append({
                "id": ident if n == 0 else f"{ident}-{n + 1}",
                "key": key_of(title),
                "title": title,
                "part": part,
                "group": group if level == 3 else "",
                "md": body,
                "source": source,
            })

    for line in path.read_text(encoding="utf-8").splitlines():
        m = re.match(r"^(#{1,3}) (.+)$", line)
        if m:
            flush()
            buf = []
            level, text = len(m.group(1)), m.group(2).strip()
            if level == 1:
                part, group = text, ""
            elif level == 2:
                group = text
            title = text
        else:
            buf.append(line)
    flush()
    return entries


def build(page, heading, kind, entries, base=None, deck_name=None):
    data = {"page": page, "deckName": deck_name, "entries": entries, "base": base or []}
    payload = json.dumps(data, ensure_ascii=False).replace("</", "<\\/")
    html = TEMPLATE.read_text(encoding="utf-8")
    html = (html.replace("__TITLE__", heading).replace("__HEADING__", heading)
                .replace("__KIND__", kind).replace("__DATA__", payload))
    OUT.mkdir(parents=True, exist_ok=True)
    out = OUT / f"{page}.html"
    out.write_text(html, encoding="utf-8")
    print(f"{out.relative_to(ROOT)}: {len(entries)} entries")


def main():
    edison = parse(RULES / "edison.md", "edison")
    build("edison", "Edison Brain", "Reglas del bot · cualquier mazo", edison)
    for key, name in DECKS:
        deck = parse(RULES / f"{key}.md", "deck")
        build(key, f"{name} Bot", "Reglas del bot · mazo", deck, base=edison, deck_name=name)


if __name__ == "__main__":
    main()
