#!/usr/bin/env python3
"""Check the docs against themselves and against the code. Exits 1 on any error.

    python tools/doccheck.py [--verbose]

Two checks:

1. Links. Every relative link in the Markdown files, and every `docs/<file>.md#anchor`
   in the C# comments, must name a file that exists and a heading that exists in it
   (GitHub anchor rules). Code links to invariants by anchor, so a renamed heading
   silently orphans them - see AGENTS.md section 4.
   Links into NPC.Core - `npc-core:docs/<file>.md#anchor` in code, the repository's GitHub
   URL in Markdown - are checked against a checkout of it beside this one
   (../npc-core-inhl), and listed as notes when there is none.
2. Constants. Each `| \\`Name\\` | value |` row under a "### ... - `File.cs`" heading in
   docs/reference.md must match the literal in the code. Rows the script can't compare
   (a computed value, a name it can't find) are listed with --verbose, not failed.

Output is `file:line: message`. No dependencies.
"""
import argparse
import glob
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REFERENCE = "docs/reference.md"
# The C# project folder: this repository's own source, searched recursively.
SOURCE = next(d for d in ("NPC.Core", "YourBuddy") if os.path.isdir(os.path.join(REPO, d)))
CORE_URL = "https://github.com/bytenull1/npc-core-inhl/blob/main/"
CORE_ROOT = REPO if SOURCE == "NPC.Core" else os.path.join(os.path.dirname(REPO), "npc-core-inhl")

MD_LINK = re.compile(r'\[[^\]]*\]\(([^)\s]+)\)')
# Not inside a URL or a path: those are links of their own, or not ours.
CODE_LINK = re.compile(r'(?<![\w/.:-])(npc-core:)?(docs/[A-Za-z0-9_-]+\.md)(?:#([A-Za-z0-9_-]+))?')
HEADING = re.compile(r'^(#{1,6})\s+(.*?)\s*#*\s*$')
FENCE = re.compile(r'^\s*(```|~~~)')
SECTION_FILE = re.compile(r'^###\s.*`([A-Za-z0-9_.]+\.cs)`\s*$')
ROW = re.compile(r'^\|\s*((?:`[A-Za-z0-9_]+`\s*/?\s*)+)\|\s*([^|]+?)\s*\|')
NUMBER = re.compile(r'-?\d+(?:\.\d+)?')
DECL = r'\b(?:const|static\s+readonly)\s+[A-Za-z0-9_.<>?\[\]]+\s+%s\s*=\s*([^;]+);'
SIMPLE_EXPR = re.compile(r'^[\d.\s+\-*/()]+$')


def rel(path):
    return os.path.relpath(path, REPO).replace(os.sep, "/")


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read().splitlines()


def slug(text):
    """GitHub's heading anchor: strip markup, lowercase, drop punctuation, spaces to '-'."""
    text = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', text)
    text = re.sub(r'<[^>]+>', '', text).lower()
    text = re.sub(r'[^\w\- ]', '', text)
    return text.replace(' ', '-')


def anchors(path, cache={}):
    """All heading anchors of a Markdown file, with GitHub's -1, -2 suffixes for repeats."""
    if path not in cache:
        found, seen, fenced = set(), {}, False
        for line in read(path):
            if FENCE.match(line):
                fenced = not fenced
                continue
            m = None if fenced else HEADING.match(line)
            if m:
                base = slug(m.group(2))
                n = seen.get(base, 0)
                seen[base] = n + 1
                found.add(base if n == 0 else "%s-%d" % (base, n))
        cache[path] = found
    return cache[path]


def source_files(*patterns):
    """This repository's C# files, the samples' included, bin/ and obj/ left out."""
    found = []
    for pattern in patterns:
        found += glob.glob(os.path.join(REPO, SOURCE, '**', pattern), recursive=True)
        found += glob.glob(os.path.join(REPO, 'samples', '**', pattern), recursive=True)
    skip = (os.sep + 'bin' + os.sep, os.sep + 'obj' + os.sep)
    return sorted(set(f for f in found if not any(s in f for s in skip)))


def check_core_target(where, target, errors, notes):
    """A link into NPC.Core: resolved against the checkout beside this one, if there is one."""
    if not os.path.isdir(os.path.join(CORE_ROOT, "docs")):
        notes.append("%s: %s not checked, no NPC.Core checkout at %s" % (where, target, CORE_ROOT))
        return
    check_target(where, CORE_ROOT, target, errors)


def check_target(where, base_dir, target, errors, notes=None):
    """One link: file must exist, anchor (if any) must be one of its headings."""
    if target.startswith(CORE_URL) and notes is not None:
        check_core_target(where, target[len(CORE_URL):], errors, notes)
        return
    if re.match(r'^[a-z]+:', target) or target.startswith('//'):
        return
    path_part, _, anchor = target.partition('#')
    path = os.path.normpath(os.path.join(base_dir, path_part)) if path_part else None
    if path and not os.path.exists(path):
        errors.append("%s: link to missing file %s" % (where, path_part))
        return
    if not anchor:
        return
    if path and not path.endswith('.md'):
        return
    doc = path or where.rsplit(':', 1)[0]
    doc = doc if os.path.isabs(doc) else os.path.join(REPO, doc)
    if anchor not in anchors(doc):
        errors.append("%s: no heading #%s in %s" % (where, anchor, rel(doc)))


