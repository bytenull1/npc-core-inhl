#!/usr/bin/env python3
"""Read the game's scene file offline: hierarchy, scripts, and serialized references.

The decompile tells you what the code does; it has no scene data. Questions like "which
GameObject carries this script", "what is this room parented under", or "what does this
component's serialized field point at" are answered by Isolated Inhale_Data/level1.

It is a Unity 2022.1.20f1 SerializedFile, format 22, WITHOUT type trees, so generic
tools that rely on type trees see nothing useful and this reader hard-codes the layouts
it needs (GameObject, Transform, MonoBehaviour header, MonoScript). No dependencies.

Usage
    python tools/unityscene.py tree [--root PATH] [--depth N] [--scripts]
    python tools/unityscene.py find NAME            # GameObjects whose name contains NAME
    python tools/unityscene.py script CLASS         # GameObjects carrying that MonoBehaviour
    python tools/unityscene.py refs PATH            # GameObjects a GO's scripts reference
    ... --data "<game>/Isolated Inhale_Data"        # if it isn't found by walking up

PATH is a slash path from a root, e.g. StaticObjects/StationParts. Inactive objects are
shown with [off]. As a library: load_scene(), load_monoscripts(), Scene.
"""
import argparse
import os
import struct
import sys


class R:
    def __init__(self, data, endian='<'):
        self.d = data; self.p = 0; self.e = endian
    def u(self, fmt, n):
        v = struct.unpack_from(self.e + fmt, self.d, self.p); self.p += n; return v[0]
    def i32(self): return self.u('i', 4)
    def u32(self): return self.u('I', 4)
    def i64(self): return self.u('q', 8)
    def u16(self): return self.u('H', 2)
    def i16(self): return self.u('h', 2)
    def u8(self): v = self.d[self.p]; self.p += 1; return v
    def f32(self): return self.u('f', 4)
    def raw(self, n): v = self.d[self.p:self.p + n]; self.p += n; return v
    def cstr(self):
        end = self.d.index(b'\0', self.p); s = self.d[self.p:end].decode('utf-8', 'replace'); self.p = end + 1; return s
    def align(self, a=4): self.p = (self.p + a - 1) // a * a
    def s(self):
        n = self.i32(); v = self.d[self.p:self.p + n].decode('utf-8', 'replace'); self.p += n; self.align(); return v
    def pptr(self): return (self.i32(), self.i64())


class SFile:
    """Header, type list, object table and externals of a SerializedFile (format >= 22)."""
    def __init__(self, path):
        self.path = path
        data = open(path, 'rb').read()
        h = R(data, '>')
        h.u32(); h.u32(); self.version = h.u32(); h.u32()
        big = h.u8(); h.raw(3)
        assert self.version >= 22, self.version
        h.u32(); h.i64(); self.data_offset = h.i64(); h.i64()
        e = '>' if big else '<'
        m = R(data, e); m.p = h.p
        self.unity = m.cstr(); self.platform = m.i32(); self.typetree = m.u8() != 0
        tcount = m.i32(); self.types = []
        for _ in range(tcount):
            cid = m.i32(); m.u8(); m.i16()
            if cid == 114: m.raw(16)
            m.raw(16)
            if self.typetree:
                nn = m.i32(); sb = m.i32(); m.raw(nn * 32); m.raw(sb)
                dc = m.i32(); m.raw(dc * 4)
            self.types.append(cid)
        ocount = m.i32(); self.objects = {}
        for _ in range(ocount):
            m.align(); pid = m.i64(); start = m.i64() + self.data_offset; size = m.u32(); tid = m.i32()
            self.objects[pid] = (self.types[tid], start, size)
        sc = m.i32()
        for _ in range(sc):
            m.i32(); m.align(); m.i64()
        ec = m.i32(); self.externals = []
        for _ in range(ec):
            m.cstr(); m.raw(16); m.i32(); self.externals.append(m.cstr())
        self.data = data; self.e = '<'  # object data is little-endian on Windows

    def reader(self, pid):
        cid, start, size = self.objects[pid]
        r = R(self.data, self.e); r.p = start
        return r, start + size


def load_scene(path):
    """-> (SFile, gos, trs, mbs), all keyed by pathID."""
    f = SFile(path)
    gos, trs, mbs = {}, {}, {}
    for pid, (cid, start, size) in f.objects.items():
        if cid == 1:  # GameObject
            r, _ = f.reader(pid)
            comps = [r.pptr() for _ in range(r.i32())]
            layer = r.u32(); name = r.s(); r.u16(); active = r.u8()
            gos[pid] = dict(name=name, comps=comps, active=active, layer=layer)
        elif cid in (4, 224):  # Transform, RectTransform
            r, _ = f.reader(pid)
            go = r.pptr(); rot = [r.f32() for _ in range(4)]; pos = [r.f32() for _ in range(3)]; scl = [r.f32() for _ in range(3)]
            children = [r.pptr() for _ in range(r.i32())]; father = r.pptr()
            trs[pid] = dict(go=go[1], pos=pos, rot=rot, scl=scl, children=[c[1] for c in children], father=father[1])
        elif cid == 114:  # MonoBehaviour: header, then the script's own fields in `body`
            r, end = f.reader(pid)
            go = r.pptr(); r.u8(); r.align(); script = r.pptr(); r.s()
            mbs[pid] = dict(go=go[1], script=script, body=(r.p, end))
    return f, gos, trs, mbs


