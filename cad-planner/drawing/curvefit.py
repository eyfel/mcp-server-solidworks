"""curvefit.py -- exploded curve fans -> PARAMETRIC ellipse primitives (pure Python, no numpy).

Why it exists: the SolidWorks DXF export DESTROYS the parametric form of every curve that is not a
circle or a circular arc. A hole on a tilted plane projects as an ellipse, and the exporter writes
that ellipse as 48 short LINE segments. On f-3 (2026-09-03) seven such fans were 236 primitives --
49% of a 39 KB payload -- carrying exactly five numbers each. Worse than the weight: the model was
handed a polygon and asked to recognise the circle it came from.

The house discipline applies unchanged -- "the fitter PROPOSES, the RESIDUAL decides" (ADR-053's
source + residual idiom): a run of short segments is replaced by ONE ellipse (or elliptical arc)
only when the whole polyline -- its vertices AND its chord midpoints -- lies within `fit_eps` of
the fitted curve. The midpoint (sag) test is what keeps a hexagon a hexagon: its vertices sit on a
circle exactly, its chords do not. Everything the fit did not accept stays RAW (lossless).

Measured on the samples (2026-09-03): the exporter's tessellation sag is 0.011 paper mm on f-3
and 0.019 on s-6 -- it depends on the source document's image-quality setting, not on a constant
-- so `fit_eps` is a PAPER-mm number scaled by the view's own scale. A 4-segment run fits the WRONG
ellipse within that sag (4.916 for a true 5.000), which is why `min_segments` exists.

Emitted record (view-local TRUE mm, mirrors the dialect's arc record):
  full ellipse:  {cx, cy, rx, ry, rot, c}                       rx >= ry; rx lies along `rot`
  ellipse arc:   {... , t1, t2, x1, y1, x2, y2, dir: 1}         CCW from t1 to t2 (parametric deg)
  a FITTED one adds {n, fit}: source segment count + max deviation (mm). A record WITHOUT them is a
  real DXF ELLIPSE entity. x1..y2 are the run's OWN end vertices (exact, so chaining still meets
  the neighbours); point_at(t1) reproduces them within `fit`.
"""
from __future__ import annotations

import math

DEFAULTS = {"fit_eps_paper_mm": 0.03, "min_segments": 8, "max_turn_deg": 40.0,
            "min_sweep_deg": 90.0}
_MIN_TURN_DEG = 0.05      # below this two chords are collinear -- a split line, not a curve


# --------------------------------------------------------------------------- ellipse geometry
def _to_frame(p, el):
    cx, cy, _a, _b, rot = el
    r = math.radians(rot)
    dx, dy = p[0] - cx, p[1] - cy
    return (dx * math.cos(r) + dy * math.sin(r), -dx * math.sin(r) + dy * math.cos(r))


def point_at(el, t_deg):
    """The ellipse point at parametric angle t (degrees, CCW from the rx axis)."""
    cx, cy, a, b, rot = el
    t, r = math.radians(t_deg), math.radians(rot)
    u, v = a * math.cos(t), b * math.sin(t)
    return (cx + u * math.cos(r) - v * math.sin(r), cy + u * math.sin(r) + v * math.cos(r))


def param_of(p, el):
    """Parametric angle (radians, [0, 2pi)) of the ellipse point nearest to p -- Newton on the
    frame angle from the obvious start, which converges in a few steps for a point near the curve
    (the only case that matters: a rejected point is rejected either way)."""
    a, b = el[2], el[3]
    u, v = _to_frame(p, el)
    t = math.atan2(v / b, u / a)
    for _ in range(12):
        f = (b * b - a * a) * math.sin(t) * math.cos(t) + a * u * math.sin(t) - b * v * math.cos(t)
        fp = (b * b - a * a) * math.cos(2 * t) + a * u * math.cos(t) + b * v * math.sin(t)
        if abs(fp) < 1e-15:
            break
        dt = f / fp
        t -= dt
        if abs(dt) < 1e-12:
            break
    return t % (2.0 * math.pi)


