# recipe-usage.md — The IR Generation Recipe (usage edition)

**Version: 0.22.0** · Owner: cad-planner · Served to the model section-by-section as MCP
**resources** — one static `recipe://usage/<slug>` per section, indexed by
`recipe://usage/index` (ADR-069, 2026-07-30; previously the `get_recipe` tool). This is the
operational rule set for turning an analysis artifact's recipe into a Feature Graph IR (and for
producing reconstructable drawings). Sections are addressed by the slug in each `##` header.

Any IR block written into an artifact MUST record which `recipe_version` produced it
(`ir.generator.recipe_version` in `analysis-artifact.schema.json`).

## contract — Input / output contract

- **Input:** one analysis artifact's `recipe` block (features in tree order, mass_properties,
  geometry counts, parameters table). Optionally live `analyze_model` reads when a detail
  (e.g. an exact sketch profile via `analysis_type='sketch'`) is missing from the artifact.
- **Output:** a Feature Graph IR conforming to `feature-graph.schema.json`
  (see the `schema://feature-graph` resource), written into the artifact's `ir.graph` — plus an
  honest `ir.verification` block (see the `verification` section). Never write a graph without
  a verification status.
- **Never** emit raw tool sequences; the IR is the only output. Lowering belongs to the
  deterministic compiler.

## canonicalization — Rules C1–C7

The same part must always yield the same IR, modulo parameter values:

- **C1 — Tree order is law.** IR nodes follow the artifact's feature order EXACTLY. Never
  reorder, never group "similar" features — overlapping boss/cut results are order-dependent.
- **C2 — Deterministic node ids.** `n1..nN` assigned in tree order. A sketch consumed by the
  next feature gets its own node immediately before the consumer.
- **C3 — Lift parameters, don't inline them.** Every dimension in the artifact's `parameters`
  table is referenced by NAME in the `ir.graph._params` side table
  (`{param_name: {node, field, value_si}}` — additive, ignored by the compiler). Two bolts must
  differ only in parameter values, never in graph shape.
- **C4 — Units and rounding.** SI meters, radians internally / degrees at tool boundaries;
  numbers rounded to 6 decimals (1 µm grid) exactly as the recipe reports them.
- **C5 — No silent gaps.** A feature the vocabulary cannot express is NOT skipped quietly: stop
  (or degrade explicitly) and record the gap (`VOCABULARY_GAP` with the missing type named).
- **C6 — Suppressed features** are carried in the recipe but NOT emitted as IR nodes (they add
  no geometry); note them in `notes` so a variant workflow can unsuppress deliberately.
