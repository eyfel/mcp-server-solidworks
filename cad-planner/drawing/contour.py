"""contour.py -- Tier A contour chaining for the `draw` dialect.

Orders a view's UNORDERED primitives into closed loops (outer + inner) and open chains. This is the
prerequisite for transcribing an arbitrary outline: before it, every contour had to be chained by
hand, so only rectangular blanks were ever proven.

DESIGN: strict degree-2 endpoint chaining, per edge class, and NOTHING ELSE. Where two primitives
meet unambiguously the chain continues; at a junction of three or more it STOPS and the fragments
are reported as open chains. It never guesses a continuation, because a wrong contour builds VALID
geometry with no error (the R14 failure class).

  What this DOES solve, measured on the samples: sheet-metal FLAT PATTERNS, completely. s-1 and s-2
  yield their outer blank loop + every cutout, with the bend lines falling out as open chains for
  free (a bend line's endpoints land mid-edge on the outline, never on a vertex -- so it can never
  join the outline loop).

  What this does NOT solve: general ORTHO views. A feature silhouette ending mid-edge on the outline
  is a T-junction (degree 3), and f-2's front view closes only 1 loop out of 15 visible primitives.
  Fixing that needs a planar arrangement -- split every segment at interior touch points, then walk
  faces by leftmost turn. That is Tier B, deliberately a separate job (see logs.md ADR-064).

Output is INDEX REFERENCES into the view's own primitive arrays, never duplicated geometry: payload
cost is a first-class constraint here.
"""
from __future__ import annotations

import math

try:                             # normal: imported as part of the `drawing` package
    from . import curvefit
except ImportError:              # fallback: imported flat (the offline gate, scripts)
    import curvefit

# Closed vocabularies (LOOP_ROLES / SEQ_CODES / PRIMITIVE_KINDS) live in vocab.py, which the
# contract test diffs against draw-dialect.schema.json.
#
# Since 0.5.0 a view may carry `ellipses` (code "e"): a FULL ellipse is a closed loop of one entity
# exactly like a circle; an ellipse ARC carries endpoints (x1..y2) and chains like an arc, its
# bulge measured on the PARAMETRIC sweep (curvefit.segment_area).


# --------------------------------------------------------------------------- geometry helpers
def _key(p, eps):
    return (round(p[0] / eps), round(p[1] / eps))


def _arc_ends(a):
    """Endpoints of an emitted arc. dxf_read emits them explicitly (0.2.0+); fall back to the
    angles for any artifact written before that."""
    if "x1" in a:
        return (a["x1"], a["y1"]), (a["x2"], a["y2"])
    r1, r2 = math.radians(a["a1"]), math.radians(a["a2"])
    return ((a["cx"] + a["r"] * math.cos(r1), a["cy"] + a["r"] * math.sin(r1)),
            (a["cx"] + a["r"] * math.cos(r2), a["cy"] + a["r"] * math.sin(r2)))


def _segment_area(r, theta):
    """Area between a chord and its arc, for an included angle theta (radians)."""
    return 0.5 * r * r * (theta - math.sin(theta))


def _sweep(cx, cy, p_from, p_to, ccw):
    """Included angle of an arc traversed from p_from to p_to about (cx, cy)."""
    a0 = math.atan2(p_from[1] - cy, p_from[0] - cx)
    a1 = math.atan2(p_to[1] - cy, p_to[0] - cx)
    d = (a1 - a0) if ccw else (a0 - a1)
    d %= 2.0 * math.pi
    return d if d > 1e-12 else 2.0 * math.pi


def point_in_polygon(pt, poly):
    """Standard ray cast. Loops in a drawing are disjoint or strictly nested, so testing ONE vertex
    of the candidate child is enough -- it can never sit on the parent's boundary."""
    x, y = pt
    inside = False
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % n]
        if (y0 > y) != (y1 > y):
            xx = x0 + (y - y0) * (x1 - x0) / (y1 - y0)
            if xx > x:
                inside = not inside
    return inside