def distance(p, el):
    a, b = el[2], el[3]
    u, v = _to_frame(p, el)
    t = param_of(p, el)
    return math.hypot(a * math.cos(t) - u, b * math.sin(t) - v)


def deviation(pts, el):
    """Max distance of the POLYLINE from the ellipse: every vertex and every chord midpoint."""
    worst = 0.0
    for k, p in enumerate(pts):
        worst = max(worst, distance(p, el))
        if k + 1 < len(pts):
            m = ((p[0] + pts[k + 1][0]) / 2.0, (p[1] + pts[k + 1][1]) / 2.0)
            worst = max(worst, distance(m, el))
    return worst


def sweep_param(el, p_from, p_to, ccw):
    """PARAMETRIC angle swept from p_from to p_to about the ellipse, in (0, 2pi]."""
    t0, t1 = param_of(p_from, el), param_of(p_to, el)
    d = (t1 - t0) if ccw else (t0 - t1)
    d %= 2.0 * math.pi
    return d if d > 1e-12 else 2.0 * math.pi


def segment_area(el, theta):
    """Area between a chord and its elliptical arc for a PARAMETRIC sweep theta. The circular
    formula generalises exactly: sector = ab*theta/2, triangle = ab*sin(theta)/2."""
    return 0.5 * el[2] * el[3] * (theta - math.sin(theta))


def ellipse_bbox(el, t1_deg=None, t2_deg=None):
    """True extent of a full ellipse, or of the CCW arc t1 -> t2."""
    cx, cy, a, b, rot = el
    r = math.radians(rot)
    # parametric angles of the four axis-extreme points
    tx = math.atan2(-b * math.sin(r), a * math.cos(r))
    ty = math.atan2(b * math.cos(r), a * math.sin(r))
    cand = [math.degrees(t) % 360.0 for t in (tx, tx + math.pi, ty, ty + math.pi)]
    if t1_deg is None:
        pts = [point_at(el, t) for t in cand]
    else:
        sweep = (t2_deg - t1_deg) % 360.0 or 360.0
        pts = [point_at(el, t1_deg), point_at(el, t2_deg)]
        pts += [point_at(el, t) for t in cand if ((t - t1_deg) % 360.0) <= sweep]
    return (min(p[0] for p in pts), min(p[1] for p in pts),
            max(p[0] for p in pts), max(p[1] for p in pts))


def record_ellipse(rec):
    """The (cx, cy, rx, ry, rot) tuple of an emitted record."""
    return (rec["cx"], rec["cy"], rec["rx"], rec["ry"], rec["rot"])


def sample_arc(rec, n=None):
    """n+1 points along an ellipse-arc record, CCW from t1 to t2 (the lowering's spline fallback:
    the IR has no partial-ellipse primitive). Default n = the source segment count, so the
    transcription is exactly as fine as the drawing was."""
    el = record_ellipse(rec)
    n = n or rec.get("n") or 16
    t1, t2 = rec["t1"], rec["t2"]
    sweep = (t2 - t1) % 360.0 or 360.0
    pts = [point_at(el, t1 + sweep * k / n) for k in range(n + 1)]
    pts[0], pts[-1] = (rec["x1"], rec["y1"]), (rec["x2"], rec["y2"])   # exact ends
    return pts


# --------------------------------------------------------------------------- the fit
def _solve(M, b):
    """Gaussian elimination with partial pivoting on an n x n system."""
    n = len(b)
    A = [row[:] + [b[i]] for i, row in enumerate(M)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(A[r][col]))
        if abs(A[piv][col]) < 1e-14:
            return None
        A[col], A[piv] = A[piv], A[col]
        for r in range(col + 1, n):
            f = A[r][col] / A[col][col]
            for c in range(col, n + 1):
                A[r][c] -= f * A[col][c]
    x = [0.0] * n
    for r in range(n - 1, -1, -1):
        x[r] = (A[r][n] - sum(A[r][c] * x[c] for c in range(r + 1, n))) / A[r][r]
    return x