def markdown_files():
    names = glob.glob(os.path.join(REPO, '*.md')) + glob.glob(os.path.join(REPO, 'docs', '*.md'))
    names += glob.glob(os.path.join(REPO, '.github', '**', '*.md'), recursive=True)
    names += glob.glob(os.path.join(REPO, 'samples', '*', 'README.md'))
    for sub in ('decompiled', 'assetripper', os.path.join(SOURCE, 'lib')):
        names += glob.glob(os.path.join(REPO, sub, 'README.md'))
    return sorted(n for n in names if os.path.basename(n) != 'LAST_SESSION.md')


def check_links(errors, notes):
    count = 0
    for path in markdown_files():
        fenced = False
        for number, line in enumerate(read(path), 1):
            if FENCE.match(line):
                fenced = not fenced
            if fenced:
                continue
            line = re.sub(r'`[^`]*`', '', line)
            for m in MD_LINK.finditer(line):
                count += 1
                check_target("%s:%d" % (rel(path), number), os.path.dirname(path), m.group(1), errors, notes)
    for path in source_files('*.cs') + sorted(glob.glob(os.path.join(REPO, 'tools', '*.py'))):
        for number, line in enumerate(read(path), 1):
            for m in CODE_LINK.finditer(line):
                count += 1
                where = "%s:%d" % (rel(path), number)
                target = m.group(2) + ('#' + m.group(3) if m.group(3) else '')
                if m.group(1) and SOURCE != "NPC.Core":
                    check_core_target(where, target, errors, notes)
                else:
                    check_target(where, REPO, target, errors)
    return count


def number(expr):
    """A literal or simple arithmetic like `5 * 60f` as a float, else None."""
    expr = re.sub(r'(?<=\d)[fFdDmM]\b', '', expr.strip())
    if not SIMPLE_EXPR.match(expr):
        return None
    try:
        return float(eval(expr, {"__builtins__": {}}))
    except (SyntaxError, ZeroDivisionError):
        return None


def source_value(name, preferred):
    """(values, file) of a constant - one value, or several for an array - searching the
    named file and its partials first. values is None if it isn't made of plain numbers."""
    stem = os.path.splitext(preferred)[0]
    first = source_files(stem + '.cs', stem + '.*.cs')
    rest = sorted(set(source_files('*.cs')) - set(first))
    decl = re.compile(DECL % re.escape(name))
    for path in first + rest:
        m = decl.search('\n'.join(read(path)))
        if m:
            expr = m.group(1).strip()
            array = re.match(r'^\[(.*)\]$', expr, re.S)
            inner = re.match(r'^new(?:\s+[A-Za-z]+)?\((.*)\)$', expr)
            parts = array.group(1).split(',') if array else [inner.group(1) if inner else expr]
            values = [number(p) for p in parts]
            return (None if None in values else values), rel(path)
    return None, None


def documented(cell):
    """Numbers in a value cell, each with the scales it may be stored at in code."""
    values = []
    cell = cell.replace('±', '')
    for m in NUMBER.finditer(cell):
        after = cell[m.end():m.end() + 3].strip()
        value = float(m.group())
        scales = [value]
        if after.startswith('%') or after.startswith('cm'):
            scales.append(value / 100)
        values.append(scales)
    return values


def check_constants(errors, unchecked):
    path = os.path.join(REPO, REFERENCE)
    section, checked = None, 0
    for number, line in enumerate(read(path), 1):
        if line.startswith('#'):
            m = SECTION_FILE.match(line)
            section = m.group(1) if m else None
            continue
        m = ROW.match(line) if section else None
        if not m:
            continue
        where = "%s:%d" % (REFERENCE, number)
        names = re.findall(r'`([A-Za-z0-9_]+)`', m.group(1))
        doc = documented(m.group(2))
        code = [(name,) + source_value(name, section) for name in names]
        missing = [(n, f) for n, v, f in code if v is None]
        if missing:
            for name, found_in in missing:
                unchecked.append("%s: %s %s" % (where, name, "is not a plain number in " + found_in
                                                if found_in else "not found in the code"))
            continue
        # An array constant takes as many documented numbers as it has elements.
        if sum(len(v) for _, v, _ in code) != len(doc):
            unchecked.append("%s: %d values in the code but %d numbers in '%s'"
                             % (where, sum(len(v) for _, v, _ in code), len(doc), m.group(2)))
            continue
        for name, values, found_in in code:
            shown, doc = doc[:len(values)], doc[len(values):]
            if all(any(abs(a - s) < 1e-6 for s in scales) for a, scales in zip(values, shown)):
                checked += 1
            else:
                errors.append("%s: %s is %s in the doc but %s in %s"
                              % (where, name, " / ".join("%g" % s[0] for s in shown),
                                 " / ".join("%g" % a for a in values), found_in))
    return checked


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--verbose", action="store_true", help="also list rows it could not compare")
    args = parser.parse_args()

    errors, unchecked = [], []
    links = check_links(errors, unchecked)
    constants = check_constants(errors, unchecked)
    if args.verbose:
        for message in unchecked:
            print("note: " + message)
    for message in errors:
        print(message)
    print("%d links, %d constants checked, %d constants not comparable, %d errors"
          % (links, constants, len(unchecked), len(errors)))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
