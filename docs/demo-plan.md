# End-to-end demo: plan

Goal: the proof by demo of `research-plan.md` §15.3. A free-form F-rep design model of a welded frame is rationalised
into plates and tubes, each an exact part; the parts leave as one STEP assembly, a DXF per plate, a cut list and a bill
of materials; the rationalised assembly is rebuilt as F-rep and compared with the design model in a verification
report. It is the first of the build methods of §12 and the generated assemblies of §16.5, kept to the two methods the
demo needs.

Decisions (2026-10-24): the pump stand; fitted tangent arcs; `Isoform.Build`; MCP and chat first, the playground
after; certified deviations in the first cut. And a speed task with D1: 41 s at 1 mm is too slow for a demo, so the
stand is profiled and made fast before the build work leans on it (done: 2.5 s, see Findings).

## Findings that shape the plan

- **Exact assembly output exists.** `StepWriter.Write(AssemblyResult, …)` writes one product per distinct part (by
  content hash) and each instance as a placed `NEXT_ASSEMBLY_USAGE_OCCURRENCE`; it refuses mirrored placements. Bundles
  (`isoform-bundle/0`) carry an assembly and its documents in one file, with edits, hashes and pinned parts. No new
  STEP work is needed.
- **DXF exists.** `DxfWriter` writes R12 lines, arcs and circles from a solved sketch, or a flat face's exact outline
  (other edges as polylines, counted as approximated). R12 has no ellipse.