def fit_ellipse(pts):
    """Closed-form least-squares conic through pts under the trace constraint A + C = 1 (linear,
    rotation-invariant, and never degenerate for an ellipse); data centred first for conditioning.
    -> (cx, cy, rx, ry, rot_deg) with rx >= ry, or None when the best conic is not an ellipse."""
    n = len(pts)
    if n < 5:
        return None
    mx = sum(p[0] for p in pts) / n
    my = sum(p[1] for p in pts) / n
    rows, rhs = [], []               # A(x^2 - y^2) + Bxy + Dx + Ey + F = -y^2   (C = 1 - A)
    for x, y in pts:
        x, y = x - mx, y - my
        rows.append([x * x - y * y, x * y, x, y, 1.0])
        rhs.append(-y * y)
    M = [[sum(r[i] * r[j] for r in rows) for j in range(5)] for i in range(5)]
    v = [sum(r[i] * rhs[k] for k, r in enumerate(rows)) for i in range(5)]
    sol = _solve(M, v)
    if sol is None:
        return None
    A, B, D, E, F = sol
    C = 1.0 - A
    disc = B * B - 4 * A * C
    if disc >= 0:
        return None                              # parabola / hyperbola
    x0 = (2 * C * D - B * E) / disc
    y0 = (2 * A * E - B * D) / disc
    Fc = A * x0 * x0 + B * x0 * y0 + C * y0 * y0 + D * x0 + E * y0 + F
    h = B / 2.0                                  # eigen-decompose [[A, h], [h, C]]
    tr, det = A + C, A * C - h * h
    root = math.sqrt(max(tr * tr / 4.0 - det, 0.0))
    l1, l2 = tr / 2.0 + root, tr / 2.0 - root
    if l1 <= 0 or l2 <= 0 or -Fc / l1 <= 0 or -Fc / l2 <= 0:
        return None
    if abs(h) > 1e-12:                           # eigenvector of the SMALLER eigenvalue = rx axis
        vx, vy = h, l2 - A
    elif A <= C:
        vx, vy = 1.0, 0.0
    else:
        vx, vy = 0.0, 1.0
    rot = math.degrees(math.atan2(vy, vx)) % 180.0
    return (x0 + mx, y0 + my, math.sqrt(-Fc / l2), math.sqrt(-Fc / l1), rot)


# --------------------------------------------------------------------------- run detection
def _key(p, eps):
    return (round(p[0] / eps), round(p[1] / eps))


def _turn(p0, p1, p2):
    """Signed turn (degrees, + = CCW) at p1 between chords p0->p1 and p1->p2."""
    ax, ay = p1[0] - p0[0], p1[1] - p0[1]
    bx, by = p2[0] - p1[0], p2[1] - p1[1]
    return math.degrees(math.atan2(ax * by - ay * bx, ax * bx + ay * by))


def _chain_points(lines, chain):
    pts = []
    for i, d in chain:
        l = lines[i]
        s, e = ((l["x1"], l["y1"]), (l["x2"], l["y2"])) if d == 1 else \
               ((l["x2"], l["y2"]), (l["x1"], l["y1"]))
        if not pts:
            pts.append(s)
        pts.append(e)
    return pts


def _endpoints(rec):
    return (rec["x1"], rec["y1"]), (rec["x2"], rec["y2"])


