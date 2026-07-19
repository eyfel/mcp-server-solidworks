# recipe-usage.md — The IR Generation Recipe (usage edition)

**Version: 0.15.0** · Owner: cad-planner · Served to the model section-by-section via the
`get_recipe` MCP tool. This is the operational rule set for turning an analysis artifact's
recipe into a Feature Graph IR (and for producing reconstructable drawings). Sections are
addressed by the slug in each `##` header.

Any IR block written into an artifact MUST record which `recipe_version` produced it
(`ir.generator.recipe_version` in `analysis-artifact.schema.json`).

## contract — Input / output contract

- **Input:** one analysis artifact's `recipe` block (features in tree order, mass_properties,
  geometry counts, parameters table). Optionally live `analyze_model` reads when a detail
  (e.g. an exact sketch profile via `analysis_type='sketch'`) is missing from the artifact.
- **Output:** a Feature Graph IR conforming to `feature-graph.schema.json`
  (see `get_recipe('feature_graph_schema')`), written into the artifact's `ir.graph` — plus an
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

- **Vocabulary + grammar come from the schema** (`get_recipe('feature_graph_schema')` — the
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
   (`get_recipe('feature_graph_schema')`). Part vocabulary (0.7.1-draft): `box`,
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

Reverse (drawing → part) reconstruction has its own reading discipline — see the `reverse`
section.

## reverse — Drawing → part reconstruction discipline

When rebuilding a part from ONLY its drawing, the original part must never be opened DURING the
build (deterministic readback of the original is allowed ONLY in the verification phase, to pin
a topology delta). Real drawings carry the MINIMUM view set (often 2–3 views, sometimes with a
section view REPLACING a standard view) and only the necessary dimensions — the rules below turn
that into data, not guesswork.

**EVIDENCE DISCIPLINE (added 0.14.0 — binding, governs the whole reverse path):**

- **Every build decision is resolved by deterministic evidence, or declared a gap.** A
  coordinate, a cut depth / end condition, a fillet or chamfer target edge, a feature type —
  each is fixed by a dimension + its `measures`, a relation group + its `source`/`residual`, a
  `station`, a `center_mark`, or a frame-mapped coordinate; OR it is written up as an explicit
  C5 gap. No decision may rest on plausibility. "I assumed / probably / most likely / looks
  like / appears to be / seems to" are SMELLS, not reasoning: if a decision needs one of them
  it is NOT yet resolved — find the evidence or declare the gap. (The rule bans the MOVE —
  deciding without evidence — not the words; honestly describing a projection is fine.)
- **Unknown stays unknown.** Never substitute the most plausible geometry for missing evidence,
  and never fall back to a DEFAULT (`through_all`, origin-centred, a "typical" size) in place of
  evidence — a default is just a guess wearing a familiar value.
- **A C5 gap must be EARNED.** "unknown / not modeled: X" is legal ONLY after you have shown the
  deterministic channels that could resolve X were consumed (the relevant
  concentric/equal_diameter group, the RD and reference dims, the section contours, the stations
  for that feature). An unchecked "no evidence exists" is itself the violation — on a real
  drawing the evidence is almost always present and simply unread.

**Reading order — dimensions are the skeleton, vectors close the gaps:**

> Ortho views now carry ONLY VISIBLE edges — the reader drops obscured/hidden edges — so internal
> and blind-depth evidence comes SOLELY from dimensions + section contours; an ortho outline never
> shows a hidden internal feature anymore.

1. Call `analyze_drawing(include_geometry=True, include_relations=True)` ONCE and extract
   everything from this FIRST read: all dimensions (value_si + anchors + diametric + `measures`
   + owning view), all per-view vector geometry (lines / curves / circles) with their positional
   ids (`c<i>`/`a<i>`/`l<i>` per view `vid`), the relation groups, the root `stations` table,
   `center_marks`, every view's `frame` (+ `normal_axis`) and `extent`, and each section view's
   `section` block. Corner counts,
   sharp-vs-filleted vertices and exact rectangles are all in this first read — count arcs by
   radius/region BEFORE sketching, so a miss isn't discovered later via the round-trip.
2. Build the DIMENSION SKELETON first: place and size every feature that has a dimension from
   that dimension (an engineer reads the dimension "edge-to-edge 35", not coordinates). Read a
   dimension's `measures` FIRST — it names the primitive id(s) the value measures, with
   `measure_src` (anchor_at_center = a Ø/R on that circle/arc) and `measure_residual`; fall back
   to `anchors` arithmetic only when `measures` is absent. A dimension flagged `unattached:true`
   is a LOUD gap — resolve what it measures from geometry (typical: an angle dim or an
   offset-plane dim whose anchors sit off the projected geometry); never drop it.
3. Close the UNDIMENSIONED gaps from the vectors: engineers deliberately leave out derivable
   dimensions and twins. Twin-hood is DATA now — an `equal_diameter` group (same Ø, distinct
   centers) states which circles are twins, so the undimensioned twin INHERITS the dimensioned
   one's size; anything else is measured from the vector geometry through the view frame.
4. Never guess a coordinate the frame arithmetic can give: for ANY view-2D coordinate (u,v) —
   line endpoints, circle centers, section geometry — `p_model = origin + u*xdir + v*ydir` with
   that view's `frame`.

**MANDATORY PRE-BUILD GATE — write these four tables out BEFORE the first create call.** The
payload's relations/stations/measures only help if consumed; this gate forces the consumption.
A build that starts while any row is unresolved is a discipline violation, not a shortcut.

**Terminal-state rule (added 0.14.0).** Every primitive (each circle, arc group, isolated closed
cluster) AND every dimension (INCLUDING RD reference dims) must end the gate in exactly ONE state:
(i) consumed by a named feature, (ii) an explicit duplicate/silhouette of a named feature, or
(iii) a written C5 gap. "Bound to no feature", "decorative", "unmodeled detail" and "ignored" are
NOT legal terminal states — they are the precise failure this gate exists to stop. The four
G-tables below ARE that one compact evidence table; no per-decision prose citation is required
beyond naming the evidence in the row.

- **G1 — Per-view inventory.** For each view (with its `frame.normal_axis`): every circle, every
  arc group (by radius), every isolated closed line/arc cluster, tallied and classified as
  feature section / silhouette / duplicate-of-another-view. A CIRCLE in a view is the
  cross-section of a feature whose axis runs along that view's normal — circles in
  different-`normal_axis` views are DIFFERENT features unless a section proves otherwise; never
  chain circles across views into one feature by radius alone. An isolated closed cluster is a
  FEATURE (boss or pocket — decide from section/adjacent-view evidence), never decoration.
- **G2 — Dimension coverage table.** EVERY dimension in EVERY view gets a row: value → target
  feature (from `measures`) or an explicit resolution or a declared gap. `unattached:true` rows
  MUST appear and MUST end in a resolution or a written C5 gap — an ignored unattached dim is
  the exact failure mode this gate exists for. Pair same-feature dims by name
  (`D1@Chamfer1` distance + `D2@Chamfer1` angle describe ONE chamfer).
- **G3 — Profile decisions per concentric stack.** Concentric circles are WEAK evidence for
  shape; section/adjacent-view PROFILES are STRONG evidence. When both exist, the section view
  dominates the geometric interpretation — the concentric circles serve primarily as
  dimensional and correspondence evidence: they size and locate whatever the profile shows,
  they do not say WHAT it is (a loft, stepped cylinders, a counterbore and independent coaxial
  features all project the same circles). For every differing-radii `concentric` group: map the
  profile geometry through the frame WITH COMPONENT SIGNS (`p = origin + u*xdir + v*ydir` —
  direction components can be negative) and write the mapped evidence line BEFORE building
  anything over the group. Resolve the group as a WHOLE (added 0.14.0): every member circle
  maps to a feature or a written gap — consuming some members and silently dropping the rest is
  forbidden. A differing-radius partner is itself evidence: a Ø equal to another Ø + 2·R (R a
  fillet/chamfer radius) is that round's RIM, coaxial with the hole — e.g. a Ø19 concentric with
  a Ø15 hole, Δr = R2 = `D1@Fillet2`, is the R2 fillet's rim, not an orphan circle. The section
  profile decides round-vs-step-vs-counterbore; the concentric pair sizes and locates it.
- **G4 — Feature tally + DEPTH SOURCE, before and after (depth source added 0.14.0).** Before
  building: expected counts (holes per axis, fillets per radius, chamfers, lofts/bosses, pockets)
  AND, for every cut or hole, its depth / end-condition WITH the evidence that fixes it — a
  section RD/depth dim (via `measures`), or the dimension-OWNERSHIP through-rule, or a written C5
  gap. `through_all` (and any blind default) may NEVER be assumed: if a section exists it MUST be
  read for the feature's depth contour before any depth is set, and reference dims (RD*) are
  evidence, not clutter. After building: list every expected-but-unbuilt item EXPLICITLY (C5). A
  silently dropped feature is the worst outcome — an explicit "not built: X, because Y" line is
  acceptable; silence is not.

**Signal rules (each from a real benchmark failure):**

- **Dimension NAMES are not feature-location truth.** A dim labelled `@Chamfer2` may be a block
  CORNER chamfer, not a hole chamfer; `@Fillet2` may sit on a feature-INTERSECTION edge (hole ∩
  slot), not a rim. When a fillet/chamfer target is ambiguous, let the round-trip topology delta
  pin it — never infer the target face/edge from the dimension name alone.
- **Through-vs-blind: dimension OWNERSHIP proves THROUGH, the section RD dim proves BLIND.** A
  hole/slot whose POSITION dims are owned by the base sketch (`@Sketch1`) is a loop IN that sketch
  → it goes THROUGH the base extrude. A BLIND depth is fixed by the section's depth contour and its
  RD/reference dim (via `measures`) — read them; NEVER default to `through_all` because "there was
  no plain depth dim", the RD dims carry it (run #3 cut two blind holes through_all after declaring
  "no depth dims existed" — RD2=0.055 and RD3=0.095 were in Section C-C, measuring the hole
  bottoms). The SECTION is the arbiter of blind-vs-through (ortho views no longer carry hidden
  outlines): a section that covers the feature's axis and shows a terminating contour (floor /
  cone) + its RD depth dim ⇒ BLIND, take the depth from there; a section that covers the axis but
  shows NO terminating contour ⇒ nothing stops it ⇒ THROUGH; NO section covering that axis ⇒ the
  depth is a declared C5 gap (per G4), never a `through_all` default.