- **Queries exist for most checks.** Certified clearance and interference between instances
  (`AssemblyResult.Clearances`; contacts over an area hit the budget but read about zero), certified distance with its
  closest point, minimum wall (sampled), mass properties from the mesh plus a certified volume range
  (`MassProperties.VolumeBounds`), and `DualContouring.Build` meshes any field in any box. Missing: how far one solid
  strays outside another (the maximum of one field over the other's solid or boundary).
- **A blend is not an arc.** The smooth blend is a quadratic smooth min, so a plane section of a blend between two flat
  faces is a parabola: at k = 10 it is 0.61 mm off the circular arc of the same span at its middle (0.06 k).
  Experiment (tangent biarcs, fixed end tangents): 4 arcs fit it within 0.05 mm (max error 0.019 mm at k = 10,
  0.028 at k = 15), 8 within 0.01. An oblique tube's section is an ellipse: 16–32 arcs within 0.05 mm with a naive
  split, fewer with a better one. Plate outlines through free-form faces are fitted, never copied.
- **Smooth unions act within k of both fields, on any side.** Probe: a k = 15 blend on a Ø50 leg embedded 8 mm in a
  12 mm foot bulged the foot's underside (~2 mm) and split two of its sides; coplanar caps of blended extrudes swell by
  k/4. A recipe that works: members run long, join with their blend, and are trimmed by plane cuts afterwards (the
  stand of §1 regenerates with 69 faces and no diagnostics). The demo design follows that recipe; the format guide
  should say it.
- **Regeneration time.** At 1 mm cells (desktop, keep-out bodies included): the probe measured 41 s for the design
  of §1 and 6.6 s for a variant whose top is not trimmed; `Examples.PumpStand` as committed (D1) took 6.6–7.2 s
  before the speed task, untrimmed or not, and 11–12 s at 0.5 mm. After it (`research-plan.md` §14): **2.5–2.6 s at
  1 mm, 4.2 s at 0.5 mm**; `MassProperties.Of` on the whole mesh (1.1 M polygons) 32 s → 1.2–1.4 s at 1 mm, 58 s →
  2 s at 0.5 mm. What was slow: the lost-piece search running futile searches through blends (half the 1 mm time),
  and blend bounds k/4 wide, which kept every cell near a blend uncertified. The design runs natively (MCP, server);
  the playground's single WASM thread is not the place for it.
- **An oblique tube is exact.** Probe: an extruded annulus Ø48.3 × 3.2 cut by two parallel planes at 15.1° regenerates
  in 1.3 s at 0.5 mm with 4 faces; `ExactBrep` gives line and ellipse edges, STEP carries 4 ellipses, volume equals the
  analytic value.
- **A plane section is a field problem the mesher already solves.** Meshing a box one or two cells thick round the
  plane gives polygons with owners and the K3 guarantees (below-resolution reports included); slicing them at the plane
  gives the loops. `SurfaceIntersection.Curve(plane, surface, seed)` gives the exact curve where the owner is analytic:
  a line on a plane, a circle on a cylinder, cone, sphere or torus across the plane's normal, an ellipse on an oblique
  cylinder.
- **Face names do the binding.** A plate can sit on a named face (`SketchPlanes.OfFace`), a tube can run along a named
  cylinder (its `Surface` axis), an interface is a named face. After a design edit the same build document runs again.
- **Tube meets tube is outside the boundary.** A cope is a cylinder–cylinder intersection, a quartic with no closed
  form (`research-plan.md` §14, `kernel-gaps-plan.md` §5). The demo is chosen so every tube ends on a plate.
- **A 2D-cut plate is square to its faces.** Where an oblique tube passes through a plate, the hole must clear the
  tube at both faces (its shadow through the slab), not just at the mid-plane. That is a task of its own (E1), and the
  demo's first cut avoids it.

## 1. The demo part: a pump stand

A welded stand that carries a 40 kg pump 600 mm off the floor, four splayed legs, a top plate and four anchored feet.
Credible as drawn (standard tube, 12 mm plate, M16 anchors) and small: three distinct parts, nine instances. Coordinates
in mm, z up, the stand's axis at x = y = 0.

| Element | Design model (one part document, body `stand`) |
|---|---|
| Top | An organic outline: disc Ø260 and four lobes Ø90 at (±90, ±90), joined with blend k = 20; extruded taller than needed and trimmed by plane cuts `under` (z = 588) and `level` (z = 600). Bore Ø80 on the axis (hose pass-through), 4 × Ø11 (M10, pump feet) at radius 100 on the axes |
| Feet | 160 × 160 × 12 at x, y 150–310, circular pattern of 4; anchor hole Ø18 (M16) at (±270, ±270) |
| Legs | Ø50, axis from (±200, ±200, 12) to (±90, ±90, 588), 15.1° from vertical; run 60 mm past both ends, joined with blend k = 10 into feet and top, pattern of 4; then plane cuts `floor` (z = 0) and `cap` (z = 600) |
| Keep-outs (own bodies) | `pump` Ø300 × 300 on the top face (z 600–900); `hose` Ø60 on the axis, z 0–900; `socket-1`…`socket-4` Ø40 × 60 above each anchor (spanner access; a body each, as one body in four pieces is reported as falling apart) |
| Interfaces | `cap/face` (pump seat), `mount/wall` and copies, `bore/wall`, `foot/z-` and copies (floor), `anchor/wall` and copies |
| Load | The pump, 40 kg on the top face. Recorded and reported with mass and centroid; nothing is sized by calculation |

Probed (with the keep-outs): the design regenerates with no diagnostics, the top's underside is one flat face
(`under/face`), each lobe has its two blend faces and each leg its blend at the top and at its foot. D1 made it
`Examples.PumpStand` (native only, not in the playground's list), checked by `PumpStandTests` at 1 mm: no diagnostics,
63 faces on the stand (81 with the keep-outs), every interface face once per copy, the pump and the sockets touching
without interference, the hose 10 mm clear of the bore.

Why this part: the design model is what a designer means (a stand cast in one piece, legs flowing into the plates)
and what an implicit kernel builds without failing: four oblique blends into a top whose outline is itself blended,
trimmed afterwards. The rationalisation is what a fabricator makes (laser-cut plates, sawn tube). Every fabricated edge
traces back to a named design face, so the report can say which design face each deviation belongs to. The design uses
blends; it needs no offset or shell, and the plan does not force them in.

Rejected: a cantilever wall bracket of gussets and braces. Braces either meet each other (copes, out of reach) or run
in the gusset's plane (a slotted joint), and the keep-outs of the wall bolts crowd the gussets. It is a good second
demo once E1 is in.

## 2. Rationalisation

### 2.1 Build document

A new document, `isoform-build/0`, canonical and hashed like the others:

```json
{"format": "isoform-build/0",
 "design": "stand.json",
 "material": {"name": "S235", "density": 7.85e-6},
 "tolerance": 0.05,
 "sections": [
   {"id": "top",  "plate": {"face": "cap/face"}, "thickness": 12},
   {"id": "feet", "plate": {"face": "foot/z+"},  "thickness": 12},
   {"id": "legs", "tube":  {"along": "leg/a"},   "outside": 48.3, "wall": 3.2}],
 "keepouts": [{"body": "pump", "clearance": 0}, {"body": "hose", "clearance": 5}, {"body": "sockets", "clearance": 0}],
 "interfaces": ["cap/face", "mount/wall", "bore/wall", "foot/z-", "anchor/wall"]}
```

- A plate is `{"face": name}` (its outer face on that flat face, thickness into the material) or `{"plane": SketchPlane}`
  (thickness along the normal), optionally `"near": [x, y, z]` to take one region of the section.
- A tube is `{"along": name}` (the axis of that cylinder) or `{"line": [[x, y, z], [x, y, z]]}`.
- A face name also names its pattern copies (`leg/a` covers `legs/1:leg/a`…), as a source reference covers its result
  pieces; a copy's own name picks that copy.
- Section order is priority: each section is cut by the parts of the sections before it.

### 2.2 Assigned and automatic

Assigned, by the user or the AI, in the build document: which method each section uses, its plane or axis (by name
where the design has the face), thickness and tube size, material, order, keep-outs with their clearances, and the
interfaces. The AI writes it from `regenerate`'s face list (names, kinds) and fixes it from the report. Nothing chooses
sections automatically and nothing sizes members.

Automatic: plate outlines, one plate per region, tube extents and end cuts, trimming by priority, same-part detection,
placements, the outputs and the verification.

### 2.3 Plates

1. The plate's slab is its plane and thickness; its outline is the section of the field (design minus the hulls of the
   parts before it, §2.5) at the slab's mid-plane: a box one or two cells thick round the plane, meshed by
   `DualContouring.Build`, sliced at the plane, every crossing Newton-projected onto the field within the plane.
2. Each loop is cut into runs by owner. An analytic owner gives its exact curve from `SurfaceIntersection`: a line or a
   circle is copied; an ellipse is fitted. Corners between two exact curves are exact (closed form, or Newton on the
   plane and the two surfaces); a blend runs into its neighbours tangentially, and its ends are where the blend begins
   (|f_a − f_b| = k).
3. Free-form runs (blends, lattices, ellipses) are fitted with tangent arcs through their end points and tangents,
   within `tolerance` both sides, the error measured against the exact points.
4. Every region of the section is a plate (`near` picks one). Its part is a sketch in the plate's own frame (XY,
   loops of lines, arcs and circles, no constraints) extruded 0 to t. Entity ids come from the design face they follow
   (an id made from `lobes/2:lobe/blend` and a running number), so a plate's face says where it came from, and a
   circle copied from an interface keeps that link.