def detect_runs(lines, arcs, ellipses, chain_eps, max_turn_deg):
    """-> [{idx: [line index...], dir: [+1|-1...], closed, class}] -- candidate fans.

    Walks degree-2 chains of LINES per edge class (degree counted over lines, arcs AND ellipses of
    that class, so a run never crosses a junction), then splits each chain wherever the turn
    between consecutive chords leaves (_MIN_TURN, max_turn] or flips sign -- a curve without an
    inflection turns one way, slowly. Two-segment runs are kept; min_segments is the caller's."""
    by_class = {}
    for i, l in enumerate(lines):
        by_class.setdefault(l["c"], []).append(i)
    runs = []
    for cls, idxs in by_class.items():
        inc = {}
        for i in idxs:
            for p in _endpoints(lines[i]):
                inc.setdefault(_key(p, chain_eps), []).append(("l", i))
        for arr in (arcs, ellipses):
            for j, a in enumerate(arr):
                if a["c"] != cls or "x1" not in a:
                    continue
                for p in _endpoints(a):
                    inc.setdefault(_key(p, chain_eps), []).append(("o", j))

        used = set()
        for seed in idxs:
            if seed in used:
                continue
            used.add(seed)
            chain = [(seed, 1)]
            head, tail = _endpoints(lines[seed])

            def _next(at):
                cands = inc.get(_key(at, chain_eps), [])
                if len(cands) != 2:
                    return None
                other = [c for c in cands if not (c[0] == "l" and c[1] in used)]
                if len(other) != 1 or other[0][0] != "l":
                    return None
                return other[0][1]

            closed = False
            while True:
                nx = _next(tail)
                if nx is None:
                    break
                used.add(nx)
                fwd = _key(_endpoints(lines[nx])[0], chain_eps) == _key(tail, chain_eps)
                chain.append((nx, 1 if fwd else -1))
                tail = _endpoints(lines[nx])[1 if fwd else 0]
                if _key(tail, chain_eps) == _key(head, chain_eps):
                    closed = True
                    break
            if not closed:
                while True:
                    nx = _next(head)
                    if nx is None:
                        break
                    used.add(nx)
                    fwd = _key(_endpoints(lines[nx])[1], chain_eps) == _key(head, chain_eps)
                    chain.insert(0, (nx, 1 if fwd else -1))
                    head = _endpoints(lines[nx])[0 if fwd else 1]

            pts = _chain_points(lines, chain)
            turns = [_turn(pts[k - 1], pts[k], pts[k + 1]) for k in range(1, len(pts) - 1)]
            if closed:
                turns.append(_turn(pts[-2], pts[0], pts[1]))
            ok = [_MIN_TURN_DEG <= abs(t) <= max_turn_deg for t in turns]
            if closed and turns and all(ok) and len({t > 0 for t in turns}) == 1:
                runs.append({"idx": [c[0] for c in chain], "dir": [c[1] for c in chain],
                             "closed": True, "class": cls})
                continue
            cur, sign = [0], None
            for k in range(len(chain) - 1):
                if ok[k] and (sign is None or (turns[k] > 0) == sign):
                    cur.append(k + 1)
                    sign = turns[k] > 0
                else:
                    if len(cur) >= 2:
                        runs.append({"idx": [chain[s][0] for s in cur],
                                     "dir": [chain[s][1] for s in cur],
                                     "closed": False, "class": cls})
                    cur, sign = [k + 1], None
            if len(cur) >= 2:
                runs.append({"idx": [chain[s][0] for s in cur], "dir": [chain[s][1] for s in cur],
                             "closed": False, "class": cls})
    return runs


def _polyline_ccw(pts, c):
    s = 0.0
    for k in range(len(pts) - 1):
        ax, ay = pts[k][0] - c[0], pts[k][1] - c[1]
        bx, by = pts[k + 1][0] - c[0], pts[k + 1][1] - c[1]
        s += math.atan2(ax * by - ay * bx, ax * bx + ay * by)
    return s > 0


