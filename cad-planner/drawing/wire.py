"""wire.py -- the artifact's WIRE/DISK form, and the single boundary to it.

The reader works in a shape that is convenient for CODE: one list of dicts per primitive kind, and
a `seq` entry referencing it by POSITION. That shape is expensive to send: a line spends ~69 bytes
to say four numbers and an edge class, and a single-segment open chain spends ~55 to say "this one
primitive belongs to no contour."

Everything ABOVE this module (contour, curvefit, pairing, viewgraph, lowering) keeps the record
shape and is untouched by the compaction -- that is the whole point of putting the boundary here.
Everything a MODEL or a human reads is encoded: the analyze_drawing answer and the saved
`*.analysis-v*.json`. `decode` is the exact inverse, so an artifact read back off disk re-enters
the pipeline in the shape the pipeline expects.

Measured over the 10 sample artifacts (v0.6.0):
    lines               41,554 -> 21,009 B   (-49%)
    open chains         20,438 ->  7,112 B   (-65%)

WHY EACH LINE CARRIES ITS OWN INDEX.  A `seq` entry is ["l", 12, 1] -- code, INDEX, direction --
so grouping lines by edge class moves what "12" refers to. Pure class-grouping measures 5 points
better (-54%) but it makes the reader of the payload recompute a global index from the group
lengths, and this project's worst failure class is exactly the silent misread (R14's mirror, R17's
invented contour): an off-by-one there builds VALID geometry with no error anywhere. 171 bytes per
sample buys that arithmetic away -- every line states the index `seq` will use for it.

NOTHING IS ASSUMED.  A single-segment chain collapses only when the collapse is provably lossless
(direction 1, the id its position implies, and a class equal to the primitive's own). Any chain
that fails one of those tests stays a full record. Measured across the sample set all 302 singles
pass, but a future reader change must not lose data quietly to make that stay true.
"""
from __future__ import annotations

# The primitive kinds a seq code refers to, in the order `geometry` declares them.
_CODE_TO_KIND = {"l": "lines", "a": "arcs", "c": "circles", "e": "ellipses"}

_LINE_FIELDS = ("x1", "y1", "x2", "y2")


# --------------------------------------------------------------------------- lines
def _encode_lines(lines):
    """[{x1,y1,x2,y2,c}, ...] -> {class: [[index, x1, y1, x2, y2], ...]}, grouped, index-first."""
    out = {}
    for i, p in enumerate(lines):
        out.setdefault(p["c"], []).append([i] + [p[f] for f in _LINE_FIELDS])
    return out


def _decode_lines(enc):
    """The exact inverse. A gap or a duplicate in the indices is a corrupt payload, not a shrug."""
    flat = {}
    for cls, rows in enc.items():
        for row in rows:
            i = row[0]
            if i in flat:
                raise ValueError("wire: line index %d appears twice" % i)
            flat[i] = dict(zip(_LINE_FIELDS, row[1:]), c=cls)
    missing = [i for i in range(len(flat)) if i not in flat]
    if missing:
        raise ValueError("wire: line indices are not contiguous, missing %s" % missing[:5])
    return [flat[i] for i in range(len(flat))]


# --------------------------------------------------------------------------- open chains
def _collapsible(oc, pos, geometry):
    """True when {id, class, dir} are all recoverable, so dropping them loses nothing."""
    seq = oc.get("seq") or []
    if len(seq) != 1:
        return False
    code, idx, direction = seq[0]
    if direction != 1 or oc.get("id") != "O%d" % pos:
        return False
    arr = geometry.get(_CODE_TO_KIND.get(code) or "") or []
    if not 0 <= idx < len(arr):
        return False
    return arr[idx].get("c") == oc.get("class")


def _encode_chains(chains, geometry):
    """-> (open_chains kept in full, open_singles as [position, code, index] triples)."""
    kept, singles = [], []
    for pos, oc in enumerate(chains):
        if _collapsible(oc, pos, geometry):
            code, idx, _d = oc["seq"][0]
            singles.append([pos, code, idx])
        else:
            kept.append(oc)
    return kept, singles


def _decode_chains(kept, singles, geometry):
    """Rebuild the original list, in the original order, with ids and classes restored.

    Every failure here is a ValueError on purpose: the adapter treats that as a stale/corrupt cache
    and re-reads the file, whereas an IndexError or a bare StopIteration escaping this function
    would take the tool down with it.
    """
    total = len(kept) + len(singles)
    by_pos = {}
    for entry in singles:
        if len(entry) != 3:
            raise ValueError("wire: open_singles entry is not [position, code, index]: %r" % (entry,))
        pos, code, idx = entry
        if not isinstance(pos, int) or not 0 <= pos < total or pos in by_pos:
            raise ValueError("wire: open_singles position %r is out of range or repeated" % (pos,))
        arr = geometry.get(_CODE_TO_KIND.get(code) or "") or []
        if not 0 <= idx < len(arr):
            raise ValueError("wire: open_singles points at missing %s[%d]" % (code, idx))
        by_pos[pos] = {"id": "O%d" % pos, "class": arr[idx]["c"], "seq": [[code, idx, 1]]}
    out, it = [], iter(kept)
    for pos in range(total):
        if pos in by_pos:
            out.append(by_pos[pos])
        else:
            out.append(next(it))
    return out


# --------------------------------------------------------------------------- artifact level
def encode(art):
    """A NEW artifact in wire form. The input is not mutated -- callers still hold the record form.

    Only `views[].geometry.lines` and `views[].open_chains` change; every other key is passed
    through by reference, so this is cheap on the big arrays it does not touch.
    """
    if not isinstance(art, dict) or "views" not in art:
        return art
    out = dict(art)
    views = []
    for v in art.get("views") or []:
        vv = dict(v)
        g = v.get("geometry")
        if isinstance(g, dict) and isinstance(g.get("lines"), list):
            gg = dict(g)
            gg["lines"] = _encode_lines(g["lines"])
            vv["geometry"] = gg
        chains = v.get("open_chains")
        if isinstance(chains, list):
            kept, singles = _encode_chains(chains, v.get("geometry") or {})
            vv["open_chains"] = kept
            if singles:
                vv["open_singles"] = singles
        views.append(vv)
    out["views"] = views
    return out


def decode(art):
    """The inverse of `encode`. Already-decoded input passes through unchanged (idempotent)."""
    if not isinstance(art, dict) or "views" not in art:
        return art
    out = dict(art)
    views = []
    for v in art.get("views") or []:
        vv = dict(v)
        g = v.get("geometry")
        if isinstance(g, dict) and isinstance(g.get("lines"), dict):
            gg = dict(g)
            gg["lines"] = _decode_lines(g["lines"])
            vv["geometry"] = gg
        singles = vv.pop("open_singles", None)
        if singles:
            vv["open_chains"] = _decode_chains(vv.get("open_chains") or [], singles,
                                               vv.get("geometry") or {})
        views.append(vv)
    out["views"] = views
    return out