- **C7 — Ground-truth readback for orientation-carrying state.** When a feature's behaviour
  depends on a stored SELECTION or DIRECTION FLAG (a sheet-metal blank's thickness flags, a
  sketched bend's fixed-side pick, any reverse/flip), copy the ORIGINAL feature's own stored
  value — the reader reports them — and never reconstruct "an equivalent" from the resulting
  geometry. Two constructions can be B-rep-identical yet carry opposite intrinsic orientations
  that downstream features are measured against.

## forward — Forward authoring (intent → IR, no original part)

Read this FIRST when writing a Feature Graph from DESIGN INTENT (the forward door,
`submit_feature_graph`) rather than from an analysis artifact. The `mapping_*` sections assume
an ORIGINAL part whose reader output you copy verbatim (C7 readback); forward authoring has no
original — these rules replace the readback discipline there. C1–C5 still apply.

- **Vocabulary + grammar come from the schema** (the `schema://feature-graph` resource — the
  capability registry; what is not in it cannot be built). Grammar essentials: `extrude` /
  `revolve` / `rib` / `sweep` / `sheet_metal` / `sketched_bend` consume the ACTIVE sketch —
  each must IMMEDIATELY follow its (profile) sketch node. `loft` profiles, a sweep's `path`,
  pattern seeds (`feature`) and mirror `features` reference EARLIER nodes by id — never by
  name (the compiler substitutes runtime names). Units METERS, angles RADIANS; nodes build in
  array order.
- **Sketch profiles close by SHARED ENDPOINTS.** Consecutive primitives sharing identical
  endpoint coordinates close the contour — do NOT add constraints or dimensions
  (frozen-coordinate discipline). Arcs need `dir` (+1 CCW / −1 CW). OMIT `frame` — it is
  reverse-replay data; a forward sketch on a datum/offset support needs none.
- **Design anchors from the geometry you are CREATING.** An edge/face anchor's `near` must be
  the target's coordinate IN THE STATE the node executes (after all earlier nodes). Compute it
  from your own intended dimensions — and know the TOOL DEFAULTS that decide where geometry
  lands: a base flange thickens to the sketch plane's −normal side by default
  (`reverse_thickness` flips, `symmetric_thickness` splits ±t/2); box/rectangle profiles are
  centred on the sketch origin; extrude runs toward +normal (`reversed` flips). A missed
  anchor fails LOUD with the nearest distance — read it, correct the coordinate ONCE (the miss
  distance usually names the mistake, e.g. exactly one sheet thickness), never iterate blindly.
- **Build direction from the datum.** A boss/extrude on a canonical datum builds toward the
  datum's +normal — Front→+Z, Top→+Y, Right→+X — unless `reversed`. Material stacks along
  +normal: a feature built ON TOP of that boss sits at +normal, and a CUT into the material runs
  −normal (into the solid) — pick the cut sign from this, never by guessing. An `EXTRUSION_FAILED`
  (or a cut that removes nothing) is almost always the WRONG SIGN — flip the direction ONCE and
  resubmit; do not re-guess the geometry or iterate blindly (the loud failure IS the sign
  correction). Fixes the recurring top-hole-drilled-the-wrong-way bug.
- **Prefer the forward-friendly forms:** `edge_flange` with `length` (not frame+profile —
  those exist for verbatim replay); `material {name, library?}` at graph level; a GRID as a
  pattern OF a pattern (a `linear_pattern` seeding on an earlier `linear_pattern` — the tool
  has no second direction).
- **Know what is NOT expressible** (state it, don't improvise a lookalike): sweep/revolve/loft
  are BOSS-only; no shell/draft/dome/wrap/hole-wizard/thread; `linear_pattern` is
  single-direction (+`flip`); mirror planes are canonical datums only. If the intent needs one
  of these, report the gap (C5).
- **Self-verify without an original:** before submitting, COMPUTE the expected outcome from
  intent — volume by hand (πr²·L for a swept tube, π·a·b·h for an elliptic prism,
  plate−n·holes for patterns), face/edge counts, CG shifts for asymmetric removals. After
  COMPLETED, read `analyze_model(mass_properties + geometry)` and compare — a match within
  rounding is the forward equivalent of the round-trip verdict. Splines are the exception:
  through-point recreation is visually equivalent, never exact — check bounds, not equality.
- **Failure model:** CAD ops are not transactional — a failed run leaves partial geometry
  (reported, never hidden). Fix the graph and resubmit with `fresh_document=true` rather than
  patching the partial document.

## mapping — Core mapping steps (recipe → IR)

1. Walk `recipe.features` in order; classify each against the CURRENT covered vocabulary —
   **the schema IS the registry**: read the node types from `feature-graph.schema.json`
   (the `schema://feature-graph` resource). Part vocabulary (0.7.2-draft): `box`,
   `sketch`+`extrude` boss/cut (ends blind / through_all / up_to_surface / mid_plane),
   `hole`-on-face, `fillet`, `chamfer`, `revolve`, `sweep`, `rib`, `loft`, `linear_pattern`,
   `circular_pattern`, `mirror`, `sheet_metal`, `sketched_bend`, `edge_flange` (custom-profile
   or simple length mode); profiles rectangle/circle/line/arc/ellipse/spline + construction;
   sketch supports = datums front/top/right with a signed `offset` OR a `ref.face` anchor; an
   optional graph-level `material {name, library?}` (part graphs only). ASSEMBLY documents use
   the separate `component`/`mate` sub-vocabulary (see `mapping_assembly`).
2. For each mappable feature emit the IR node per C1–C4; resolve its sketch plane from the
   recipe's `plane {ref, offset}` → `ref {datum, offset}` (the compiler creates the offset plane
   itself and threads its RUNTIME name — never guess a plane name). A plane the reference model
   can't express (angled/face-bound beyond the `ref.face` anchor) is a `RESOLVER_GAP`, not an
   excuse to guess.
3. First unmappable feature ⇒ stop mapping (partial graphs are worthless for round-trip);
   record `VOCABULARY_GAP` / `RESOLVER_GAP` with the exact missing types/selectors.
4. If all features mapped: attach `_params` (C3), set `schema_version`/`units`, and hand the
   graph to verification. Set `ir.verification.status='unverified'` until the round-trip runs.

## mapping_part — Part-vocabulary rules

- **ALWAYS record each sketch's `frame`.** `analyze_model(sketch)` reports
  `plane.frame {origin, xdir, ydir}` (the sketch→model axes). Copy it verbatim onto the IR
  sketch node. `{ref, offset}` alone is LOSSY — it drops the support's normal sign and the
  in-plane axis orientation, so a sketch on a −Y-normal face is the MIRROR of one on a +Y-normal
  plane at the same height. The compiler compares the recorded frame against the frame
  `create_sketch` MEASURES on the rebuild and transforms every coordinate.
- **Path profiles carry EXACT coordinates.** When a sketch is not a single rectangle/circle,
  read its full geometry with `analyze_model(analysis_type='sketch', name=…)` and emit one
  primitive per segment, in segment order, coordinates verbatim (6 decimals): `line
  {x1,y1,x2,y2}`, `arc {cx,cy,x1,y1,x2,y2,dir}` (the reader's `dir` is REQUIRED —
  centre+start+end alone describe two arcs), `circle {diameter, cx, cy}`. Copy each segment's
  `construction: true` flag. Do NOT add constraints or dimensions — identical shared endpoints
  close the contour and the lifted `_params` (C3) carry the design intent.
- **Extrude direction: trust the recipe's `reversed` flag verbatim.** The reader normalizes
  `reversed` against the canonical plane axis, which is exactly the rebuild's frame:
  `reversed: true` in the recipe ⇒ `"reversed": true` on the IR extrude node, nothing else.
- **`end: 'up_to_surface'` needs a face anchor** (`up_to.face.near` + optional `hint`): a point
  ON the terminating face's plane, taken from the ORIGINAL part's `analyze_model(faces)`
  representative point of that face. The resolver matches by plane containment on the rebuilt
  geometry (coplanar faces are interchangeable for an up-to).
- **`fillet` edge anchors reference the PRE-fillet geometry** — in the finished part the
  filleted edges no longer exist (the fillet consumed them). Identify them by topology delta
  (one edge = +1 face/+3 edges/+2 vertices; fillet-face area ≈ (π/2)·r·L pins the edge length)
  and take each anchor `near` from the pre-fillet state's `analyze_model(edges)` midpoint
  (a partial rebuild of the nodes so far, or geometric inference; `analyze_model(feature_map)`
  reports each feature's consumed edges directly). Always write a `hint` describing the edge
  semantically — the compiler ignores it, but it feeds the future semantic reference model.
- **Anchors are replay-exact, edit-fragile.** They survive a fresh-doc rebuild bit-for-bit but
  break on ANY upstream change. A parametric variant workflow must re-derive anchors — do not
  reuse a graph's anchors after editing `_params` upstream of them.
- **`sweep` grammar (0.9.0):** node order is path sketch, …, PROFILE sketch, sweep — the profile
  sketch must IMMEDIATELY precede the sweep (it is consumed as the active sketch, extrude's
  grammar); `path` references the EARLIER path-sketch node by id (the compiler substitutes its
  runtime name). The path is an open line/arc chain that starts ON the profile's plane; boss
  only (the tool surface has no sweep cut).
- **`linear_pattern` (0.9.0):** `{feature, direction: x|y|z, spacing, count, flip?}` — the seed
  is an earlier feature-producing node by id; `flip` patterns toward the NEGATIVE axis. SINGLE
  direction per node: compose a grid as a pattern OF a pattern (a `linear_pattern` is itself a
  valid seed). REVERSE-reading gap (recorded): `analyze_model(features)` lifts only an
  LPattern's `d1_instances` + `d1_spacing_si` — direction/flip are not readable yet; a reverse
  mapping of a linear pattern needs a geometric direction inference until the reader grows.
- **`ellipse` / `spline` profile primitives (0.9.0):** copy the reader's segments verbatim —
  ellipse `{cx,cy, x1,y1 (major-axis point), x2,y2 (minor-axis point)}`, spline
  `{points: [x1,y1,x2,y2,…]}` (flat through-points). HONEST spline caveat: recreation from
  through-points is visually equivalent, not bit-identical (SW stores control points +
  tangency) — expect volume/area within tolerance, never byte-exact.
- **Graph-level `material` (0.9.0):** `{name, library?}` at the TOP of the graph (not a node —
  material is document state), applied by the compiler AFTER the last node; part graphs only.
  REVERSE-reading gap (recorded): the reader does not lift the applied material yet — take it
  from the artifact/user when known, else omit.

## mapping_sheet_metal — Sheet-metal rules

- **`sheet_metal` (base flange): copy the ORIGINAL Base-Flange's flags VERBATIM (C7).**
  `analyze_model(features)` reports a `base_flange` block —
  `{thickness, bend_radius, k_factor, reverse_thickness?, symmetric_thickness?}`. Map them 1:1
  onto the node. Do NOT derive thickness or its direction from face positions/bbox: topology
  counts and visible face planes are thickness-direction-blind, and the flags define the
  intrinsic sheet orientation every downstream bend folds against.
- **`sketched_bend`: the fixed anchor comes from the original's own pick.** The reader reports
  `sketched_bend {angle (rad), radius?, position, flip?, fixed_pick [u,v,0], …}`. `fixed_pick`
  is in the BEND SKETCH's 2D space — map it to model space through that sketch's `frame`:
  `p3d = origin + u·xdir + v·ydir` → the node's `fixed.near`. Copy `angle` / `radius` (omit when
  absent = sheet default) / `position` (omit when `centerline`) / `flip` verbatim. One sketch
  with N bend lines = ONE node. Write a `hint` naming the region that stays put. The fixed FACE
  is stored pre-bend and may span all regions — the POINT alone selects which region stays
  fixed, which is why this anchor passes through as a coordinate, never an index.
- **`mirror`: seeds by node id, plane canonical.** The reader's `mirror {plane, features}` gives
  the mirrored feature names — reference the IR nodes that created them (`nodes: [...]`); the
  compiler substitutes runtime names. `plane` maps to the canonical datum. SolidWorks refuses to
  mirror a bare sketched bend — mirrors reference flanges/cuts; if a mirror's seeds aren't
  expressible yet, that is the `VOCABULARY_GAP`, not the mirror.
- **Non-axis sketch supports use the `ref.face` anchor:** a sketch on a bent/flange face (its
  plane is no canonical datum ± offset) gets `ref {face: {near, hint?}}` — a point ON that face
  from the ORIGINAL's `analyze_model(faces)`; the frame rule still applies and handles the
  orientation.
- **Anchor `near` points must be INTERIOR ground truth, never boundary constructions.** A
  segment midpoint or edge point can lie EXACTLY on a second face's plane and resolve AMBIGUOUS.
  For a bend sketch's face anchor use the bend's own `fixed_pick` mapped through the sketch
  frame — the stored pick is interior and unique by construction (C7).
- **`edge_flange` (custom profile): a SELF-CONTAINED node** (no separate sketch node — the
  flange API only accepts a profile sketch IT generated, so the compiler generates/clears/
  redraws it). From the reader's `edge_flange` block: `edge.near` = `edges[0].mid` (the compiler
  resolves it to an edge INDEX — a raw coordinate pick can miss a real edge); `angle` / `radius`
  (omit = sheet default) / `position` verbatim (C7); `frame` + `profile` = the flange's
  `profile_sketch` read fully via `analyze_model(sketch, name=…)` — `frame` is REQUIRED (the
  rebuild's generated profile sketch has an unpredictable frame; the frame transform maps the
  original coordinates into it).
