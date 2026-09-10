"""viewgraph.py -- the VIEW GRAPH: frame plausibility, graded alignment, axis solving, section
labels. Pure functions over already-extracted boxes/sizes/notes, so the offline contract gate can
run them with no ezdxf (same split as contour/pairing/lowering vs dxf_read).

Everything here follows the reverse path's one discipline: report a relation only when the
geometry FORCES it (cardinality 1), refuse it otherwise. Nothing in this module names a view
"front" or "top" -- axes come out anonymous (ax0/ax1/ax2) and the sign/naming stays with the
projection convention (config) and the model (recipe R4).

Why it exists (each function paid for by a real failure):
  * frame_plausible  -- f-3 has no border, and "the biggest cluster is the frame" swallowed its
    FRONT VIEW whole: 152 lines + 8 arcs (the very fillets R3/R4/R6 dimension) became "frame",
    7 dimensions went view-less, alignment came out empty and the cut line vanished. A frame must
    prove itself by CONTAINING the rest of the drawing; a borderless sheet has NO frame.
  * graded alignment -- s-7 stacks three views of one beam, but the middle one's bevelled ends
    make its silhouette ~6 mm longer on BOTH sides: the full-span test loses a true pair that the
    midpoints confirm to 0.27 mm. Span stays the strong grade; mid is the weaker, still
    deterministic one.
  * solve_view_graph -- with the pairs known, the {view axis -> part axis} assignment is a small
    constraint system. When it has exactly one solution it is a FACT (f-3: 100/57.9/40) and the
    model stops re-deriving it every turn; when it does not (a cube, an unfolded flat pattern in
    the stack), it is refused with the reason, never guessed.
  * pair_sections    -- "SECTION A-A" / "A-A 1 : 1" captions tie a section view to the view
    carrying its cut line even where box math cannot (a cross-scale section never shares a span).
"""
from __future__ import annotations

import re

ALIGN_EPS_MM = 0.5        # paper mm -- span edges / midpoints closer than this coincide
AXIS_MERGE_EPS_MM = 0.5   # TRUE mm -- two unpaired axis extents closer than this may be one axis

# A section caption: a letter pair "A-A", either opening the text ("A-A 1 : 1") or accompanied by
# a section word. A bare "A-A" buried in prose is neither.
_LETTER_PAIR_RE = re.compile(r"\b([A-Z])\s*-\s*\1\b")
_SECTION_WORDS = ("SECTION", "KESIT", "KESİT", "SCHNITT", "COUPE")


# --------------------------------------------------------------------------- frame plausibility
def frame_plausible(box, other_boxes, gap):
    """Is `box` (the biggest cluster's bbox) really the sheet frame?

    A border/title-block cluster encloses the whole drawing by construction, so the test is
    CONTAINMENT: every other cluster must lie inside it (grown by the cluster gap). A candidate
    with nothing else on the sheet proves nothing -- a borderless single-view drawing must not
    have its only view eaten -- so an empty `other_boxes` is a refusal too."""
    if not other_boxes:
        return False
    x0, y0, x1, y1 = box[0] - gap, box[1] - gap, box[2] + gap, box[3] + gap
    return all(b[0] >= x0 and b[1] >= y0 and b[2] <= x1 and b[3] <= y1 for b in other_boxes)


# --------------------------------------------------------------------------- graded alignment
def compute_alignment(views, eps=ALIGN_EPS_MM):
    """[{vid, geom_box}] -> [{a, b, shares, grade}] for every projection pair.

    grade 'span': both edges of one paper axis coincide within eps -- the strongest evidence,
        and exactly the pre-0.4.0 rule.
    grade 'mid': the midpoints of one paper axis coincide within eps AND the views are disjoint
        along the other axis (genuinely stacked / side-by-side). Weaker -- silhouette ends may
        genuinely differ (s-7's bevelled beam) -- but still a measurement, not a guess.
    Both grades of one pair never appear together (mid is only tried where span failed)."""
    out = []
    for i in range(len(views)):
        for j in range(i + 1, len(views)):
            a, b = views[i]["geom_box"], views[j]["geom_box"]
            entry = {"a": views[i]["vid"], "b": views[j]["vid"]}
            if abs(a[0] - b[0]) < eps and abs(a[2] - b[2]) < eps:
                entry.update(shares="x", grade="span")
            elif abs(a[1] - b[1]) < eps and abs(a[3] - b[3]) < eps:
                entry.update(shares="y", grade="span")
            elif (abs((a[0] + a[2]) - (b[0] + b[2])) / 2.0 < eps
                  and (a[3] < b[1] or b[3] < a[1])):
                entry.update(shares="x", grade="mid")
            elif (abs((a[1] + a[3]) - (b[1] + b[3])) / 2.0 < eps
                  and (a[2] < b[0] or b[2] < a[0])):
                entry.update(shares="y", grade="mid")
            else:
                continue
            out.append(entry)
    return out