On the stand: the top comes out as eight exact arcs (disc and lobes), eight fitted blend runs, the bore and four holes
exact; a foot as four lines and a circle, all exact.

### 2.4 Tubes

The axis comes from the named cylinder (or the line). From the middle of the run outwards, each end stops at the first
face of an earlier part's hull that the axis meets (a plane: the tube's end is cut by it, exact, an ellipse edge where
oblique), or else where the axis leaves the design (cut square). The part is an extruded annulus along z in the tube's
own frame, cut by two `PlaneCut`s. An end that meets another tube is refused (`build-tube-meets-tube`). On the stand
each leg is Ø48.3 × 3.2, 609.7 mm at the long point and 583.6 at the short one, both ends 15.1° off square, cuts
parallel.

Bent tubes along line-and-arc skeletons (sweeps, exact) are E2.

### 2.5 Joints and priority

- **Hull:** what a part takes from the parts after it: a plate's own solid, a tube's outer solid (bore filled), as
  `PlacedField`s.
- **Plate after plate:** the section of design minus the earlier plate runs along that plate's face: a line, exact.
  A butt joint, in the first cut with a T-joint test (the stand does not need it).
- **Tube after plate:** cut by the plate's face (§2.4). The stand's legs stop on the feet's tops and the top's underside.
- **Plate after tube, pierced:** the tube's shadow through the slab plus a fit-up gap (two half-ellipses and two tangent
  lines, fitted). E1; a mid-height spider plate joining the legs is the demo for it.