- **`edge_flange` SIMPLE LENGTH mode (0.9.0 — the FORWARD form):** give `length` INSTEAD of
  frame+profile for a plain full-edge-width flange ("20 mm flange on this edge at 90°") — one
  tool call, edge resolved by index from `edge.near`. Length mode accepts only edge + angle +
  length (position = material_inside, radius = sheet default, no flip — use the custom-profile
  mode for those). REVERSE mappings keep using frame+profile (verbatim replay, C7); length mode
  exists because a forward generator cannot know the generated sketch's frame in advance.

## mapping_assembly — Assembly rules (component + mate)

The input is an ASSEMBLY artifact (`document_type: "assembly"`): `recipe.components` (tree
order, full transforms), `recipe.mates` (creation order, enum types, per-entity params),
`recipe.mass_properties`, and `relationships.part_files` (source path + sha256 per referenced
part). The output graph contains ONLY `component` and `mate` nodes — part and assembly
vocabularies never mix (a component references its part FILE; part IRs are verified
separately).

- **Grammar: components first (tree order), then mates (creation order).** Both orders are law
  (C1 extends). Node ids `n1..nN` in that order (C2).
- **`component` node — copy the reader VERBATIM (C7):** `source.path` = the reader's component
  path, `source.hash` from `relationships.part_files`; `config` when reported; `fixed` exactly
  as read; `transform` = ALL 13 numbers exactly as reported (3×3 rotation row-major +
  translation meters + scale). The transform is the authoritative placement for a fixed
  component and the initial placement + verification ground truth for a floating one (mates do
  the constraining; inserting at the final transform keeps the solve trivial). A component with
  `children` (a subassembly) or a suppressed component is a recorded `VOCABULARY_GAP` — stop (C5).