# --------------------------------------------------------------------------- the axis solver
def solve_view_graph(views, pairs, eps=AXIS_MERGE_EPS_MM):
    """Assign each view's two paper axes to the part's three axes -- or refuse, with the reason.

    views: [{vid, size: [w, h]}] -- role 'view' at SHEET scale only (a detail/section at its own
        scale re-shows existing geometry and is excluded by the caller).
    pairs: alignment entries of grade span|mid (label pairs carry no axis claim).

    Mechanics: each view contributes two slots (vid,h) and (vid,v). A pair sharing paper-x unions
    the two h slots; sharing paper-y, the two v slots. The equivalence classes are part-axis
    candidates; a valid solution has exactly THREE, and no view's h and v in the same class.
    While there are more than three, the only legal move is merging two classes with matching
    observed extents (within eps, TRUE mm) that share no view -- and only where no class is
    CONTESTED: independent forced merges all apply at once (f-1's four views leave two, 100~100
    and 40~40), but a class claimed by two different merges means nothing is forced (a cube) and
    the whole solve is refused as ambiguous.

    -> {"solved": True, "views": {vid: [h_ax, v_ax]}}
     | {"solved": False, "reason": "inconsistent" | "axes_ambiguous" | "axes_unmerged"}"""
    slots = [(v["vid"], ax) for v in views for ax in ("h", "v")]
    if not slots:
        return {"solved": False, "reason": "axes_unmerged"}
    parent = {s: s for s in slots}

    def find(s):
        while parent[s] != s:
            parent[s] = parent[parent[s]]
            s = parent[s]
        return s

    def union(s1, s2):
        r1, r2 = find(s1), find(s2)
        if r1 != r2:
            parent[r1] = r2

    known = {v["vid"] for v in views}
    for p in pairs:
        if p.get("grade") not in ("span", "mid") or p["a"] not in known or p["b"] not in known:
            continue
        ax = "h" if p["shares"] == "x" else "v"
        union((p["a"], ax), (p["b"], ax))

    size = {(v["vid"], "h"): float(v["size"][0]) for v in views}
    size.update({(v["vid"], "v"): float(v["size"][1]) for v in views})

    def classes():
        cl = {}
        for s in slots:
            cl.setdefault(find(s), []).append(s)
        return list(cl.values())

    def views_of(members):
        return {vid for vid, _ax in members}

    def extent(members):
        return max(size[s] for s in members)

    for v in views:                                   # a pair chain must not fold h onto v
        if find((v["vid"], "h")) == find((v["vid"], "v")):
            return {"solved": False, "reason": "inconsistent"}

    cl = classes()
    while len(cl) > 3:
        candidates = []
        for i in range(len(cl)):
            for j in range(i + 1, len(cl)):
                if views_of(cl[i]) & views_of(cl[j]):
                    continue                          # would fold one view's h onto its v
                if abs(extent(cl[i]) - extent(cl[j])) <= eps:
                    candidates.append((i, j))
        if not candidates:
            return {"solved": False, "reason": "axes_unmerged"}
        involved = [k for pair in candidates for k in pair]
        if len(set(involved)) != len(involved):   # one class claimed twice -> nothing is forced
            return {"solved": False, "reason": "axes_ambiguous"}
        for i, j in candidates:
            union(cl[i][0], cl[j][0])
        cl = classes()
    if len(cl) < 3:                                   # fewer than three axes ever observed --
        return {"solved": False, "reason": "axes_unmerged"}   # two views can only show three

    # Deterministic axis names: descending extent, ties by the lexically first member slot. The
    # extents themselves are NOT emitted: each view's `size` already carries them as-drawn, and a
    # borderless sheet's recovered view may have furniture in its measured size (see geom_bbox) --
    # the solved TOPOLOGY is robust to that (the apparatus is symmetric, so midpoint pairs hold),
    # a re-published number would not be.
    ordered = sorted(cl, key=lambda m: (-extent(m), sorted(m)[0]))
    name = {}
    for k, members in enumerate(ordered):
        for s in members:
            name[s] = "ax%d" % k
    return {
        "solved": True,
        "views": {v["vid"]: [name[(v["vid"], "h")], name[(v["vid"], "v")]] for v in views},
    }


# --------------------------------------------------------------------------- section labels
def pair_sections(views, notes):
    """Tie each section view to the view carrying its cut line, by LABEL.

    views: [{vid, role, has_cut_line}] -- role 'view' only is considered.
    notes: [{text, view}] -- free text with its owning view (which_view), as read() built them.

    A caption ("SECTION A-A", "A-A 1 : 1") names the section view it sits beside; the parent is
    the view that carries cut_line primitives, disambiguated by the bare-letter notes ("A") at the
    cut line's ends when more than one view has a cut. Emitted only when both ends resolve to
    exactly one view: {a: parent, b: section, shares: None, grade: 'label', label: 'A-A'}.
    shares is None on purpose -- a label pair asserts the section RELATION (it survives a
    cross-scale section, which never shares a span); the axis reading is R7's job."""
    real = [v for v in views if v.get("role") == "view"]
    by_vid = {v["vid"]: v for v in real}
    out = []
    for n in notes:
        text = (n.get("text") or "").strip()
        m = _LETTER_PAIR_RE.search(text)
        if not m:
            continue
        up = text.upper()
        if m.start() != 0 and not any(w in up for w in _SECTION_WORDS):
            continue
        letter = m.group(1)
        section_vid = n.get("view")
        if section_vid not in by_vid:
            continue
        parents = [v["vid"] for v in real if v.get("has_cut_line") and v["vid"] != section_vid]
        if len(parents) > 1:
            lettered = {nn.get("view") for nn in notes if (nn.get("text") or "").strip() == letter}
            parents = [p for p in parents if p in lettered]
        if len(parents) != 1:
            continue
        pair = {"a": parents[0], "b": section_vid, "shares": None,
                "grade": "label", "label": "%s-%s" % (letter, letter)}
        if pair not in out:
            out.append(pair)
    return out