- **Tube after tube:** refused.
- Joints are contacts (zero clearance over an area). No weld geometry; a weld list from the contacts is E3.

### 2.6 Same parts

Plates are the same part when their outlines are congruent: the loops' edge sequences (length, radius, turn) match up to
a cyclic shift, or reversed (a flat plate turned over is its own mirror image, so the placement stays proper). Tubes
are the same when size, length and both end planes in the tube's frame match up to a turn about the axis or end for
end. The part is written once; each instance gets the matching placement. On the stand: top ×1, foot ×4, leg ×4.

### 2.7 Exactness boundary

- **Exact:** every part in its own right (planes and cylinders; line, circle and ellipse edges in STEP; lines, arcs and
  circles in DXF), and every edge copied from an analytic design face. Interfaces are always copied: reproduced to 1e-7
  or reported.
- **Approximated:** only fitted plate edges (blends, ellipses, anything without a closed-form section), within the
  build tolerance, each fit error measured and listed.
- **Different by choice:** thickness, tube sizes, end cuts, the blends no part covers. These are what the verification
  measures; nothing is hidden in an export.

## 3. Output

- **Bundle:** the assembly of exact parts with every document in it: the design, the build document and one part per
  distinct plate or tube, components pinned by hash. Hand-made and generated assemblies are the same document (§16.5);
  a re-run after a design edit diffs with `DocumentDiff`.
- **STEP:** one AP214 file with assembly structure (existing writer), plus one per part on request.
- **DXF:** one per distinct plate (`top.dxf`, `foot.dxf`), from its sketch; quantity and thickness in the BOM.
- **Cut list:** per distinct tube: size, quantity, long-point and short-point length, each end's angle off square and
  the turn between the two cut planes, material. CSV and markdown.
- **Bill of materials:** part, kind, quantity, description (`Plate 12 S235 160 × 160`, `Tube Ø48.3 × 3.2 S235
  L 609.7`), mass each and in all. CSV and markdown.

## 4. Verification report

The bundle's instances (placed fields) are unioned into A and compared with the design D (the design body, not the
keep-outs). Every number names the faces it comes from.

| Check | How | Pass |
|---|---|---|
| Parts outside the design | certified maximum of D over A's solid, with its point and both faces; mean from A's mesh vertices by part face | ≤ tolerance |
| Design not covered | certified maximum of A over D's boundary, and mean, per design face (`leg/a`: 0.85 mm, the Ø50/Ø48.3 difference; `leg/blend`: a few mm) | reported, no limit |
| Volume and mass | V(D), V(A), V(A∖D), V(D∖A) from meshes of A, D and A∩D, with certified ranges; mass and centroid with the density; pump load with the stand's centroid | reported |
| Interference | `Clearances` over every pair; contact within the modeling tolerance is a joint, anything below is interference | no interference |
| Keep-outs | certified clearance of every instance to every keep-out body | ≥ its clearance |
| Interfaces | every mesh vertex of each interface face lies on A's boundary within 1e-6, on a part face of the same kind | all exact |
| Minimum wall | `Queries.MinimumWall` per distinct part | reported (and a limit if the build gives one) |
| Fits | fitted edges per plate, count and largest error | ≤ tolerance |
| Diagnostics | the design's, every part's, the assembly's, the build's (`build-face-missing`, `build-region-empty`, `build-tube-meets-tube`, `build-open-end`), export problems | listed, all of them |