- **`mate` node — type/alignment/value verbatim from the ENUMS (C7):** the reader maps
  `swMateType_e`/`swMateAlign_e` to canonical strings (locale-proof); NEVER derive type or
  alignment from resulting positions, and never touch display names. `value` for distance
  (meters) / angle (radians) mates comes from the mate's own dimension (SI). A mate type outside
  the covered slice (coincident / concentric / perpendicular / parallel / tangent / distance /
  angle / lock) is a `VOCABULARY_GAP`, stop (C5). An entity owned by no component (an assembly
  datum) is a gap too.
- **Mate side anchors come from the mate's own stored EntityParams** (the reader's `params`:
  `[px, py, pz, dx, dy, dz, r1, r2]` — location + direction + radius, ASSEMBLY space, final
  positions). Map them COMPONENT-LOCAL through the inverse of that side's component transform
  (`p_loc = ((p − t) · X, (p − t) · Y, (p − t) · Z)`, columns from the transform; direction the
  same without the translation) — local anchors are transform-invariant. Then:
  - `r1 > 0` (a cylindrical fit): anchor `kind: "cylinder"`, `dir` = the local axis, `radius` =
    r1 verbatim, `near` = **proj(component origin → the local axis line) + r·u** with u a
    deterministic perpendicular. NEVER use the stored point directly: the params' axis point
    lies ANYWHERE on the infinite axis.
  - `r1 == 0` (a plane): anchor `kind: "plane"`, `near` = the local point, `dir` = the local
    normal. `dir` is REQUIRED: a stored point can lie on TWO distinct planes of the same
    component; the normal disambiguates deterministically.
  The compiler resolves each anchor to a face/edge INDEX on that component
  (`analyze_assembly(faces|edges, component)`, component-local coords) — index-first selection,
  never coordinate picks.
- **Distance-mate SIDE (`flip`) is not readable from the mate object, and the mate API FORCES a
  side regardless of the current position.** Derive it from the stored final-configuration
  params: **`flip = dot(p1 − p0, n0) < 0`** (entity 0 and 1 in reader order, assembly space).
- **Under-constrained assemblies are honest, not errors:** real mechanisms have free DOFs.
  The round-trip still verifies because components are inserted at their recorded transforms
  and consistent mates do not move them. Record looseness in the artifact notes, never invent
  extra mates.