def load_monoscripts(path):
    """MonoScript pathID -> 'Namespace.Class'. The scene refers to these as external fileID 1."""
    f = SFile(path)
    out = {}
    for pid, (cid, start, size) in f.objects.items():
        if cid != 115: continue
        r, _ = f.reader(pid)
        r.s(); r.i32(); r.raw(16); cls = r.s(); ns = r.s(); r.s()
        out[pid] = (ns + '.' if ns else '') + cls
    return f, out


class Scene:
    def __init__(self, data_dir):
        self.f, self.gos, self.trs, self.mbs = load_scene(os.path.join(data_dir, 'level1'))
        _, self.scripts = load_monoscripts(os.path.join(data_dir, 'globalgamemanagers.assets'))
        self.tr_of_go = {t['go']: tid for tid, t in self.trs.items()}

    def script_name(self, mb):
        fid, spid = mb['script']
        return self.scripts.get(spid, '?') if fid == 1 else '?'

    def scripts_on(self, goid):
        return [(pid, self.script_name(self.mbs[pid])) for _, pid in self.gos[goid]['comps'] if pid in self.mbs]

    def label(self, goid):
        go = self.gos[goid]
        return go['name'] + ('' if go['active'] else '[off]')

    def path(self, goid):
        parts, tid = [], self.tr_of_go.get(goid)
        while tid:
            t = self.trs[tid]; parts.append(self.label(t['go'])); tid = t['father']
        return '/'.join(reversed(parts))

    def roots(self):
        return [t['go'] for t in self.trs.values() if not t['father']]

    def children(self, goid):
        return [self.trs[c]['go'] for c in self.trs[self.tr_of_go[goid]]['children']]

    def by_path(self, path):
        level, want = self.roots(), path.strip('/').split('/')
        node = None
        for name in want:
            name = name[:-5] if name.endswith('[off]') else name  # accept paths as printed
            node = next((g for g in level if self.gos[g]['name'] == name), None)
            if node is None:
                sys.exit('no such object: ' + path)
            level = self.children(node)
        return node

    def references(self, mb):
        """PPtrs to GameObjects/Transforms in this scene found in a MonoBehaviour's fields.

        No type tree, so field names are unknown: scan the body at 4-byte offsets for
        (fileID 0, pathID) pairs that hit a known object. Offsets are body-relative; match
        them against the field order in the decompiled class. Collisions are possible but rare.
        """
        s, e = mb['body']; d = self.f.data; out = []
        for p in range(s, e - 11, 4):
            fid, pid = struct.unpack_from('<iq', d, p)
            if fid == 0 and pid in self.gos:
                out.append((p - s, pid))
            elif fid == 0 and pid in self.trs:
                out.append((p - s, self.trs[pid]['go']))
        return out


def find_data_dir():
    here = os.path.dirname(os.path.abspath(__file__))
    while True:
        cand = os.path.join(here, 'Isolated Inhale_Data')
        if os.path.isfile(os.path.join(cand, 'level1')):
            return cand
        up = os.path.dirname(here)
        if up == here:
            return None
        here = up


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--data', help='the game\'s "Isolated Inhale_Data" folder')
    sub = ap.add_subparsers(dest='cmd', required=True)
    t = sub.add_parser('tree'); t.add_argument('--root'); t.add_argument('--depth', type=int, default=99)
    t.add_argument('--scripts', action='store_true', help='list MonoBehaviours on each object')
    sub.add_parser('find').add_argument('name')
    sub.add_parser('script').add_argument('cls')
    sub.add_parser('refs').add_argument('path')
    a = ap.parse_args()

    data = a.data or find_data_dir()
    if not data:
        sys.exit('game data folder not found; pass --data "<game>/Isolated Inhale_Data"')
    sc = Scene(data)

    if a.cmd == 'tree':
        def walk(g, depth):
            extra = ''
            if a.scripts:
                names = [n for _, n in sc.scripts_on(g)]
                extra = '  <' + ', '.join(names) + '>' if names else ''
            print('  ' * depth + sc.label(g) + extra)
            if depth < a.depth:
                for c in sc.children(g):
                    walk(c, depth + 1)
        for g in ([sc.by_path(a.root)] if a.root else sorted(sc.roots(), key=lambda g: sc.gos[g]['name'])):
            walk(g, 0)
    elif a.cmd == 'find':
        needle = a.name.lower()
        for p in sorted(sc.path(g) for g, go in sc.gos.items() if needle in go['name'].lower()):
            print(p)
    elif a.cmd == 'script':
        hits = sorted(sc.path(m['go']) for m in sc.mbs.values()
                      if sc.script_name(m).split('.')[-1] == a.cls.split('.')[-1])
        print('\n'.join(hits) if hits else 'no MonoBehaviour of class ' + a.cls)
    elif a.cmd == 'refs':
        g = sc.by_path(a.path)
        for mpid, name in sc.scripts_on(g):
            print(name)
            for off, target in sc.references(sc.mbs[mpid]):
                print('  +0x%03x -> %s' % (off, sc.path(target)))


if __name__ == '__main__':
    main()