# --------------------------------------------------------------------------- the chainer
def chain_view(view, eps_mm=0.01):
    """-> {"loops": [...], "open_chains": [...]}, referencing view['geometry'] by index.

    A loop entry: {id, class, role, parent, area, bbox, seq} where seq is [[code, index, dir], ...]
    and dir is +1 when the primitive is traversed from its stored start to its stored end, -1 when
    reversed. `area` is the ABSOLUTE enclosed area in mm^2 (arc bulges included exactly).
    """
    g = view.get("geometry") or {}
    lines, arcs, circles = g.get("lines", []), g.get("arcs", []), g.get("circles", [])
    ellipses = g.get("ellipses", [])

    # A CIRCLE is a closed loop of one entity by definition -- it never enters the walk.
    loops = []
    for i, c in enumerate(circles):
        r = c["d"] / 2.0
        loops.append({"class": c["c"], "area": math.pi * r * r,
                      "bbox": [c["cx"] - r, c["cy"] - r, c["cx"] + r, c["cy"] + r],
                      "poly": [(c["cx"] + r, c["cy"]), (c["cx"], c["cy"] + r),
                               (c["cx"] - r, c["cy"]), (c["cx"], c["cy"] - r)],
                      "probe": (c["cx"], c["cy"]), "seq": [["c", i, 1]]})
    # ... and so is a FULL ellipse (no t1): area pi*rx*ry exactly, extremes as its chord polygon.
    for i, e in enumerate(ellipses):
        if "t1" in e:
            continue
        el = curvefit.record_ellipse(e)
        loops.append({"class": e["c"], "area": math.pi * e["rx"] * e["ry"],
                      "bbox": list(curvefit.ellipse_bbox(el)),
                      "poly": [curvefit.point_at(el, t) for t in (0.0, 90.0, 180.0, 270.0)],
                      "probe": (e["cx"], e["cy"]), "seq": [["e", i, 1]]})

    segs = []   # (code, index, class, p_start, p_end, payload)
    for i, l in enumerate(lines):
        segs.append(("l", i, l["c"], (l["x1"], l["y1"]), (l["x2"], l["y2"]), l))
    for i, a in enumerate(arcs):
        s, e = _arc_ends(a)
        segs.append(("a", i, a["c"], s, e, a))
    for i, e in enumerate(ellipses):
        if "t1" in e:
            segs.append(("e", i, e["c"], (e["x1"], e["y1"]), (e["x2"], e["y2"]), e))

    open_chains = []
    for cls in sorted({s[2] for s in segs}):
        sub = [s for s in segs if s[2] == cls]
        inc = {}
        for si, s in enumerate(sub):
            inc.setdefault(_key(s[3], eps_mm), []).append(si)
            inc.setdefault(_key(s[4], eps_mm), []).append(si)

        used = set()
        for seed in range(len(sub)):
            if seed in used:
                continue
            used.add(seed)
            # chain = [(seg_index, direction)], traversed head -> tail
            chain = [(seed, 1)]
            head, tail = sub[seed][3], sub[seed][4]

            def _grow(at, backwards):
                """Extend from point `at`; returns the new free endpoint (or None when stuck).

                The degree is counted over EVERY segment incident at the point, used or not:
                a T-junction is a junction no matter which of its branches some earlier chain
                already consumed. Counting only the unused ones made the result depend on the
                seed ORDER -- f-3's front outline "closed" through a boss-foot T only because
                the outline's own continuation had been eaten by another chain first, and the
                same drawing chained differently once the array indices shifted (0.5.0)."""
                here = inc.get(_key(at, eps_mm), [])
                if len(here) != 2:           # 1 = dead end, >2 = junction: never guess
                    return None
                cands = [si for si in here if si not in used]
                if len(cands) != 1:
                    return None
                si = cands[0]
                used.add(si)
                s = sub[si]
                forward = _key(s[3], eps_mm) == _key(at, eps_mm)
                nxt = s[4] if forward else s[3]
                d = 1 if forward else -1
                if backwards:
                    chain.insert(0, (si, -d))
                else:
                    chain.append((si, d))
                return nxt

            closed = False
            while True:
                # len>1 for a real chain; a single ARC (or ellipse arc) with coincident ends is a
                # full turn.
                if _key(tail, eps_mm) == _key(head, eps_mm) and (len(chain) > 1
                                                                 or sub[seed][0] in ("a", "e")):
                    closed = True
                    break
                nxt = _grow(tail, backwards=False)
                if nxt is None:
                    break
                tail = nxt
            if not closed:
                while True:
                    nxt = _grow(head, backwards=True)
                    if nxt is None:
                        break
                    head = nxt
                    if _key(head, eps_mm) == _key(tail, eps_mm):
                        closed = True
                        break

            seq = [[sub[si][0], sub[si][1], d] for si, d in chain]
            if closed:
                loops.append(_measure(chain, sub, seq, cls))
            else:
                open_chains.append({"class": cls, "seq": seq})

    # --- nesting: a loop's parent is the SMALLEST loop that contains it (class-independent) ------
    loops.sort(key=lambda lp: -lp["area"])
    for i, lp in enumerate(loops):
        lp["id"] = "L%d" % i
    for lp in loops:
        parent = None
        for cand in loops:
            if cand is lp or cand["area"] <= lp["area"]:
                continue
            if point_in_polygon(lp["probe"], cand["poly"]):
                if parent is None or cand["area"] < parent["area"]:
                    parent = cand
        lp["parent"] = parent["id"] if parent else None
        lp["role"] = "inner" if parent else "outer"

    out_loops = [{"id": lp["id"], "class": lp["class"], "role": lp["role"], "parent": lp["parent"],
                  "area": round(lp["area"], 4),
                  "bbox": [round(v, 4) for v in lp["bbox"]], "seq": lp["seq"]}
                 for lp in loops]
    for i, oc in enumerate(open_chains):
        oc["id"] = "O%d" % i
    chained = {"loops": out_loops,
               "open_chains": [{"id": oc["id"], "class": oc["class"], "seq": oc["seq"]}
                               for oc in open_chains]}
    # TIER B (phase 1): where the visible graph is a CLOSED subdivision and Tier A returned no
    # outer loop, the boundary is determined and can be walked -- see `outer_face`.
    tier_b = outer_face(view, chained, eps_mm)
    if tier_b is not None:
        chained["loops"].append(tier_b)
    return chained