- **An anchor beyond the base body is a FEATURE, not bad data.** A dimension anchor whose
  coordinate lies BEYOND the base extrude's depth (e.g. z=65.1 mm on a 45 mm body) means the part
  EXTENDS there — an offset-plane feature, a loft/boss nose. Find that feature (an offset-plane
  dimension like `D1@Plane1` plus profile circles usually names it) instead of discarding the
  dimension as inconsistent.
- **Read `extent` before any "no material beyond X" claim.** `geometry.extent` is the
  server-computed model-space span of a view's primitives along its resolved axes, with the
  frame's signs applied. Never re-derive a view's span from raw 2D coordinates — a frame
  direction component can be negative, and a dropped sign silently mirrors the axis.
- **Trust the relation groups — never re-derive what they state.** `relations` are deterministic
  reads, each carrying `source` (a closed enum: why the relation was called) and `residual` (the
  max measured deviation in meters). A `concentric` group IS the shared center — do not
  re-compare circle coordinates; `tangent` records state line/arc contacts (a slot's line-arc
  chain); `touches` gives the wireframe's junction points (shared endpoints + T-contacts;
  projected X-crossings are deliberately NOT listed — a 2D crossing has no same-depth
  guarantee, so never infer contact from crossing lines).
- **Loft/cone/taper signal.** CONCENTRIC full circles of DIFFERING diameter in a plan view plus
  slanted silhouette lines in an adjacent or section view = a loft/cone between those two
  profiles (the circles are its end sections; a third concentric circle is typically a coaxial
  hole through it). The `concentric` relation group hands you this stack directly (members +
  radii + residual) — a shared center is data, not coincidence. This is a SIGNAL, not a verdict:
  per G3 the section/adjacent-view PROFILE decides the shape; the circles size and locate it.
  (An INTERNAL taper/cone's silhouette is a hidden edge — no longer emitted in ortho views — so
  read it from the SECTION; only EXTERNAL loft/cone silhouettes still appear as visible outlines.)