The head of the report holds the design, build and bundle hashes and the settings, no date: the same inputs give the
same report byte for byte. Two forms: JSON for the agent, markdown for people (verdict line, BOM, checks table,
deviation per design face, per part, diagnostics). The playground shows it after the cut (P2): the design as a
translucent overlay, the assembly in the main view, the worst points as markers (as thin spots are now), the markdown
in a panel, the files to download.

## 5. Where it lives

- `src/Isoform.Document`: `Build.cs` (records) and `CanonicalFormat.Build.cs`; edits address `sections.<id>`.
- `src/Isoform.Geometry`: `ArcFit.cs` (tangent arcs through points, pure 2D).
- `src/Isoform.Modeling`: `Sections.cs` (slab mesh, slice, loops with owners), `SectionCurves.cs` (runs to exact or
  fitted curves), `Queries.Excess.cs`.
- `src/Isoform.Build` (new, on Modeling and Step): `IBuildMethod` with `PlateMethod` and `TubeMethod` (§12's one
  interface, two modules), `Rationaliser` (priority, hulls, bundle), `SameParts`, `Outputs` (STEP, DXF, cut list,
  BOM), `Verifier`, `Report`.
- MCP and chat: two tools, `build` (design + build document → bundle, files into a folder, report summary) and
  `verify` (design + any bundle → report), so hand-made assemblies can be checked too; `describe_format` gains the build
  document and the blend recipe.
- Tests in `tests/Isoform.Tests/Build/`; OCCT reads the stand's STEP back in `Isoform.Occt.Tests`.

## 6. What it needs from the kernel

- **Plane section with owners** (`Sections`): slab mesh and slice, Newton projection in the plane. New, small.
- **Section curves** (`SectionCurves`): owner runs to exact curves and corners; blend ends. New.
- **Tangent-arc fitting** (`ArcFit`). New, pure.
- **Excess** (`Queries.Excess`): certified [lower, upper] of the maximum of one field over another's solid or boundary,
  with the witness and its faces; branch and bound as in `Clearance`. New.
- **Shadow of a tube through a slab** (E1). New.
- Faster mass properties on large meshes, only if V2 measures it as the bottleneck.

## 7. Work breakdown

Each task is about half a day and owns its files.

**D: design and format** (`Examples`, `Isoform.Document`)
- **D1** The stand's design document as an example; test: no diagnostics at 1 mm, face names, keep-outs met, timing.
- **D2** Build document: records, canonical format, hash, edits (2 tasks).

**S: sections** (`Sections`, `SectionCurves`, `ArcFit`)
- **S1** Slab mesh, slice, projected loops with owners, below-resolution passed on.
- **S2** Runs to exact curves and corners; blend ends (3 tasks).
- **S3** Tangent-arc fitting with end tangents and measured error.
- **S4** Loops to a sketch: points, entities with ids from design faces, outer loops and holes.

**B: build** (`Isoform.Build`)
- **B1** Project, `IBuildMethod`, `Rationaliser` with priority, hulls and bundle output.
- **B2** Plate method: plane from a face or a plane, regions, own frame, part document.
- **B3** Tube method: axis, extent, end planes, part document, refusals.
- **B4** Same parts and placements.
- **B5** Outputs: STEP, DXF per plate, cut list, BOM.

**V: verification** (`Queries.Excess`, `Verifier`, `Report`)
- **V1** Excess, tested against analytic answers (3 tasks).
- **V2** Verifier: volumes, deviations by face, contacts and interference, keep-outs, interfaces, walls, fits,
  diagnostics (4 tasks).