def _measure(chain, sub, seq, cls):
    """Signed area of a closed chain: shoelace over the chords + each arc's circular segment,
    signed by whether that arc is traversed CCW about its own centre. Exact, not an approximation."""
    verts, extra = [], 0.0
    for si, d in chain:
        code, _idx, _c, ps, pe, payload = sub[si]
        a, b = (ps, pe) if d == 1 else (pe, ps)
        verts.append(a)
        if code == "a":
            # `dir` is the arc's own stored sweep sense; flip it when traversed backwards.
            ccw = (payload.get("dir", 1) * d) > 0
            theta = _sweep(payload["cx"], payload["cy"], a, b, ccw)
            extra += (1 if ccw else -1) * _segment_area(payload["r"], theta)
        elif code == "e":
            ccw = (payload.get("dir", 1) * d) > 0
            el = curvefit.record_ellipse(payload)
            theta = curvefit.sweep_param(el, a, b, ccw)
            extra += (1 if ccw else -1) * curvefit.segment_area(el, theta)
    shoelace = 0.0
    for i in range(len(verts)):
        x0, y0 = verts[i]
        x1, y1 = verts[(i + 1) % len(verts)]
        shoelace += x0 * y1 - x1 * y0
    area = 0.5 * shoelace + extra
    xs = [v[0] for v in verts]
    ys = [v[1] for v in verts]
    return {"class": cls, "area": abs(area), "seq": seq,
            "bbox": [min(xs), min(ys), max(xs), max(ys)],
            "poly": verts, "probe": verts[0]}


def _leave_angle(entry, d):
    """The direction a half-edge LEAVES its start vertex, as an angle. Tangent, not chord.

    This is the whole correctness question for Tier B: the face walk picks the next edge by
    angular order at a junction, so an arc leaving on a chord bearing that differs from its real
    tangent can order wrongly and hand back a WRONG contour with no error anywhere -- the exact
    silent-failure class R17 exists to prevent. A line's tangent is its chord; an arc's is
    perpendicular to its radius, signed by the sweep sense; an ellipse arc's is sampled off its
    own parameterisation."""
    code, _idx, _c, ps, pe, payload = entry
    a, b = (ps, pe) if d == 1 else (pe, ps)
    if code == "l":
        return math.atan2(b[1] - a[1], b[0] - a[0])
    if code == "a":
        ccw = (payload.get("dir", 1) * d) > 0
        rad = math.atan2(a[1] - payload["cy"], a[0] - payload["cx"])
        return rad + (math.pi / 2.0 if ccw else -math.pi / 2.0)
    # ellipse arc: sample a short step along its own parameter, in the traversal sense
    el = curvefit.record_ellipse(payload)
    ccw = (payload.get("dir", 1) * d) > 0
    t0 = curvefit.param_of(a, el)
    nxt = curvefit.point_at(el, t0 + (0.5 if ccw else -0.5))
    return math.atan2(nxt[1] - a[1], nxt[0] - a[0])