- **Analyze the assembly ONCE, not per step (added 0.16.1).** Read the structure a SINGLE time —
  `analyze_assembly(components)` + `(mates)` — to build the whole component+mate node list; do NOT
  re-call `analyze_assembly` after each insert/mate to re-check positions. Each call re-walks the
  ENTIRE component tree and reads every `Transform2` (a full COM pass — the dominant cost on a large
  assembly, and the reason a mated build feels as slow as modelling a part). Build the complete
  graph, then VERIFY ONCE at the end via `compare_assemblies`. Positions are mate-derived + the
  recorded transforms; there is nothing to re-read between steps.

## verification — "The LLM proposes, the round-trip decides"

Per PART (never per batch):

1. Rebuild the graph in a FRESH document — via `rebuild_from_ir` (the graph type picks the
   document: part graphs → a new part, assembly graphs → a new assembly).
2. Objectively diff rebuilt vs original — via `compare_parts` (parts) / `compare_assemblies`
   (assemblies).
3. **`verified` (PART) =** topology EXACT (bodies, faces, edges, vertices counts ALL equal)
   **AND** |volume Δ| ≤ 1% **AND** |surface-area Δ| ≤ 1%.
   **`verified` (ASSEMBLY) =** component set EXACT (source + config + instance counts) **AND**
   every component transform within tolerance (position ≤ 1 µm, rotation ≤ 1e-6) **AND** mate
   count + type multiset match **AND** |ΔV| ≤ 1% AND |ΔA| ≤ 1%.
4. Anything less that still built: `failed` with `detail.reason='MISMATCH'` + the measured
   deltas. Could not build / could not map: `failed` with `BUILD_FAILED` / `VOCABULARY_GAP` /
   `RESOLVER_GAP` and specifics.
5. Write the whole outcome into `ir.verification`. **Only `verified` IR may ever be used for
   rebuilds, variants, or pattern matching.**

Reverse (drawing → part) reconstruction has been SPLIT OUT (2026-07-27): the SLDDRW-based
discipline is FROZEN as the SLDDRW TEST edition, the repo file
`cad-planner/slddrw-testing-recipe-usage.md` — deliberately NOT served on the MCP surface
(ADR-069). The DXF/DWG reverse path is the main line and is being rebuilt; do not apply the
frozen rules to a DXF job.

## reverse — Drawing (DXF/DWG) → part reconstruction

The input is a **2D DXF** (a DWG is converted to DXF first — the SolidWorks route is lossless for
every field below, though ADDITIVE: it duplicates section-view geometry, which the reader dedups).
Read it with the `draw`-dialect analysis tool: it hands over sheet scale, views, per-view geometry
with an EDGE CLASS, **contours already chained into closed loops**, true-valued dimensions, and notes.
These rules are v1 — each one was USED in a reconstruction that came back topology-exact (f-1, s-1,
f-2, s-2, 2026-07-27).

**Read R16 FIRST.** The tool answers in one of two shapes, and the shape tells you which job you have.

**R1 — Apply the scale before anything else — and it is PER VIEW, not per sheet.** A DXF dimension's
stored value is PAPER space. The TRUE value is `value × scale` (DXF `DIMLFAC`; the tool already applies
it, and view sizes and geometry are emitted in TRUE mm). A 1:2 sheet reads 50 for a 100 mm part — an
unscaled read silently builds a half-size part and every downstream check still "passes". Two riders:

- A VIEW MAY CARRY ITS OWN SCALE. A detail or section labelled `A-A 1 : 1` on a 1:10 sheet is drawn at
  1:1, and the sheet factor is wrong for it by ten. The reader reads the ratio from the view's own
  LABEL and reports `view.scale_factor` when it differs from the sheet's; its geometry and its
  dimensions are then scaled with THAT factor. Where a view carries a `scale_factor`, say so before
  using any number from it.
- AN ANGLE IS NEVER SCALED. `DIMLFAC` is a length factor; an angular dimension is dimensionless.

