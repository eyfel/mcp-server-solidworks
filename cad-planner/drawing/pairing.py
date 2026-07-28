"""pairing.py -- associate flat-pattern bend ANNOTATIONS with the lines they annotate.

Split out of dxf_read so the whole post-extraction pipeline (chaining -> pairing -> gate ->
IR) is testable with NO ezdxf dependency: the offline gate runs on frozen `draw`-dialect
fixtures on any machine, CI included. dxf_read stays the only module that touches DXF.
"""
from __future__ import annotations

import math

try:
    from . import contour
    from .vocab import UNPAIRED_REASONS
except ImportError:
    import contour
    from vocab import UNPAIRED_REASONS


def _reason(name):
    """Guard: an unpaired reason must exist in the published vocabulary, or the schema is lying."""
    assert name in UNPAIRED_REASONS, "unpaired reason %r is not in vocab.UNPAIRED_REASONS" % name
    return name


def pair_bend_notes(bends, out_views, sheet_scale, cfg):
    """Pair each UP/DOWN annotation with the bend line it annotates.

    Three independent channels, none of them a guess:
      1. EDGE CLASS must match the direction (`bend_class_map`) -- SolidWorks draws a DOWN bend line
         hidden and an UP one visible, because the flat pattern is viewed from one side.
      2. The note sits ABOVE its line, along the note's own local up axis (its DXF rotation), so
         candidates must be PARALLEL to the note's baseline and on the negative side of that axis.
      3. Nearest wins, and a line may annotate only ONE bend (1:1).

    Verified 6/6 on s-1 + s-2. The predecessor rule -- "nearest parallel line", class ignored --
    mis-paired s-1's fourth note to the blank's top OUTLINE edge, which then contradicted recipe
    R10's own class corroboration. Channels 1 and 2 each fix that case independently.

    If more than one candidate survives at (near) equal distance, the note is left UNPAIRED with a
    reason. Guessing here is not cheap to undo: a bend line cannot be nudged afterwards (no
    sketch-entity move/delete tool exists), and a wrong fold is invisible to volume, area and
    topology alike.
    """
    sm = cfg["sheet_metal"]
    class_map = sm.get("bend_class_map") or {}
    note_above = sm.get("note_above_bend_line", True)
    max_off = sm.get("bend_note_max_offset_mm", 25.0)
    eps = cfg["tolerance"].get("chain_eps_mm", 0.01)
    vmap = {v["vid"]: v for v in out_views}
    in_loop = {v["vid"]: {idx for code, idx in contour.loop_member_segments(v) if code == "l"}
               for v in out_views}

    pairs = []                                     # (offset_paper_mm, note_index, line_index)
    for bi, b in enumerate(bends):
        v = vmap.get(b["view"])
        if v is None:
            continue
        want = class_map.get(b["dir"])
        rot = math.radians(b.get("rot", 0.0) or 0.0)
        upx, upy = -math.sin(rot), math.cos(rot)   # the note's local UP in paper space
        px, py = b["at"]
        x0, y0 = v["paper_box"][0], v["paper_box"][1]
        for li, ln in enumerate(v["geometry"]["lines"]):
            if want and ln["c"] != want:
                continue
            dx, dy = ln["x2"] - ln["x1"], ln["y2"] - ln["y1"]
            length = math.hypot(dx, dy)
            if length < 1e-9 or abs((dx * upx + dy * upy) / length) > 1e-3:
                continue                            # not parallel to the note's baseline
            t = ((x0 + ln["x1"] / sheet_scale) - px) * upx + \
                ((y0 + ln["y1"] / sheet_scale) - py) * upy
            if note_above and t > -1e-9:
                continue                            # the line must be BELOW the note
            if abs(t) > max_off:
                continue
            pairs.append((abs(t), bi, li))

    order = sorted(pairs)
    assigned, used_lines = set(), set()
    for d, bi, li in order:
        if bi in assigned or (bends[bi]["view"], li) in used_lines:
            continue
        rivals = [ll for dd, bb, ll in order
                  if bb == bi and ll != li and (bends[bi]["view"], ll) not in used_lines
                  and abs(dd - d) <= eps]
        assigned.add(bi)
        if rivals:
            bends[bi]["unpaired"] = {"reason": _reason("ambiguous"),
                                     "candidates": [["l", li]] + [["l", r] for r in rivals[:3]]}
            continue
        used_lines.add((bends[bi]["view"], li))
        ln = vmap[bends[bi]["view"]]["geometry"]["lines"][li]
        bends[bi]["bend_line"] = {
            "seg": ["l", li], "class": ln["c"], "note_offset": round(d * sheet_scale, 3),
            # A bend line belongs to NO closed contour. in_loop=true means the class+side filter
            # landed on an outline edge -- suspicious, and the thing to watch (logs.md KNOWN RISKS).
            "in_loop": li in in_loop.get(bends[bi]["view"], set()),
        }
    for bi, b in enumerate(bends):
        if bi not in assigned:
            b["unpaired"] = {"reason": _reason("no_candidate"), "candidates": []}