def outer_face(view, chained, eps_mm=0.01):
    """TIER B, phase 1: recover a view's OUTER boundary by planar face traversal.

    Tier A stops at every junction of three or more, which is right -- it must never guess. But
    where a view's visible graph has NO FREE END, the figure is already a closed planar
    subdivision, and its faces are then DEFINED by the angular order of the edges at each vertex.
    "Take the next edge in angular order" is not a heuristic there; it is what a face IS. So this
    adds no guessing to the pipeline -- it reads a boundary that was fully determined all along
    and that Tier A simply declines to walk.

    Deliberately narrow (phase 1, decided with the user):
      * VISIBLE edges only -- the part's silhouette, never hidden/centre/cut-line furniture.
      * runs ONLY when no vertex has degree 1. One free end means a dangling edge, the
        subdivision is not closed, and the faces are no longer determined. Those views wait for
        phase 2.
      * runs ONLY when Tier A found no visible OUTER loop, so this can never duplicate or
        contradict a Tier A answer -- it only fills a hole where Tier A returned nothing.
    The outer boundary is the face whose SIGNED area is negative with the largest magnitude:
    bounded faces come out of this walk with one orientation and the unbounded one with the
    other. Verified against the value f-3's v0 outline was independently reported to have
    (3993.1327 mm^2, 8 primitives).

    Returns a loop dict tagged `tier: "B"`, or None.
    """
    if any(lp["class"] == "visible" and lp["role"] == "outer" for lp in chained["loops"]):
        return None
    g = view.get("geometry") or {}
    sub = []
    for i, l in enumerate(g.get("lines", [])):
        if l["c"] == "visible":
            sub.append(("l", i, l["c"], (l["x1"], l["y1"]), (l["x2"], l["y2"]), l))
    for i, a in enumerate(g.get("arcs", [])):
        if a["c"] == "visible":
            s, e = _arc_ends(a)
            sub.append(("a", i, a["c"], s, e, a))
    for i, e in enumerate(g.get("ellipses", [])):
        if e["c"] == "visible" and "t1" in e:
            sub.append(("e", i, e["c"], (e["x1"], e["y1"]), (e["x2"], e["y2"]), e))
    if len(sub) < 3:
        return None

    deg, out = {}, {}
    for si, s in enumerate(sub):
        for p in (s[3], s[4]):
            deg[_key(p, eps_mm)] = deg.get(_key(p, eps_mm), 0) + 1
    if any(v == 1 for v in deg.values()):      # a free end: not a closed subdivision
        return None
    for si, s in enumerate(sub):
        out.setdefault(_key(s[3], eps_mm), []).append((si, 1))
        out.setdefault(_key(s[4], eps_mm), []).append((si, -1))

    def end_key(h):
        si, d = h
        return _key(sub[si][4] if d == 1 else sub[si][3], eps_mm)

    seen, best = set(), None
    for seed in [(si, d) for si in range(len(sub)) for d in (1, -1)]:
        if seed in seen:
            continue
        face, h = [], seed
        while h not in seen:
            seen.add(h)
            face.append(h)
            at = end_key(h)
            # The direction pointing BACK down the edge just traversed. Reversing the half-edge
            # gives the tangent leaving the far vertex; back is simply that direction itself.
            back = _leave_angle(sub[h[0]], -h[1])
            cands = [c for c in out.get(at, []) if c != (h[0], -h[1])] or out.get(at, [])
            if not cands:
                face = []
                break
            h = max(cands, key=lambda c: (_leave_angle(sub[c[0]], c[1]) - back) % (2 * math.pi))
        if len(face) < 3:
            continue
        m = _measure(face, sub, [[sub[si][0], sub[si][1], d] for si, d in face], "visible")
        signed = _signed_area(face, sub)
        if signed < 0 and (best is None or m["area"] > best[0]["area"]):
            best = (m, face)
    if best is None:
        return None
    m = best[0]
    return {"id": "B0", "class": "visible", "role": "outer", "parent": None, "tier": "B",
            "area": round(m["area"], 4), "bbox": [round(v, 4) for v in m["bbox"]],
            "seq": m["seq"]}


def _signed_area(face, sub):
    """Shoelace over the traversal's chord polygon -- SIGN only (the exact magnitude, arc bulges
    included, comes from _measure). The unbounded face traverses opposite to the bounded ones."""
    s = 0.0
    for si, d in face:
        ps, pe = sub[si][3], sub[si][4]
        a, b = (ps, pe) if d == 1 else (pe, ps)
        s += a[0] * b[1] - b[0] * a[1]
    return s / 2.0


def loop_member_segments(chained):
    """The set of (code, index) pairs that belong to some CLOSED loop -- used by the bend-note
    pairing to flag a suspicious match (a bend line should never be part of a closed contour)."""
    return {(item[0], item[1]) for lp in chained["loops"] for item in lp["seq"]}