**R2 — The edge CLASS is data, never appearance.** Every primitive carries `c`: `visible` (a real edge
on the near side) · `hidden` (obscured — a real feature seen through material) · `cut_line` (a
section's cutting line, not part geometry) · `center` (an axis). Consequence: a feature drawn with
VISIBLE lines is on the face that view shows; the same feature drawn HIDDEN is on the far side. This
one test places a pocket on the correct face (R6).

**R2b — Contours arrive CHAINED; a loop is a fact, an open chain is a question.** Each view carries
`loops[]` (closed contours: `role` outer|inner, `parent`, exact `area` with arc bulges included, and
`seq` — the primitives IN ORDER, as index references) and `open_chains[]` (everything that closed into
nothing). An open chain is a real segment belonging to no contour: a bend line, a centre line, or an
ortho silhouette fragment the chainer stopped at a T-junction. It is never an invitation to close the
loop yourself — see R17.

**R3 — The frame is not a view; the TITLE BLOCK is a parameter TABLE, not junk.** The border/title
block is one cluster the size of the sheet — never a view. A cluster fully INSIDE another view's box is
a FEATURE of that view (a hole, a slot), not a separate view; a cluster sitting in the title-block
corner that no dimension points at is furniture and arrives as `role: "frame_item"` (a projection
symbol, a weld symbol, the oval around a check dimension). But the TEXT down there is often the most
important evidence on the sheet, and it arrives separately as **`frame_notes`**, in reading order:

- Read it as a TABLE: a label and its value share an x, one row apart. On a real industrial block that
  is where LENGTH, WIDTH, THICKNESS, MATERIAL, the standard, the DESCRIPTION, the SCALE, the
  PROJECTION METHOD and the WEIGHT live. On a break view the title-block length is the ONLY length
  there is (R19).
- A field whose value is IDENTICAL across DIFFERENT parts is template boilerplate, not part data —
  a check dimension repeating `257 ±0.1` on five unrelated drawings is not a feature.
- Still do not trust a title-block LABEL over geometry where the two overlap: the f-1/f-2 template
  prints "A3" on a 210×297 (A4) sheet.

**R4 — Alignment gives the axis; the PROJECTION STANDARD gives the sign.** Two views sharing their
paper-X span are a vertical projection pair; sharing paper-Y, a horizontal pair. That much is
convention-independent. What the convention decides is WHICH physical face a placed view shows, and it
is read from config (`projection`), never guessed:

- **first_angle (ISO/European — the configured default):** the view BELOW the front view is the TOP
  view, and the object's **BACK** is the edge ADJACENT to the front view (that view's top edge).
- **third_angle (ASME/American):** the mirror of the above.

A wrong sign builds VALID geometry with no error — only a comparison catches it. State the front/back
(and left/right) assignment EXPLICITLY before placing any depth-axis feature.

**R5 — The base profile comes from ONE view; the other views only place things along the third axis.**
Build the outline — chamfers and corner radii included as sketch primitives, since a profile corner of
a prism is geometrically identical to an edge feature — from the view that shows it, extrude it by the
depth an adjacent view gives, then position the remaining features. In-plane coordinates carry no
convention risk; only the extrusion axis does (R4). f-1 and f-2 were both built this way.

Transcribe the outline from the loop's own `seq`, in the order given: consecutive entries already
share an endpoint exactly, so a line/arc chain maps 1:1 onto a sketch path profile with no
re-derivation (an arc even carries its endpoints and sweep sense). The loop's `area` is exact — use it
for R13 instead of recomputing.

**R6 — Which FACE a pocket or groove sits on is decided by R2, not by plausibility.** f-2's channel is
drawn with VISIBLE lines in the front view ⇒ it is cut into the face the front view shows. Placing it on
the opposite face produced identical topology, volume AND area — the error surfaced only as a CG shift
and a flipped face normal. Depth-axis placement is the highest-risk decision on this path.

**R6b — Knowing which view shows which face is only HALF the decision: bind it to the IR's own BUILD
DIRECTION, explicitly, before the first extrude node.** The IR extrudes toward the datum's POSITIVE
normal by default (Front→+Z, Top→+Y, Right→+X). A boss on `front` therefore grows toward the direction
the front view is looked at FROM — so with the default the sketch plane is the part's **BACK** face, not
its front, and every depth you then take from the drawing's front datum lands mirrored. State the
binding in one sentence and then hold to it:

- *"the drawing's FRONT face is my sketch plane"* ⇒ set **`reversed: true`** on the base extrude, after
  which every subsequent depth is measured from that plane with the drawing's own sign. This is usually
  the cheaper choice, because it is the datum the drawing dimensions from.
- *"my sketch plane is the BACK face"* ⇒ leave the default and SUBTRACT every drawing depth from the
  part's thickness.

Either is legal. Leaving it IMPLICIT is what produced f-2's mirrored first build — and it passed
topology, volume AND area (R14 is why: none of the three can see a reflection). Two riders:

- **A BASE FLANGE's default runs the OTHER WAY.** `sheet_metal` thickens to the sketch plane's
  **−normal** side unless `reverse_thickness` (`symmetric_thickness` splits ±t/2). So the same binding
  question has the opposite default from `extrude` — do not carry one habit into the other. A wrong side
  also puts every downstream bend-sketch plane off the sheet, so it fails later and less clearly.
- `mid_plane` is symmetric by construction and immune; `reversed` is ignored there.

Verify it, do not trust it: after the base feature, read one face or edge back
(`analyze_model(faces|edges, near=…)`) and check that the coordinate matches the dimension that fixed
it. That single readback is the whole guard (R14).

**R7 — A section view is read along its cut line's axis.** The `cut_line` primitives name where the
section was taken; the section view's horizontal axis is then the depth axis. Decide which SIDE of the
section is the front by cross-checking a feature already placed by R2/R6 (in f-2 the channel notch sits
on the front side), then read every depth from that datum. Two independent views must agree — f-2's
hole read 40-from-front in the section and 20-from-back in the top view: the same point.

**R8 — Undimensioned twins and centred features are conventions, not gaps.** A feature carrying no
position dimension across an axis is CENTRED on that axis, or repeats a dimension given for its
symmetric partner. Say which reading you used. (f-2's top hole carries only its depth-axis distance;
its left/right position is the centre.)