- **Resolve cross-view identity from the `stations` table + dimensions.** A primitive's model-axis
  station lists the CANDIDATE entities at the same coordinate in the other views
  (`{v, members:{vid:[ids]}}`) — join a plan-view circle to its side-view silhouette lines
  (center and center±r stations) through them, and use `frame.normal_axis` to know WHICH axis a
  circle's feature runs along before joining. Candidates are candidates: when a station lists
  more than one plausible entity and the dimensions don't disambiguate, STATE the ambiguity
  explicitly — never silently pick one.
- **A center mark is the drafter's declared feature center.** `center_marks` names the circles
  (`on:[ids]`) the drafter marked as hole/boss axes — prioritize those circles as real feature
  sections (not silhouette artifacts) and take their centers as feature positions.
- **A section view may BE the standard view.** When the side/top view is given AS a section
  (e.g. front + top + Section C-C instead of a plain side view), read the section's OUTER outline
  as that view's profile and its INTERIOR contours as the internal geometry exposed by the cut;
  the `section` block's `frame`/`axis` does the 2D→3D mapping.
- **PDF export+crop is a LAST-RESORT orientation aid, not the primary read.** Use it only when a
  specific 2D→3D orientation or corner is genuinely ambiguous from vectors; a cluttered section
  can mislead (it "looks blind").
