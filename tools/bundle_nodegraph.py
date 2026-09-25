#!/usr/bin/env python3
"""Regenerate the nav graph shipped inside the DLL.

    python tools/bundle_nodegraph.py [--version N] [--source <nodegraph.json>]
    python tools/bundle_nodegraph.py --check [--base REF]

Reads your live graph (default: BepInEx/config/NPC.Core/nodegraph.json, found by walking
up from this repository) and writes NPC.Core/Resources/nodegraph.bundled.json
with a BundleVersion stamp. The plugin rewrites a user's cached copy whenever that
number changes, which is what makes an updated graph land without touching anyone's
own nodes - see docs/navigation.md.

Bump the version every time you change the graph, or nobody will get the update.

--check writes nothing: it validates the committed bundle and exits 1 on a problem. With
--base REF it also requires a bigger BundleVersion than REF's whenever the file changed.
"""
import argparse
import json
import os
import subprocess
import sys

# Must match NavGraph.UserIdBase: ids at or above it belong to players, so the
# bundle may never use one or a later update would collide with somebody's own node.
USER_ID_BASE = 100000

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "NPC.Core", "Resources", "nodegraph.bundled.json")


def default_source():
    """BepInEx/config/NPC.Core/nodegraph.json, from any ancestor game folder."""
    path = REPO
    while True:
        candidate = os.path.join(path, "BepInEx", "config", "NPC.Core", "nodegraph.json")
        if os.path.isfile(candidate):
            return candidate
        parent = os.path.dirname(path)
        if parent == path:
            return None
        path = parent


def problems(graph, bundled):
    """Everything wrong with a graph, as a list of messages (empty when it is fine)."""
    found = []
    if not isinstance(graph.get("Version"), int) or graph["Version"] < 4:
        found.append("Version is %r; a legacy (v3) graph must be opened in the game once to upgrade it."
                     % graph.get("Version"))
    ids = [n.get("Id") for g in graph.get("Graphs", []) for n in g.get("Nodes", [])]
    high = sorted(i for i in ids if isinstance(i, int) and i >= USER_ID_BASE)
    if high:
        found.append("Node ids %s are at or above %d, which is reserved for players' own nodes. "
                     "Renumber them before bundling." % (high[:5], USER_ID_BASE))
    dupes = sorted({i for i in ids if ids.count(i) > 1})
    if dupes:
        found.append("Duplicate node ids %s." % dupes[:5])
    known = set(ids)
    dangling = sorted({e for l in graph.get("Links", []) for e in (l.get("A"), l.get("B"))
                       if e not in known})
    if dangling:
        found.append("Links point at missing nodes %s." % dangling[:5])
    if bundled:
        if not isinstance(graph.get("BundleVersion"), int):
            found.append("BundleVersion is missing or not an integer.")
        if "ForkedOwners" in graph:
            found.append("ForkedOwners must not be bundled.")
    return found


def base_bundle(ref):
    """The bundle as committed at ref, or None if it did not exist there."""
    rel = os.path.relpath(OUT, REPO).replace(os.sep, "/")
    try:
        text = subprocess.run(["git", "show", "%s:%s" % (ref, rel)], cwd=REPO, check=True,
                              capture_output=True, text=True, encoding="utf-8").stdout
    except subprocess.CalledProcessError:
        return None
    return json.loads(text)


def check(base):
    """--check: validate the committed bundle; returns the exit code."""
    rel = os.path.relpath(OUT, REPO).replace(os.sep, "/")
    try:
        with open(OUT, encoding="utf-8") as handle:
            bundled = json.load(handle)
    except (OSError, ValueError) as error:
        print("%s: %s" % (rel, error))
        return 1
    found = problems(bundled, bundled=True)
    if base:
        old = base_bundle(base)
        stale = old is not None and old != bundled \
            and bundled.get("BundleVersion", 0) <= old.get("BundleVersion", 0)
        if stale:
            found.append("The bundle changed since %s but BundleVersion did not go up (%s -> %s). "
                         "Regenerate it with tools/bundle_nodegraph.py."
                         % (base, old.get("BundleVersion"), bundled.get("BundleVersion")))
    for message in found:
        print("%s: %s" % (rel, message))
    if not found:
        print("%s: ok (BundleVersion %s)" % (rel, bundled.get("BundleVersion")))
    return 1 if found else 0


def owner_name(g):
    return g.get("Owner") or "world"


def carry_unforked(graph, previous):
    """A user file holds only the owners the player forked; every other owner still
    comes from the bundle. Copy those, with their links, into graph so regenerating
    never drops them. Returns the owners carried over."""
    if not previous:
        return []
    taken = {owner_name(g) for g in graph.get("Graphs", [])}
    taken.update(graph.get("ForkedOwners") or [])
    kept = [g for g in previous.get("Graphs", []) if owner_name(g) not in taken]
    if not kept:
        return []

    graph.setdefault("Graphs", []).extend(kept)
    kept_ids = {n.get("Id") for g in kept for n in g.get("Nodes", [])}
    all_ids = {n.get("Id") for g in graph["Graphs"] for n in g.get("Nodes", [])}
    links = graph.setdefault("Links", [])
    for link in previous.get("Links", []):
        ends = (link.get("A"), link.get("B"))
        if any(e in kept_ids for e in ends) and all(e in all_ids for e in ends):
            links.append(link)
    return [owner_name(g) for g in kept]


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", type=int, help="BundleVersion to stamp (default: current + 1)")
    parser.add_argument("--source", dest="source", help="graph to bundle (default: your live one)")
    parser.add_argument("--check", action="store_true",
                        help="validate the committed bundle instead of writing one")
    parser.add_argument("--base", help="with --check: git ref the bundle must be newer than if changed")
    args = parser.parse_args()

    if args.check:
        sys.exit(check(args.base))

    source = args.source or default_source()
    if not source or not os.path.isfile(source):
        sys.exit("No source graph found. Pass --source <path to nodegraph.json>.")

    with open(source, encoding="utf-8") as handle:
        graph = json.load(handle)

    previous_bundle = None
    if os.path.isfile(OUT):
        with open(OUT, encoding="utf-8") as handle:
            previous_bundle = json.load(handle)

    version = args.version
    if version is None:
        version = (previous_bundle or {}).get("BundleVersion", 0) + 1

    carried = carry_unforked(graph, previous_bundle)

    # ForkedOwners is a user-file concept; a bundle that carried one would tell every
    # installation that the player had already taken that owner over.
    graph.pop("ForkedOwners", None)
    bundled = {"Version": graph.get("Version", 4), "BundleVersion": version}
    bundled.update({k: v for k, v in graph.items() if k not in ("Version", "BundleVersion")})

    # Checked against the merged graph, not the raw source: a link from a node you own to
    # one in an owner you never forked (e.g. your station node to the ship's airlock) is
    # only resolvable once carry_unforked has pulled that owner in.
    found = problems(bundled, bundled=True)
    if found:
        sys.exit("\n".join(found))

    if carried:
        print("Kept from the previous bundle (not forked in the source): %s" % ", ".join(carried))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(bundled, handle, indent=2)
        handle.write("\n")

    nodes = sum(len(g.get("Nodes", [])) for g in bundled.get("Graphs", []))
    owners = ", ".join(g.get("Owner") or "world" for g in bundled.get("Graphs", []))
    print("Bundled version %d: %d nodes, %d links (%s)"
          % (version, nodes, len(bundled.get("Links", [])), owners))
    print("Wrote %s" % OUT)
    print("Rebuild the plugin so the DLL carries it.")


if __name__ == "__main__":
    main()