**R9 — A Ø equal to another Ø + 2R is that round's RIM, not an orphan circle.** f-2's top view shows a
concentric Ø15 and Ø19; 19 = 15 + 2×2, and the section shows R2 arcs running from the top face into the
bore ⇒ ONE blind Ø15 hole with an R2 fillet at its MOUTH. Consume the whole concentric group or declare
an explicit gap.

### Sheet metal

**R10 — The flat-pattern annotation carries the whole bend, and the READER does the pairing.**
`UP 90 R 1` / `DOWN 90 R 1` gives direction, angle and bend radius. The tool matches each note to its
line on three independent channels — the line's edge class must match the direction (SolidWorks draws
DOWN bend lines `hidden` and UP `visible`, because the flat pattern is viewed from one side and a bend
folding away is an obscured edge), the note sits ABOVE its line along the note's own up axis, and a
line may annotate only one bend. Read the result, do not re-derive it:

- `bend_line` present ⇒ matched. `in_loop: true` on it is a WARNING, not a detail: a bend line belongs
  to no closed contour, so a true there means the match landed on outline geometry — say so.
- `unpaired` present ⇒ the reader refused to guess, and lists why and which candidates it saw. That
  bend is NOT built. Resolve it from the drawing or declare it a gap (R15) — never split the
  difference. A wrong bend cannot be nudged afterwards (there is no sketch-entity move/delete tool);
  repairing one costs deleting the feature and re-creating it.

**R11 — Build sheet metal as FLAT BLANK + sketched bends.** One `sheet_metal` node (thickness from the
thickness view or from a bare text note like "2 mm"; `bend_radius` and `k_factor` from the notes and
config), then one `sketched_bend` node per DIRECTION group — a single sketch may hold several bend
lines, and they share one angle/radius/flip. Order the groups so each bend sketch still lies on FLAT
material: fold the OUTER bends first when an inner region must stay planar for a later sketch. The
`sketched_bend` `fixed` point must sit on material that stays put for EVERY bend — the blank's own
centroid, when it is clear of the bend lines and outside every cutout. (When the reader answers
DIRECT_BUILDABLE it has already done all of this — see R16.)

**R12 — Bent-state dimensioning converts to the flat by closed-form bend arithmetic.** When the drawing
dimensions the FORMED part (outer-to-outer) instead of the blank, each flat segment is
`outer_dim − Σ OSSB + Σ BA/2` over the bends bounding it, with `BA = θ·(R + K·t)` and
`OSSB = (R + t)·tan(θ/2)`; K comes from config (0.5 = the SolidWorks default). Verified exactly on s-2:
15/30/60 outer → 13.5708 / 27.1416 / 58.5708 flat, matching the drawn flat pattern to 4 decimals. If a
flat-pattern view is ALSO present, measure it and cross-check — a mismatch means the K-factor
assumption is wrong; FLAG it, never silently re-derive.

### Self-verification — there is NO original part

A real drawing-only job has nothing to compare against: the part you are producing IS the deliverable,
and nobody re-models a part they already have. Verify from the DRAWING and from computed expectations
only. (A benchmark or test prompt may hand you a reference part and ask for an objective diff — that
instruction comes from the TASK, never from this recipe.)

**R13 — Compute the expected result BEFORE building, then read it back.** Derive the volume from the
drawing's own dimensions (profile area × depth, minus each pocket/hole, minus the chamfer and fillet
corners; for sheet metal, blank area × thickness). After the build read
`analyze_model(mass_properties + geometry)` and compare. A match within rounding is the strongest
verdict available without an original, and a mismatch localises the error immediately because you know
which term you added last.

**R14 — Volume, area and topology cannot see a MIRROR.** A depth feature placed on the wrong face, or
at the mirrored coordinate, leaves all three identical — proven twice in one session. So for EVERY
feature whose position came from a SECOND view, read the built geometry back (`analyze_model(edges |
faces, near=…)` for a hole's axis, a groove's floor plane) and compare that coordinate against the
dimension that fixed it. Do this BEFORE declaring success: it is the only check that catches a sign
error, because a sign error fails silently.

**R15 — Close the ledger.** Every dimension and every primitive in the analysis must end in exactly one
state: consumed by a named feature, an explicit duplicate/silhouette of one, or a written gap. Say
explicitly what you did NOT build and why. Silence about a dropped feature is the worst outcome — worse
than an honest gap.

A SKIPPED bend is the sharpest case of this. Bending does not change the blank's volume, so a missing
bend leaves volume, area AND topology untouched — R13 and R14 both pass on a part that is simply not
folded. The skip report is the only signal there will ever be. Build it by hand (`create_sketch` on the
flat face → `add_sketch_entity(line)` → `sheet_metal_feature('sketched_bend')`) or state it.

**R16 — The reader answers in one of TWO shapes; the shape is the instruction.**

- **`DIRECT_BUILDABLE`** — every decision this drawing needs is forced by the drawing itself, so it has
  already been lowered to IR deterministically. You get the blank, the thickness AND its source, each
  bend with its class corroboration, anything SKIPPED, and the expected volume — but not the contour,
  deliberately: echoing a 200-segment outline back would pay for it twice. Check the summary against
  what the drawing should be, then `mode='build'`. Verification (R13/R14) is still yours, and so is
  every skipped bend.