# --------------------------------------------------------------------------- the collapse
def make_record(el, cls, p1=None, p2=None, ccw=True, n=None, fit=None, nd=3):
    """Build an emitted ellipse record. p1/p2 = the arc's own end vertices in TRAVERSAL order;
    the record is normalised to run CCW from t1 to t2 (like a DXF arc), so a CW traversal is
    stored with its ends swapped."""
    cx, cy, rx, ry, rot = el
    rec = {"cx": round(cx, nd), "cy": round(cy, nd), "rx": round(rx, nd), "ry": round(ry, nd),
           "rot": round(rot % 180.0, 2) % 180.0, "c": cls}
    if p1 is not None:
        if not ccw:
            p1, p2 = p2, p1
        t1 = round(math.degrees(param_of(p1, el)), 2) % 360.0
        t2 = round(math.degrees(param_of(p2, el)), 2) % 360.0
        rec.update({"t1": t1, "t2": t2, "x1": round(p1[0], nd), "y1": round(p1[1], nd),
                    "x2": round(p2[0], nd), "y2": round(p2[1], nd), "dir": 1})
    if n is not None:
        rec["n"] = n
        rec["fit"] = round(fit, 3)
    return rec


def collapse_fans(lines, arcs, ellipses, fit_eps, chain_eps, min_segments=DEFAULTS["min_segments"],
                  max_turn_deg=DEFAULTS["max_turn_deg"], min_sweep_deg=DEFAULTS["min_sweep_deg"],
                  nd=3):
    """-> (remaining_lines, fitted_ellipses). Every accepted run leaves the line array and becomes
    one record; a refused run is left exactly as it was (raw is the lossless fallback).

    On a failed fit an OPEN run is trimmed one segment at a time from the end that looks least
    like a chord: a straight tangent absorbed into the run (a slot flank, a boss silhouette) is far
    longer than any chord, so an end > 3x the median chord goes first; otherwise the end that
    deviates more from the current fit. A CLOSED run is all-or-nothing.

    A fitted ARC is emitted only when it sweeps >= min_sweep_deg (parametric): the residual proves
    the curve passes through the points, not that its PARAMETERS are determined -- measured on
    synthetic fans with the exporter's own sag, a 45-60 deg arc fits rx wrong by 2-9%, a >= 90 deg
    arc by <= 0.65%, a half ellipse by 0.03%. A short arc stays raw rather than carry a number
    that is not a measurement."""
    runs = detect_runs(lines, arcs, ellipses, chain_eps, max_turn_deg)
    fitted, consumed = [], set()
    for run in runs:
        chain = list(zip(run["idx"], run["dir"]))
        closed = run["closed"]
        pts = _chain_points(lines, chain)
        if closed:
            pts = pts[:-1]
        rec = None
        while len(chain) >= min_segments:
            poly = pts + ([pts[0]] if closed else [])
            el = fit_ellipse(pts)
            if el is not None:
                dev = deviation(poly, el)
                if dev <= fit_eps:
                    rec = (el, dev, chain, poly)
                    break
            if closed:
                break
            seglen = [math.hypot(pts[k + 1][0] - pts[k][0], pts[k + 1][1] - pts[k][1])
                      for k in range(len(pts) - 1)]
            med = sorted(seglen)[len(seglen) // 2]
            head_long, tail_long = seglen[0] > 3 * med, seglen[-1] > 3 * med
            if head_long != tail_long:
                drop_head = head_long
            elif el is not None:
                drop_head = distance(pts[0], el) >= distance(pts[-1], el)
            else:
                drop_head = True
            if drop_head:
                chain, pts = chain[1:], pts[1:]
            else:
                chain, pts = chain[:-1], pts[:-1]
        if rec is None:
            continue
        el, dev, chain, poly = rec
        if closed:
            fitted.append(make_record(el, run["class"], n=len(chain), fit=dev, nd=nd))
        else:
            ccw = _polyline_ccw(poly, (el[0], el[1]))
            if math.degrees(sweep_param(el, poly[0], poly[-1], ccw)) < min_sweep_deg - 1.0:
                continue                          # too short an arc to pin the parameters: raw
                # (1 deg of slack: a quarter arc's rounded end vertices read 89.99)
            fitted.append(make_record(el, run["class"], poly[0], poly[-1], ccw,
                                      n=len(chain), fit=dev, nd=nd))
        consumed.update(i for i, _d in chain)
    remaining = [l for i, l in enumerate(lines) if i not in consumed]
    return remaining, fitted
