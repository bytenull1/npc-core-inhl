#!/usr/bin/env python3
"""Trim a BepInEx log down to the NPC lines worth pasting into a session.

A raw capture is mostly overhead: every line carries `[Info   :  NPC.Core] ` and the
interesting lines repeat with only the coordinates changing. This strips the boilerplate
and collapses repetition without throwing away the numbers -- coordinates and node ids
are usually the evidence, so a run is folded to its first and last occurrences.

Kept: NPC.Core's lines, every NPC's (`<Mod>:<Name>` sources, docs/logging.md), the
plugin sources of those mods (`YourBuddy Mod` for `YourBuddy:*`), any --source, and
free-text notes added to the capture by hand.

--stats is usually the first thing to look at: it answers "what is this log mostly
made of" in a few dozen lines. --fold then gives a readable body at ~1/4 size.

Usage
    python tools/trimlog.py LogOutput.log                   # strip + exact dedupe
    python tools/trimlog.py LogOutput.log --fold            # also fold near-identical runs
    python tools/trimlog.py LogOutput.log --stats           # shape histogram, no body
    python tools/trimlog.py LogOutput.log --tag nav,ai      # only these tags
    python tools/trimlog.py LogOutput.log --npc "Buddy 2"   # only that NPC (Name or Mod:Name)
    python tools/trimlog.py LogOutput.log --all             # every source
    ... | python tools/trimlog.py -                         # read stdin

When a capture holds more than one NPC, each of their lines is prefixed with its name
(`Buddy 2| [ai] ...`), or with `Mod:Name` when the NPCs belong to several mods.
"""

import argparse
import re
import sys

# BepInEx line prefix: "[Info   :  NPC.Core] ", "[Warning:YourBuddy:Buddy] ", ...
BEPINEX = re.compile(r'^\[(?P<level>\w+)\s*:\s*(?P<source>[^\]]+)\]\s?')
# The subsystem tag, e.g. "[nav] ".
TAG = re.compile(r'^\[(?P<tag>[A-Za-z]+)\]\s?')
# Anything that varies run to run: numbers (incl. comma decimals) and #ids.
VARIABLE = re.compile(r'#\d+|-?\d+[.,]\d+|-?\d+')

CORE = 'NPC.Core'
LEVEL_MARK = {'Warning': '! ', 'Error': 'E ', 'Fatal': 'E '}


def shape(text):
    """Line with every number replaced, so near-identical lines compare equal."""
    return VARIABLE.sub('N', text)


def parse(line):
    """-> (source, level, tag, text) with the BepInEx prefix removed; source is None
    for a line BepInEx did not write (a note added by hand)."""
    line = line.rstrip('\n').rstrip()
    m = BEPINEX.match(line)
    if not m:
        return None, '', '', line
    text = line[m.end():]
    tag = ''
    t = TAG.match(text)
    if t:
        tag = t.group('tag')
        text = text[t.end():]
    return m.group('source').strip(), m.group('level'), tag, text


def npc_of(source):
    """(mod, name) for an NPC's `<Mod>:<Name>` source, else None."""
    if source is None or ':' not in source:
        return None
    mod, name = source.split(':', 1)
    return mod, name


def emit(out, level, who, tag, text):
    out.append('%s%s%s%s' % (LEVEL_MARK.get(level, ''), '%s| ' % who if who else '',
                             '[%s] ' % tag if tag else '', text))


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument('path', help='log file, or - for stdin')
    ap.add_argument('--tag', help='comma-separated tags to keep (nav, ai, probe, save, talk, ...)')
    ap.add_argument('--fold', action='store_true',
                    help='keep only the first and last few occurrences of each repeated line shape')
    ap.add_argument('--keep', type=int, default=2, metavar='N',
                    help='with --fold, how many of each shape to keep at each end (default 2)')
    ap.add_argument('--stats', action='store_true',
                    help='print a shape histogram instead of the log body')
    ap.add_argument('--all', action='store_true', help='keep lines from every source')
    ap.add_argument('--source', help='comma-separated extra sources to keep (prefix match)')
    ap.add_argument('--npc', metavar='NAME', help="only this NPC's lines, by Name or Mod:Name")
    args = ap.parse_args()

    stream = sys.stdin if args.path == '-' else open(args.path, 'r', encoding='utf-8', errors='replace')
    lines = [raw for raw in stream if raw.strip()]
    if stream is not sys.stdin:
        stream.close()

    parsed = [parse(raw) for raw in lines]
    npcs = {npc_of(src) for src, _l, _t, _x in parsed} - {None}
    mods = {mod for mod, _name in npcs}
    extra = [s.strip() for s in args.source.split(',')] if args.source else []
    wanted = {t.strip() for t in args.tag.split(',')} if args.tag else None
    # Names only when there is more than one NPC to tell apart; Mod:Name across mods.
    naming = len(npcs) > 1
    by_mod = len(mods) > 1

    def kept(source):
        if args.all or source is None or source == CORE or npc_of(source):
            return True
        return any(source.startswith(p) for p in list(mods) + extra)

    rows = []
    for source, level, tag, text in parsed:
        if not kept(source):
            continue
        npc = npc_of(source)
        if wanted is not None and tag and tag not in wanted:
            continue
        if args.npc is not None and npc and args.npc not in (npc[1], source):
            continue
        who = (source if by_mod else npc[1]) if npc and naming else ''
        rows.append((level, who, tag, text))

    if args.stats:
        counts = {}
        for _level, who, tag, text in rows:
            key = ('%s| ' % who if who else '') + ('[%s] ' % tag if tag else '') + shape(text)
            counts[key] = counts.get(key, 0) + 1
        for key, n in sorted(counts.items(), key=lambda kv: -kv[1]):
            print('%6d  %s' % (n, key))
        print('\n%d lines, %d distinct shapes' % (len(rows), len(counts)))
        return

    out = []
    if args.fold:
        # Sample by shape rather than by run length: real captures interleave, so exact
        # repeating-block detection almost never fires, while a handful of shapes still
        # accounts for most of the file.
        total = {}
        for _level, who, tag, text in rows:
            key = (who, tag, shape(text))
            total[key] = total.get(key, 0) + 1
        seen = {}
        dropped = 0
        for level, who, tag, text in rows:
            key = (who, tag, shape(text))
            n = seen[key] = seen.get(key, 0) + 1
            # First N and last N of each shape: the drift between them is often the
            # finding (a position that never changes means a livelock).
            if n <= args.keep or n > total[key] - args.keep:
                if dropped:
                    out.append('    ... %d lines of shapes already shown ...' % dropped)
                    dropped = 0
                emit(out, level, who, tag, text)
            else:
                dropped += 1
        if dropped:
            out.append('    ... %d lines of shapes already shown ...' % dropped)
    else:
        i = 0
        while i < len(rows):
            reps = 1
            while i + reps < len(rows) and rows[i + reps][1:] == rows[i][1:]:
                reps += 1
            emit(out, *rows[i])
            if reps > 1:
                out.append('    ... x%d identical ...' % (reps - 1))
            i += reps

    sys.stdout.write('\n'.join(out) + '\n')
    sys.stderr.write('trimlog: %d lines -> %d\n' % (len(lines), len(out)))


if __name__ == '__main__':
    main()