- **`NOT_DIRECT | <reason>`** + the full analysis — the drawing needs real reading, which is the normal
  case for a machined part. The reason names exactly which decision was left open (no bend notes at
  all, an outline that did not close, competing thickness sources, …). Nothing is lost: apply R1–R15
  to the JSON and build with `submit_feature_graph`.

A NOT_DIRECT verdict is not a failure and not something to argue with. It says the drawing does not
force the answer — which is exactly when a human-grade read is what the job needs.

**R17 — An unresolved contour is a GAP, never a guess.** Chaining is strict: where two primitives meet
unambiguously the contour continues, and at a junction of three or more it STOPS. This closes flat
patterns completely; in ORTHO views it leaves fragments, because a feature silhouette ending in the
middle of an outline edge is exactly such a junction. When the outline you need is in pieces, say which
pieces you have and what you could not close. Do NOT invent the missing segment: a wrong contour builds
VALID geometry with no error — the same silent-failure class as R14's mirror.

### Reading a real production drawing

**R18 — What the drawing PRINTS arbitrates.** Every dimension carries `printed` — the string the CAD
system actually drew, taken from the dimension's own block. It is more reliable than the stored
measurement, which the DWG→DXF route corrupts in two whole classes: an ANGULAR value comes back as
180+θ (a 7.9° bend reads 187.8765, a 45° weld bevel reads 225), and a RADIUS loses its arc side. So for
angular, radius and diameter the printed value WINS and the reader has already substituted it. For a
linear dimension the computed value stays (it carries more decimals than the printed rounding), but a
disagreement beyond rounding is reported as `printed_mismatch` — read that flag, it means the scale,
the arc match or the per-view factor is wrong, and it is free.

**R19 — A BREAK (interrupted) view gives you the PROFILE, never the LENGTH.** A very long part is drawn
shortened, with the middle cut out. The signature is mechanical, all of it computable from what the
reader emits: the outline does not close although every primitive is `visible`; the silhouette edges
appear as COLLINEAR PAIRS with a gap; the gap is spanned by primitives that OVERHANG the silhouette on
BOTH sides (nothing real overhangs its own outline); and they come as a MATCHED PAIR, one set per
broken end. When you see it:

- Take the cross-section/profile from the drawn body. Its extent along the break axis is VOID.
- The length comes from an ANNOTATION — a dimension or the title block. If nothing states it, that is
  a gap (R17 applied to the length axis), never a derivation.
- A dimension placed ON the broken geometry may itself be the DRAWN extent, not the part: cross-check
  it against the title-block length before trusting it, and let R20 arbitrate.
- Classify the break lines explicitly in the R15 ledger. They are furniture, not contour.

**R20 — If the title block states a WEIGHT, it must come out.** Compute it from your reading before
building: volume × density. It is an independent check on the WHOLE interpretation at once — it caught
a break view's true length (338 gives 0.464 kg, the drawn 125 gives 0.172) and it confirms
material-REMOVING detail too, because the stated weight includes it (a 6.7 m plate read 183.47 kg as a
plain blank and 178.73 kg once the 45° weld preparation along both long edges was subtracted, against
a stated 178.694). Do NOT go looking for a weight that is not there — many drawings have none. Use it
when it exists.

**R21 — Sheet metal is decided by DECLARATION first, thickness second.** If the title block says so
(`Blech`, `plate`, and their equivalents), build it as sheet metal. If it does not, a part of
**thickness ≤ 20 mm** that is otherwise a single extrude is sheet metal too — building it as a base
flange is more useful downstream (flat pattern, bends, manufacturing intent) than a plain boss. Above
20 mm there is no rule: decide, and say which way you decided and why.

**R22 — With no UP/DOWN note, bend DIRECTION comes from the projection, not from the line's class.**
`bend_class_map` (visible=UP, hidden=DOWN) is a SolidWorks flat-pattern convention and only applies to
a flat pattern SolidWorks generated — a real drawing draws bend lines as plain continuous lines and may
mark them with a hand leader instead. Read the direction from the FORMED view via R4 instead: find the
edge view of the formed part, apply the projection standard to learn which side of it is the near face,
and see which way the bend centre lies. Then, because the compiler's own fold convention is a BUILD
choice and not something the drawing says: build once, READ THE FOLD BACK (R14), and set `flip` if it
came out mirrored. The magnitude being right and only the sign wrong is the expected first result.

**R23 — On sheet metal, BENDS come before chamfers and fillets.** SolidWorks cannot fold through a
chamfer: a sketched bend crossing a chamfered edge returns null with no useful message. Order the graph
blank → base flange → every bend → only then the edge treatments. Selecting the edges afterwards costs
more (each long edge is split by every bend it crosses, so one weld preparation became 16 edges instead
of 4) — pay it. If the edge treatment is easier to express on the flat, that is not a reason: it will
not build.

## coverage — Coverage reporting

Every batch/folder run ends with one summary:
`parts_total / verified_without_ai / verified_with_ai / unverified / failed`, plus a ranked
list of missing vocabulary from the `failed` details.
