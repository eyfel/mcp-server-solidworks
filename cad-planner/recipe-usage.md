# recipe-usage.md — The IR Generation Recipe (usage edition)

**Version: 0.7.1** · Owner: cad-planner · Served to the model section-by-section via the
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

## mapping — Core mapping steps (recipe → IR)

1. Walk `recipe.features` in order; classify each against the CURRENT covered vocabulary —
   **the schema IS the registry**: read the node types from `feature-graph.schema.json`
   (`get_recipe('feature_graph_schema')`). Part vocabulary: `box`, `sketch`+`extrude` boss/cut
   (ends blind / through_all / up_to_surface / mid_plane), `hole`-on-face, `fillet`, `chamfer`,
   `revolve`, `rib`, `loft`, `circular_pattern`, `mirror`, `sheet_metal`, `sketched_bend`,
   `edge_flange`; profiles rectangle/circle/line/arc + construction; sketch supports = datums
   front/top/right with a signed `offset` OR a `ref.face` anchor. ASSEMBLY documents use the
   separate `component`/`mate` sub-vocabulary (see `mapping_assembly`).
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

**View sourcing — always pass `model_path` explicitly.** `add_drawing_view` without
`model_path` projects the FIRST OPEN part, not the drawing's referenced model. Before building
a drawing, EITHER close all other part docs OR pass `model_path=<target part>` on
`create_drawing` AND every `add_drawing_view`. Confirm the first view shows the intended part
before adding more.