- **Readback of the original belongs ONLY to a verification phase, never the build.** Whether a
  verify-against-the-original phase exists at all is the TASK's call: a benchmark/round-trip
  prompt specifies it; a production drawing-only job usually has NO original part to compare.
  When no original comparison is available, self-check from the drawing itself (mass_properties
  sanity vs the read dimensions; every dimension accounted for; report unmodeled features
  explicitly — C5, no silent gaps).

## coverage — Coverage reporting

Every batch/folder run ends with one summary:
`parts_total / verified_without_ai / verified_with_ai / unverified / failed`, plus a ranked
list of missing vocabulary from the `failed` details.

## drawing — Drawing-generation rules (model → drawing)

These govern producing a drawing FROM a model so the drawing is a **complete, reconstructable**
input for the reverse path.

**Section-view coverage — one section per distinct internal-depth AXIS.** A section makes a
feature's hidden depth/chamfer dimensionable only if that feature's axis lies IN the cutting
plane; an axis PERPENDICULAR to the plane shows only its cross-section outline. So:

1. From `analyze_model(features)` (already in hand — no new read), enumerate every feature
   carrying a HIDDEN depth or internal profile: blind/through holes & bores, lofts/tapers,
   blind cuts, internal chamfers/fillets on those.
2. For each, read its **axis direction** from data you already have: a cut/hole → its
   sketch-plane normal / extrude direction (`reversed`); a loft → its profile-plane normal; an
   offset-plane feature → the offset axis.
3. **Group by axis DIRECTION** (not by centre position — parallel-axis features can share one
   cut; only DIFFERENT directions force separate sections).
4. Provide **one section per distinct axis-direction group**. Perpendicular internal features
   require **two orthogonal sections**. Draw each group's cut LINE in a view so the cutting
   plane CONTAINS that axis.
5. **Verify before declaring done:** after `auto_dimension_drawing`, re-read via
   `analyze_drawing` and confirm each intended depth/chamfer actually appears as a dimension in
   SOME section.
6. **Isolate a blind cut's depth — a cluttered section drops it.** `auto_dimension_drawing`
   silently OMITS a blind cut's depth when the section is busy. Prefer the cut that ISOLATES
   the target feature: for a hole/bore, cut in the view where its axis is the viewing
   direction, on a plane at the feature's own depth-position, so the section shows a clean
   feature cross-section. If a depth still won't land, add it manually
   (`add_drawing_dimension`) — but there is no delete-dimension tool, so avoid spurious
   auto-dims by keeping sections clean.

**View display modes — drafting convention.** Orthographic views (front/top/right/back/bottom/
left) → Hidden Lines Visible (hidden edges SHOWN for reference, NEVER dimensioned to);
`add_drawing_view` already defaults ortho views to HLV. Isometric and SECTION views are
EXCLUDED from the HLV rule — isometric keeps the document default, sections show the cut face.
HLV is now HUMAN-FACING only: the reverse reader drops hidden edges, so a drawing must carry a
SECTION for every internal-depth axis (per the section-coverage rule above) — hidden lines can no
longer stand in for a missing section.

**View sourcing — always pass `model_path` explicitly.** `add_drawing_view` without
`model_path` projects the FIRST OPEN part, not the drawing's referenced model. Before building
a drawing, EITHER close all other part docs OR pass `model_path=<target part>` on
`create_drawing` AND every `add_drawing_view`. Confirm the first view shows the intended part
before adding more.