- **V3** Report as JSON and markdown (2 tasks).

**M: tools and the demo** (`Isoform.Mcp`, `Isoform.Server`, `Isoform.Interactive`, tests)
- **M1** `build` and `verify` in MCP and chat, format guide (2 tasks).
- **M2** The demo as a test: BOM, cut list, report verdicts, the same bundle and report on a repeat; then the design
  edited (height 600 → 700, splay changed) and run again: every binding resolves, the diff names what changed (2 tasks).
- **M3** `research-plan.md` §14/§15 and a short demo write-up.

**P: playground** (after the cut)
- **P1** Build in the worker (`BuildJson`), the stand as an example bundle.
- **P2** Design overlay, deviation markers, report panel, downloads (4 tasks).

**E: extensions** (after the cut)
- **E1** Plates pierced by tubes: shadow through the slab and fit-up gap; a mid-height spider plate in the demo (4 tasks).
- **E2** Bent tubes along line-and-arc skeletons; bend data in the cut list (3 tasks).
- **E3** Weld list from the contacts (2 tasks).
- **E4** Plates that never leave the design: the intersection of the sections at both faces instead of the mid-plane (3 tasks).

**Estimate.** D 3, S 6, B 5, V 9, M 5 = **28 half-days to the minimal demo**; P 5 and E 12 more, **about 45 in all**.

**Cut line: the minimal demo** is D, S, B, V and M: the stand with top, feet and legs, built by MCP or chat, one STEP
assembly, two DXFs, cut list, BOM and the report, and a design edit re-run. Not in it: the playground view, pierced
plates, bent tubes, weld list. If it must be smaller still, V1 can start sampled (maxima from mesh vertices, then
certified `Distance` at the worst point) and be certified after: 25 half-days.

**Order**
1. D1 first: it fixes the numbers every later test checks.
2. Then three streams on different files: D2 and B1 (Document, Build), S1–S4 (Modeling, Geometry), V1 (Queries).
3. B2–B5 after S4; V2–V3 after B1 and V1.
4. M1–M2 last; M2 is the demo.

## Risks

- **Section topology where runs meet tangentially:** a blend's end is not where the mesh's owners switch; S2 must find
  it from the blend's own field, or the fitted run ends off its neighbour's tangent.
- **Thin slab meshing:** loops closer than a cell (a bore near an edge) must come through as reports, as they do in 3D;
  S1 tests that.
- **Time:** 2.5 s for the design at 1 mm natively (4.2 s at 0.5 mm); meshes of A∩D and the certified volume ranges add to it. V2
  measures and may drop the certified ranges to per-part.
- **Contacts:** every joint is an area contact, the costly case for `Clearances`; with nine instances it is a few dozen
  pairs, under budget, but it is read as contact only through the upper end.
- **Credibility:** an engineer will ask about welds, fit-up gaps and strength. The report says what it does not check.

## Decisions

1. **Demo part.** Recommended: the pump stand of §1 (tubes end only on plates, every part exact, three distinct
   parts). Alternative: a cantilever wall bracket, after E1.
2. **Fitted edges.** Recommended: tangent arcs, both sides of the true curve, `tolerance` in the build document
   (default 0.05 mm), error listed per edge. Alternative: exact ellipses in STEP for oblique holes; R12 DXF cannot carry
   them and a 2D cutter cuts square anyway.
3. **Where it lives.** Recommended: a new `Isoform.Build` project, the format in `Isoform.Document`, the three kernel
   pieces in Geometry and Modeling, and `verify` as its own tool that accepts any bundle.
4. **First cut without the playground.** Recommended: yes; the demo runs through MCP and chat, natively, and the
   playground view follows (P). Alternative: P inside the cut, five half-days more.
5. **Certified deviations in the first cut.** Recommended: yes (`Queries.Excess`): certified answers are what the
   kernel claims over the others. Alternative: sampled first, three half-days less.
