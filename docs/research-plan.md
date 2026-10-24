# Isoform: Geometry Core Research Plan

> Status: spike run (2026-10-20); results in section 11. Builds on `cad_geometry_engine_draft.md`.

## 1. Goal

Ship usable, manufacturing-grade mechanical CAD that humans and AI agents drive through the same
typed command API. This spike answers one question first:

**Does an implicit (F-rep/CSG) core give stable, CAD-meaningful references — and anything else B-rep
does not — while still producing exact output (STEP, drawings)?**

If not, the fallback is a B-rep (OCCT) product with the reference and versioning layer from this
spike on top. That layer is designed to survive either outcome.

## 2. Decisions

### Agreed

| Topic | Decision |
|---|---|
| Purpose | Ship usable CAD, not a research result |
| Exactness | STEP and exact drawings are required → a B-rep must exist, at least as a derived export |
| References | Faces, edges and vertices |
| Fillets | True constant-radius fillets are core |
| Approach | Verify the idea first; B-rep comparison is the stop/go test |
| Stack | C# / .NET 10 |
| Licence | MPL-2.0, open source |
| Units | mm |
| Modeling tolerance | `1e-7` mm (matches OCCT `Precision::Confusion`), configurable |
| Mesh tolerance | Separate chord tolerance, configurable; tested for convergence, not held to the modeling tolerance |
| Coplanar / split faces | Every face keeps a **source** identity and a **result** identity (section 4) |
| Versioning | Git-style: version the feature graph, never the geometry (section 5) |
| Versioning backend | Canonical text format that works with real git now and can back an in-app history later |

### Working hypotheses

- **H1 – References.** References that bind to the feature graph (source identities) resolve correctly after
  every edit in the suite, or report *split / gone / ambiguous* explicitly. They never silently rebind.
- **H2 – Advantage over B-rep.** On the same suite, the F-rep scheme beats an OCCT baseline that uses OCCT's
  operation history (`Modified` / `Generated` / `IsDeleted`), at least on the split and vanish cases.
- **H3 – Exact output.** From the CSG tree we get (a) a mesh whose measured error against the analytic
  surface converges below the chord tolerance, with sharp edges preserved, and (b) an OCCT B-rep rebuilt
  from the same tree, exported to STEP, whose faces map 1:1 back to the references.
- **H4 – Fillets.** Constant-radius fillets on plane–plane edges (90° and non-90°) and on a plane–cylinder edge
  (torus) deviate from the true rolling-ball surface by ≤ the modeling tolerance.
- **H5 – Determinism.** The same document gives the same content hash, the same result and the same reference
  IDs across runs (and across machines with the same pinned OCCT build).

## 3. Test part and edit suite

The base part is a plate (W × L × T), a hole (Ø D at x, y), an optional slot through one side, an optional
boss on top, and a fillet on one edge.

Before each edit, faces, edges and vertices are referenced, and each case has an expected outcome:

| # | Edit | Expected |
|---|---|---|
| E1 | Change W / L / T within valid values | Everything resolves |
| E2 | Move the hole inside the plate | Resolves |
| E3 | Hole touches a side face | Degenerate-contact diagnostic |
| E4 | Hole crosses a side | Side face: one source, two result pieces; hole wall partial |
| E5 | Hole fully off the plate | Hole wall *gone* |
| E6 | D > W | Body splits into two solids → diagnostic |
| E7 | Slot through a side | Side face: one source, two result pieces |
| E8 | Boss flush with top face | One result face, two sources |
| E9 | T ≤ 0 | Invalid-parameter diagnostic |
| E10 | Fillet radius larger than the neighbouring face | Fillet-invalid diagnostic |

The cases are stored as data files and run unchanged against both the F-rep core and the OCCT baseline.

## 4. Reference model

- **Source identity:** feature ID + primitive surface (e.g. `boss/top`, `hole/wall`). Stable across edits.
- **Result identity:** a connected piece that exists after regeneration. One result face can have several
  sources (merge); one source can produce several result pieces (split).
- **Edges** = pairs of faces, **vertices** = triples, both at source and result level.
- A reference binds to either level. A feature that consumes a source reference works on **all** its result
  pieces. Only a reference to one specific result piece can become *ambiguous*. That is reported, never guessed.

## 5. Versioning and determinism

- The document (feature graph + parameters) is the only thing stored. Geometry is regenerated, and caches
  are keyed by content hash.
- Canonical text format: fixed ordering, stable feature IDs (never list-index based), doubles written so they
  read back exactly. Same document → same bytes → same hash.
- Later, not in the spike: commits, branches, feature-level diff, and three-way merge. Merge has
  textual conflicts (same parameter) and semantic conflicts (a reference becomes *gone* / *ambiguous*).

## 6. OCCT baseline route

- A small C++ shim `native/Isoform.Occt`, written fresh for this repo, exporting only what the spike needs:
  box, cylinder, cut / fuse / common, fillet, **operation history**, face/edge queries, STEP write.
  C ABI: opaque handles, int return codes, a last-error string.
- Built with CMake against a local OCCT build, found through `OpenCASCADE_DIR` / `OCCT_ROOT`.
  No machine paths in the repo.
- Consumed from C# through `LibraryImport`. Optional: the core builds and tests without OCCT;
  OCCT tests skip when the native library is absent.
- The spike pins OCCT 8.0.0-rc2. Results only match across machines with the same OCCT build.
  Before shipping, move from the release candidate to the final 8.0.0.

## 7. Proposed layout

Solution layout: projects at top level, a `tests` folder, `Solution Items`.

```text
Isoform.sln
src/Isoform.Document      feature graph, canonical format, content hash, edits as data
src/Isoform.Geometry      primitives, CSG, field + ownership evaluation
src/Isoform.Meshing       feature-preserving dual contouring
src/Isoform.Modeling      regeneration, topology, references, diagnostics (backend-neutral API)
src/Isoform.Occt          OCCT baseline modeler, LibraryImport shim to native/Isoform.Occt
native/Isoform.Occt       C++ C-ABI shim over OCCT 8.0.0-rc2
tests/Isoform.Tests       xUnit + FluentAssertions 6.12; edit suite in Cases/edit-suite.json
```

Point/vector types come from the public `BasAutomation.Geometry` NuGet package (no submodule).
Its `==` is tolerance-based, so its types are never used as dictionary keys in the core.

## 8. Out of scope

Sketch solver, UI, performance beyond basic timings, patterns, assemblies, server/Linux builds, branch/merge.

## 8b. Open source

Isoform will be open source under **MPL-2.0** (`LICENSE`): changes to Isoform's own files stay open,
and closed-source applications can still be built on top. Consequences for the spike:

- No machine paths, private repo names or copied in-house code in the repo.
- Dependencies must be licence-compatible: OCCT (LGPL-2.1 + exception, dynamically linked),
  BasAutomation.Geometry (MIT), xUnit (Apache-2.0), FluentAssertions 6.12 (Apache-2.0; v8+ is not free
  for commercial use, so it stays on 6.x).
- The core must build and test without OCCT installed.


## 9. Stop / go

| Result | Meaning |
|---|---|
| H1–H5 all pass, H2 shows a clear advantage | **Go**: F-rep core, B-rep derived for export |
| H2 equal or worse | **Pivot**: build on OCCT B-rep; keep the reference + versioning layer |
| H4 fails for non-90° or plane–cylinder fillets | **Hybrid required**: fillets come from B-rep |
| H3b mapping not 1:1 | **Rethink export**: references would not survive into STEP |

## 10. Still open

- Own exact B-rep output from the CSG tree (replacing OCCT for STEP): candidate spike 3.

## 11. Results (2026-10-20)

Default settings: tolerance 1e-7 mm, cell size 0.5 mm. Core: 27 tests, OCCT baseline: 19 tests, all green.

| | Result | Evidence |
|---|---|---|
| H1 – References | **Pass.** All 11 edit cases resolve as expected, including split, merge, gone and ambiguous piece references. The base part's derived topology is exactly a B-rep's: 8 faces, 17 edges, 10 vertices. | `EditSuiteTests`, `FrepTopologyTests` |
| H2 – Advantage over B-rep | **Fail: no naming advantage.** On OCCT with history-based naming plus the same source/signature layer, every reference resolves identically (0 mismatches on either backend). Topology is identical in 10 of 11 cases. The only difference is E03 (hole tangent to a side): F-rep flags the zero-thickness contact; OCCT builds a pinched solid, splits the hole wall in two, reports it valid, and gives no warning. | `BackendComparisonTests`, `reference-comparison.md` in its output |
| H3a – Mesh | **Pass.** Watertight, outward, volume within 7e-5 of exact. Vertices lie on the exact surface to ~1e-15 mm. Chord error converges quadratically: 2.0e-2 / 5.4e-3 / 1.3e-3 mm at 0.5 / 0.25 / 0.125 mm cells, so 0.01 mm needs cells of about 0.35 mm around R2 features. | `MeshQualityTests` |
| H3b – Exact output | **Pass, via OCCT.** The same document rebuilt in OCCT exports to STEP, and its faces map 1:1 to the F-rep faces by sources and neighbour signatures. The exact output is a B-rep, so the B-rep has to be built anyway. | `BackendComparisonTests` |
| H4 – Fillets | **Pass for the supported pairs.** Fillet vertices lie within 7e-15 mm of the analytic rolling-ball surface at 90°, 60° and 120°, and on the hole-edge torus. OCCT's own fillet surfaces are the same cylinders and torus to 1e-9. Limits: exact only for plane–plane and plane ⊥ cylinder; only convex edges inside one intersection. | `FilletTests`, `FilletCrossCheckTests` |
| H5 – Determinism | **Pass.** Golden content hash; regeneration is bit-for-bit identical. | `DocumentTests`, `MeshQualityTests` |

### What this means

- **Naming robustness comes from the reference layer, not the representation.** Binding references to
  sources (feature + primitive surface) plus neighbour signatures, with explicit split / merged / gone / ambiguous,
  works the same on both backends. F-rep's ownership makes the sources cheap to compute; OCCT's history gives them too.
- **Exact output requires a B-rep anyway.** STEP came from OCCT. In a pure F-rep product the B-rep becomes a
  second, always-required representation, and keeping it consistent is the hard part.
- **Fillets are F-rep's weak spot beyond the simple cases.** Exactness here relied on closed-form pairs; general
  surface pairs, concave edges, variable radius and corner blends are routine for OCCT and open research for F-rep.
- **Where F-rep did better:** cheap point classification caught the degenerate tangent case that OCCT built
  silently. Analytic checks like that can also run in front of a B-rep kernel.

### Stop / go

By section 9, H2 equal means **pivot: build on OCCT B-rep and keep the reference + versioning layer.**
Everything backend-neutral carries over unchanged: `Isoform.Document` (canonical format, hash, edits),
the `Isoform.Modeling` API (topology, references, diagnostics), the edit suite, and the comparison harness.

## 12. Direction after spike 1 (agreed 2026-10-20)

Keep the F-rep core, for what B-rep cannot do. The longer-term product shape (brainstormed, not yet planned):

- **Design model (F-rep):** envelope, free-form blends, keep-outs, clearances, fields, and exact interfaces.
- **Sections with a build method:** machined, ribs / plates, frame / profiles, 3D printed, sheet metal, panels.
  Methods are modules behind one interface; the user or the AI assigns them per section.
- **Parts are exact:** plates, beams and machined solids are simple enough to output exactly (B-rep, DXF);
  printed parts go out as mesh / 3MF.
- **Verification loop:** the rationalized assembly is rebuilt as F-rep and compared with the design model.

Build methods come **after** a solid kernel. OCCT is only a test oracle from now on, never part of the core.

## 13. Kernel roadmap

| | Component | Measured by |
|---|---|---|
| K1 | Guarantees: 1-Lipschitz fields (distance never overestimated) and interval bounds of the field over a box | Property tests over random points and boxes: zero violations |
| K2 | Fast evaluation: tree compiled to a flat instruction list, batched, multithreaded, interval pruning | Points / second; regeneration time. Targets set after the first measurements |
| K3 | Adaptive, certified meshing and topology: octree near the surface only; no feature above a set size missed, smaller ones reported | Torture parts with thin walls and small holes |
| K4 | Breadth: 2D sketches → extrude / revolve, sphere / cone / torus, transforms, patterns, chamfer, smooth blend, shell / offset | Every operation passes the K1 property tests and the edit-suite principle |
| K5 | Queries: distance, closest point, ray hit, clearance, minimum wall, mass properties | Agreement with analytic answers |
| K6 | Exact boundary extraction for analytic faces → own B-rep / STEP | OCCT reads it back valid; edges and vertices within 1e-7 of OCCT's |
| K7 | Robustness torture suite against OCCT | Zero failures; OCCT's failure rate recorded |

Order: K1 → K2 → K3 → K4 → K5 → K6 → K7. The spike-1 reference layer, document format and edit suite stay;
every new operation must keep the edit suite green.

## 14. Kernel progress

Benchmark: `src/Isoform.Bench`, 20 logical cores. "20-op" is a 100×50×10 plate with 8 holes, slots, a pocket, tabs,
a boss, a chamfer and 3 fillets (60 surfaces). OCCT times are regenerate + tessellate at the same chord deflection.

**K1 — guarantees (done).** Every field is 1-Lipschitz (property test over random point pairs) and `Bounds(box)`
encloses every value in the box (random boxes, zero violations). Planes and axis-aligned cylinders have exact bounds.

**K2 — fast evaluation (done for now).** Interval-pruned octree, per-cell simplification of the tree, Illinois root
finding, allocation-free inner loops. Pruning is a pure speed-up: on uniform cells the mesh is bit-identical with and
without it (test). A compiled instruction list with SIMD is deferred: it matters for lattices and AI variant sweeps,
not for these parts.

**K3 — adaptive, certified meshing (done).** Octree dual contouring (Ju et al.): cells holding one plane stay coarse
at any size, one curved surface while chords stay within `ChordTolerance` (cell ≤ √(4rε)), everything else is refined
to `CellSize`. Polygons are dual to minimal edges, so the mesh has no cracks.

- *Guarantee:* walls, gaps and holes of at least 2 × `CellSize` are always resolved; smaller ones are either right
  or reported as `below-resolution` (holes, shells and fillets by size; thin walls and gaps by the mesher, which
  looks for opposing surfaces through one finest cell that do not meet within four cells). A surface no polygon
  carries is searched for a patch of the part it still bounds (`LostFaces`: branch-and-bound on the margin by which it
  decides every ∩/∪ on its path); a patch wider than `Tolerance` is reported, and `no-effect` needs a proof that none
  exists. Faces coincident within `Tolerance` (cut faces oriented as the part sees them) are one face.
- *Local refinement (2026-10-21):* a report with a place and a size is meshed again in a zone round it with cells
  small enough for it, down to `CellSize / 2^Refinement` (default 3: 1/8), up to three times while new reports appear;
  only what is smaller still is reported, with the resolved size where it is. The lattice is refined by a power of two
  so every coarse node is a node of the fine one: outside the zones the mesh is the unrefined one, and with no zones
  it is bit-identical. A finest cell beside a finer zone keeps one vertex (its neighbours may be smaller). The
  playground refines too, except drag previews (they have a time budget); result v2 says how far (`refinement`) and
  where (`refined`), and the cell it offers to resolve a report allows for it.
- *Torture suite* (236 cases at 0.5 mm cells: pocket walls in x/y/z, a hole beside a 30°/45° face, two holes, slot
  gaps, small holes, thin plates; 0.2–3 cells; four grid alignments): all 87 cases ≥ 2 cells exact (faces, edges,
  vertices, one body, watertight) with nothing reported. Below 2 cells: 91 correct, 58 wrong but reported, 0 silent.
- *Chord tolerance:* measured error ≤ 0.67 ε on coarse curved cells.
- On the 20-op part it found a real defect: hole h5 and the boss's y+ face touch tangentially (0.004 mm sliver).
- *Tangent junctions (2026-10-24):* where four faces meet tangentially (a fillet's contact line across a profile's
  line–arc tangent point) the owners have no common point. Their least-squares point sat in the sliver between two
  of them, where the field has a positive minimum, and Newton on the field rocked between the two, leaving vertices
  up to 8e-7 off. A cell vertex left off the solid now goes to the nearest point on it that one owner's own surface
  reaches within the cell; those vertices are on their surfaces within 1e-14 like the rest.

| 20-op, mesh + topology | K2 start | K2 end | K3 | OCCT |
|---|---|---|---|---|
| 0.5 mm cells, chord 0.02 mm | 0.8 s | 0.10 s | **0.07 s** | 0.22 s |
| 0.25 mm, chord 0.0054 mm | 2.8 s | 0.32 s | **0.19–0.23 s** | 0.26 s |
| 0.1 mm, chord 0.0013 mm | 18.7 s | 1.76 s | **0.63–0.69 s** | 0.23–0.26 s |
| Triangles at 0.1 mm | ~3.5 M | ~3.5 M | **785 k** | 5.2 k |

Triangle count (2026-10-23): octree cells are isotropic, so a hole wall at chord 0.0013 mm needs cells of 0.1 mm in
both directions where OCCT uses one strip along the axis. The display mesh is now the octree mesh simplified face by
face (`FaceSimplifier`; topology, diagnostics and export keep the octree mesh): planar faces triangulated from their
boundary loops, collinear vertices on plane–plane edges dropped from both faces, curved faces reduced by edge collapse
within the chord of their own surface; watertight, every triangle on its face, vertices the mesh's own. On 20-op
59.6 k → 3.7 k triangles at 0.5 mm (0.08 s), 172 k → 10.8 k at 0.25 mm (0.26 s), 785 k → 15.9 k at 0.1 mm (1.3 s;
OCCT 5.2 k); smooth blends and a shell 138 k → 14 k, gyroid in a skin 115 k → 66 k. Still open: boundary vertices stay
where the finest cells put them (the rest of the gap to OCCT), and strut lattices at 0.5 mm are meshed coarser than
the chord, so nothing there collapses (93 k → 92 k). Peak working set at 0.1 mm on 20-op is ~670 MB.

**K4 — breadth (done).** Every operation passes the K1 property tests (`FieldProperties`: 1-Lipschitz, Value =
Sample, bounds enclose, simplification bit-exact) and keeps its face names under parameter edits.

- *Sketches → extrude / revolve:* closed line-and-arc profiles, exact 2D signed distance. Revolved segments become
  planes, cylinders or cones, arcs spheres or tori; partial angles add two flat ends. Seven test parts have their
  exact volume and topology.
- *Placement and patterns:* every solid feature compiles under a rigid placement; linear, circular and mirror
  patterns name copy k of `hole/wall` as `row/k:hole/wall`. A mirrored part is symmetric to 1e-12.
- *Edge features:* chamfers (a plane, or a 45° cone on a hole or boss edge); fillets sharing a face meet in a mitre;
  fillets on swept edges where the profile lies behind the edge; a fillet after a shell rounds only the outside and
  is rejected when it would cut through the wall. Vertices lie on the exact surfaces to 1e-7. Where such a side runs on smoothly into an arc (2026-10-23), the
  fillet or chamfer follows the whole tangent run, as on a side the profile is not behind: a round of the side's own
  plane ran straight on past the tangent point (1.45 mm³ too much at r = 2 on a block with one R5 corner) and left the
  arc sharp, unreported. Lines, convex and concave arcs, arc to arc and cut rims match a brute-force rolling ball;
  a cap leaning across the sweep is refused.
- *Smooth blend and offset:* join or cut with a blend radius (quadratic smooth min, 1-Lipschitz); offset moves every
  face and rebuilds real offset surfaces, so later features see true geometry; grown corners get faces of their own.

- *Inside corners:* fillets that add material, exact rolling ball. Neighbouring profile edges get a tangent arc in
  the profile (convex or reflex; chamfers a straight cut), so outline corners and shaft steps are cylinders and tori.
  Inside a cut the tool's convex edge is rounded. Where two joined solids meet a filler is added, cut flush by both
  solids' other faces.

- *Loop fillets and chamfers:* one feature on every edge round a face. Between a flat cap and the exact side of a
  swept profile the rolling ball is exact for the whole loop (lines → cylinders, arcs → tori, chamfers → planes and
  cones), on boss tops, outlines, pocket floors and the rims of cut-outs. Loop faces are named after the face across
  the edge (`round/rib/a`).
- *Multi-body parts:* `op: new` starts a named body anywhere; features aim at a body or the current one; `combine`
  joins or cuts bodies. Each body is meshed on its own, so overlapping bodies stay separate solids.
- *Playground:* the kernel compiled to WebAssembly runs in the browser (`src/Isoform.Playground`): document editor,
  3D view with face names on hover, edge picking for fillets, diagnostics, example parts. One thread there, so
  0.3–2 s per regeneration against ~50–100 ms on the desktop.

Corners (2026-10-23): three plane–plane fillets of one radius at a convex corner meet in a sphere
(`<id>/corner:a+b+c`), and two inside fillets round a joined boss meet round its square edge in a horn torus
(`<id>/corner:a+b`, also where the two faces belong to different joined solids), both exact in STEP (a horn's apex,
like a sphere's pole, needs the reader's healing). Below its apex a horn is narrower than a cell, so the mesh splits
the apex in two; corners joined by a chain that are one point of the part (Gauss-Newton on all their surfaces, judged
by the field) are one vertex, so the topology counts what the exact B-rep does. An offset moves a horn with its boss:
the edge it rolls round stays a line, its apex stays. The horn is exact only for two planes at a right angle on a flat
base: elsewhere each filler's flush end is not where the ball turns the corner. Not yet: corners of unequal radii
(convex ones keep their mitre, `corner-mitred`), and inside corners at another angle, round a leaning edge, a curved
face or a curved base: they stay open with `corner-open` naming why. An inside fillet whose filler cannot end where its
edge does (a side running on smoothly into an arc or a round, a minor arc whose circle would cut it short, coplanar
start and end faces of a half revolve) is refused, as is rounding or chamfering a boss edge an inside fillet already
ends against. Fillets on cone, sphere and torus edges off a common axis (no closed form), chamfers
on them, and fillets next to a hollow torus. The OCCT baseline does not run the new features, so they are checked
against analytic answers only; cross-checking them is part of K7.

**K5 — queries (done).** On one body's field, checked against analytic answers (`QueryTests`):
- *Ray hit:* Lipschitz sphere tracing then bracketed root finding; point on the boundary to ~1e-12, face named.
- *Distance:* a certified interval [lower, upper] and the closest point; collapses to ≤ 4e-12 on the test cases.
- *Clearance and interference* between two solids: certified branch-and-bound; facing planes, coaxial pin and bore
  and convex shapes are exact at once; interference reports the penetration (−2 exactly for a 2 mm overlap).
- *Minimum wall:* inward ray from the surface, like a caliper; 0.8 mm web and 1.5 mm shell measured exactly; not
  certified (a spot narrower than the mesh polygons can fall between seeds). On the 20-op part it found a real
  knife edge where `cut3/x+` runs through hole `h6`.
- *Mass properties:* volume, area, centroid, inertia from the mesh with curvature correction (errors 1e-10 to
  5e-7), plus a certified volume range.
- *Excess (2026-10-24, demo plan V1):* the largest value of one field over another's solid (`Queries.Excess`: how
  far parts stray outside a design) or over the boundary points a face owns (`BoundaryExcess`, `ExcessByFace`: how
  far each design face is from the parts), certified [lower, upper] with the witness point and both faces, and a flag
  when the budget stopped it first. Branch and bound, highest box first: the upper end from the field's structure
  over the convex shapes the region lies in (a plane's highest point over planes, balls and rods by duality; a
  cylinder within a rod round a near-coaxial axis; a sphere round a ball), the lower end from points of the region
  climbed up the gradient. Faces a constant distance apart settle in one box: a Ø48.3 tube in a Ø50 leg reads −0.85
  outside and 0.85 uncovered exactly, as do a box poking 1 mm out, a tilted plate's top edge and an offset ball. Face
  ownership follows the tree (ties to the first child, a flush plane cut never shows a blend's rim), blends get bounds
  that close on their face (the plain ones stay k/4 wide), and a smooth union's face keeps both sides within k. A
  round peak still closes only to first order (1e-4 on a ball in 0.6 s). Stand-sized: a Ø50 × 600 leg blended
  (k = 10) into a foot and a top, against a placed Ø48.3 × 3.2 tube cut by two planes: outside 50 ms, all 16 design
  faces 0.7 s at 1e-3, the leg's blend 10.85 (its rim on the foot, k + 0.85 from the tube).
  *Sketched plates (2026-10-24, after V2):* on the stand as the plate and tube methods build it the verdict was
  unknown (hole walls and the fitted top never closed; 68 s at 100 000 boxes). Now: a hole loop of a sketch is a
  bound like a hole feature's wall (a round one holds the solid outside its cylinder; a convex one is never above
  the complements of its sides), so walls, a pin in a bore and the hose in the top read exact in one box. Each box is
  cut to a polytope that holds the region (`Queries.RegionHigh`): planes, tangent planes of rods and balls, outside
  a cylinder the chord the box's width allows (a sag of w²/2r), and a blend's tangent plane moved out by MR²/2 (M its
  curvature plus 2/k): a solid below a blend, a blend face in a slab round it. Over that a convex field is highest at
  a corner (exact), a concave one under its tangent plane, a smooth union under the tangent plane of S(a, b) (concave
  and rising in a and b), a union of two convex parts under their best mix: a fitted outline on a blend closes to
  second order. A box splits only across what changes (an intersection of planes and of planes, cylinders and
  profiles along an axis, a union only if all of it is along it), so a plate against a design extruded the same way
  splits in its plane; there an extrude's bounds are taken in its profile's coordinates (in the queries: the tape
  stays bit-identical to `Field.Bounds`), and witnesses start from the polytope's middle, then its highest corner.
  Several faces at once are taken one by one (each face's own surfaces held its points; intersected over several
  faces they were not sound). The built stand passes with every check settled: the top outside [0.0235, 0.0245] (the
  fit stands out of the blend), feet 0, hose [10.000, 10.001] (was [3.2, 10]), the hole interfaces 0, every design
  face settled (leg blend 2.943, lobe blend 0.006). Verify 16–19 s at 20 000 or 100 000 boxes (outside 0.6–0.9 s,
  uncovered 3.9–5.1 s, keep-outs 0.00 s, interfaces 0.6–1.0 s; was 32 s at 20 000 with 2.1, 9.6, 2.1 and 3.2 s, and
  68 s at 100 000), the rest being the assembly's meshes, volumes and walls. Checked against analytic
  answers (a sketched plate's Ø20 hole on a hole feature, a Ø24 one 2 off it, a Ø16 pin 2 from it; the fitted plate
  against the design's field along its arcs; the chord exact across a hole's wall) and a property test of the box
  bound over random boxes (K1).

**K6 spike — exact B-rep / STEP (works for the analytic case).** `src/Isoform.Step` writes STEP from Isoform's own
exact B-rep; OCCT reads every one of 15 parts back as one valid solid with Isoform's face/edge/vertex counts, volume
equal to the analytic value to ≤ 3e-16 (one oblique-cut part 1.8e-7: the file has no 2D p-curves yet), and vertices
and edges within 1e-13 of OCCT's own model. Covered: planes, cylinders, cones, spheres, tori, plane–plane and
plane–cylinder fillets, tangent edges, seams. Not yet: loop fillets (mapping work), shell voids, p-curves; smooth
blends and general fillets have no closed form and would need approximated surfaces: the exactness boundary of
the implicit approach.

**Assemblies, steps one and two (done).** Assembly documents with placed instances (one regeneration per distinct
part), occurrence names, pinned hashes, sub-assemblies, bundles; mates (mate, flush, axis) solved by damped
Gauss-Newton with the free degrees of freedom counted; clearance and interference between instances.

**K7 — robustness against OCCT (2026-10-21).** Seeded random documents (`RobustnessTests`) built on both backends
and judged against a certified volume and an F-rep run at a quarter of the cell size. The generator is adversarial:
faces snap to existing ones plus a nudge of 0 to 1 mm, so coincident faces, slivers and near-tangent holes are common.
On 150 seeds at 0.5 mm cells: F-rep 0 silent failures (OCCT 1: a volume outside the certified range), 0 crashes.
In 46% of the documents F-rep refuses where OCCT builds; over 1000 seeds every refusal that names a size names one below the
resolved size of 1 mm (82 under 1e-5 mm, 57 under 0.01, 129 under 0.25, 114 under 1), so none is a false refusal,
but about half are features a finer local mesh would build.

- *False edges:* where a piece of face is narrower than a cell (a step between parallel faces, a corner a cut just
  clears), the mesh could join its neighbours with an edge they do not have. Every edge now needs a point near its
  chain on both surfaces and the boundary (Newton from the chain's vertices), or branch-and-bound proves there is
  none and the edge is reported (`FalseEdges`). This turned 16 silent results in 1000 seeds into reports and flagged
  no part both backends agree on.
- *Contacts along an edge or at a vertex:* a plane cut through the part's corner edge has a margin of zero all along
  it, which plain branch-and-bound cannot clear. Planes nested in the siblings (and the two planes of a rounded
  edge, which only removes material) now count, and two or three planes whose gaps slope against each other on the
  surface cap the margin by their weighted average, so such a cut is proven `no-effect` (the bench's chamfer).
- *False vertices:* a vertex is real only if its faces' surfaces have a point in common on the part (Gauss-Newton
  on the surfaces, fillets included). Two fillets that just touch at a corner, or a face a cut leaves as a small
  triangle, collapse into one vertex that fails this, and are reported.
- *Local refinement* (K3) on 1000 more seeds (150–1149): F-rep refuses 38.5% where OCCT builds, against 48.2%
  unrefined, and both build and agree 30.1% against 22.9%. Mean regeneration 57 ms against 29 (median 33 against 25).
  Silent: 0.9% refined against 0.4% unrefined. The difference is not new error: in each case the unrefined run
  reported something else nearby, which hid a feature smaller than the finest cell that even uniform 1/8 cells mesh
  wrong without a report. Those gaps (sub-cell steps of 1e-6 to 0.1 mm against another face) are next.
- *Closing them (2026-10-21):* a false vertex now needs a residual of one tolerance, not ten (blocks overlapping by
  1e-6 mm); a face the mesh carries is searched for lost pieces too, in blocks of 8 cells along every edge and round
  every vertex where three surfaces meet, away from its own polygons (a hole wall's sliver where it clips a corner);
  pieces of a body that touch within the tolerance are reported (`PieceContacts`); a face cut off at a sharp tip is
  joined back to its face, with its edges and corners, where the way between them is on the part's boundary and,
  for edges, both faces decide the field round it. The K7 judge no longer blames F-rep for faces OCCT's history
  lost the names of. Result on seeds 150–1149: silent 0 (was 9), both build and agree 30.3%; on 1000 fresh seeds
  (1150–2149) silent 0.1%, against OCCT's 1.8% failures there (a crash, two invalid shapes). The one left: a body
  0.007 mm thick at a clipped corner, far from every edge the mesh made. Cost: about 5–9 ms on a 30 ms random part
  (Release), more on large meshes, mostly the polygon index the piece search queries.
- *The whole box (2026-10-23):* lost pieces are also looked for away from the mesh's edges, in a descent from the
  body's box to the same blocks that drops a box the bounds prove empty or full, or where fewer than three distinct
  surfaces may bound the part. The sliver body is reported (a patch 0.01 mm across, too small for refinement), and
  so is the same sliver at the plate's other corners. Result: silent 0 on both 150–1149 and 1150–2149; no other
  document changed (both build and agree 30.3% and 31.6%). Cost: about 0.5 ms on a random part, 4–8 ms on the 20-op
  part at 0.5 to 0.1 mm cells. Still missed then: a lost body bounded by two surfaces (a ball's cap cut off flush
  with another face), and a sliver within 2.5 mm of its own faces' polygons.
- *Cells without a vertex (2026-10-23):* a cell round no crossing is searched for the other sign than its corners'
  (best-first branch-and-bound on its field, 32 boxes, halving the side that bounds best; two opposite planes in one
  ∩ or ∪ bound it by their mean, so zero-thickness contacts end at once). The deepest point found is a pocket in the
  region result (cache format 3). Polygons part the grid's nodes as the field does, so where no polygon comes nearer
  than the cell's nearest corner the mesh is on the corner's side all round: the material or gap there, twice the
  depth across, is lost and reported, unless it reaches the mesh further on (see lattices, 2026-10-24). Both skipped repros
  and variants (other corners, smaller holes, the cap off the grid's lines) are reported; pockets beside polygons
  (the wedges where a hole runs out through a side) are not. And a cell beside a fillet run's cusp, where no vertex
  fit its crossings' surfaces, kept their mean up to 0.1 mm off the surface: it now takes a real edge of two of its
  surfaces, so every vertex there is on the surface. Result: silent 0 on both 150–1149 and 1150–2149, no document
  newly refused; one refused document now built and agreeing (both build and agree 30.3% and 31.7%). Cost: the
  search is 1–4 ms of the 20-op part's 0.1–0.6 s mesh at 0.5 to 0.1 mm cells and about 0.3 ms on a random part;
  end to end within the noise.
- *A body that touches itself (2026-10-23):* a corner of a piece on one of its own edges, away from the edge's ends
  and where the edge's surfaces are not both the corner's, is a point contact of zero thickness and is reported
  (`SelfContacts`, tangent-contact), as the exact B-rep refuses it. The corner is put onto its surfaces by Newton
  steps, so only a contact within the tolerance counts. It turned the one silent case known (a sweep's bend running
  through the corner where it starts) into a report; on 3000 random documents (F-rep only and common, seeds 0–1499)
  it reports nothing. Cost: about 2 ms on the 20-op part. Still silent: a corner on a face's inside, and two edges
  that cross in a point.
  *Generalised (2026-10-24):* the points tried are every corner and every pair of edges of a piece whose chains come
  within two cells (Gauss-Newton on their three or four surfaces). At a point on them within the tolerance, every
  surface through it stands for its tangent plane: the planes' great circles cut the sphere of directions into cells,
  each probed once in the field 1e-4 mm out (from beside every crossing of two circles, so none is missed), and cells
  of one kind that share an arc are one piece. Two pieces of material, or of space, that meet only in the point are a
  contact. No heuristics about split faces, tangent junctions, merged coincident faces or edge ends are needed; it says
  nothing where a blend or lattice, or a surface not through the point, comes within 2e-4 mm, or two planes are within
  about 1° without being one. Repros (`SelfContactTests`; each moved 0.05 mm either way is not reported): a cube on its
  corner touching a flat face, an edge, the same edge 0.6 mm from its end (silent before), a fillet's face (its
  distance from its field), and two edges crossing. The exact B-rep now also refuses a corner inside another face of
  its solid (winding number in the face's parameters) and two edges that cross (Gauss-Newton on the curves'
  parameters), naming the same point; OCCT reads all three back as valid solids. Seeds 0–2149, both sets: nine new
  reports, all line contacts (two blocks meeting edge to edge, two of them within 1e-7 mm), silent before; nothing
  else changed. K7 labels: 158 and 1812 add tangent-contact to a below-resolution refusal, 945 is refused for the
  contact instead, and 639 (two blocks meeting edge to edge, F-rep built, "OCCT topology differs") is now refused as a
  tangent-contact. Cost: 5–10 ms of the 20-op part's 0.3–1.4 s at 0.5 to 0.1 mm cells (was 2–4 ms). Still silent: an
  edge touching a face in a point (a circle's edge on a plane), smooth surfaces touching (a ball on a plate, two
  cylinders along a line), contacts on blends and lattices; the exact B-rep still writes a line contact (639, 168:
  the topology ends both edges at the contact's corner); and the crossing edges run 0.05 mm into each other lose the
  overlap with nothing reported (below the cells).
- *Lattice channels (2026-10-23):* where a TPMS sheet comes within a cell of its skin's inner face, facing it, the
  void between them is a channel: at a saddle of the wall's depth on the sheet a lens 0.07–0.09 mm thick and about
  1 mm wide joining two voids (gyroid and diamond of 6 mm cells in a 1.5 mm skin 0.0852 mm, Schwarz P of 10 mm 0.0843,
  of 8 mm in a 1.2 mm skin 0.0674). 0.5 mm cells fill it in, and neither the lattice's sizes nor the thin-spot search
  (which takes the saddle's sides for short wedges) saw it. `LatticeChannels` now finds these critical points once per
  regeneration (seeds in boxes of an eighth of the lattice cell where the depth may be below 2 cells and the sheet may
  pass; Newton on the sheet's value and the tangential difference of the two normals from those whose sheet faces the
  wall within 60°; void all the way across), and each pass reports those no polygon crosses the line across on both
  sides of the middle, with the cell that resolves them, and refines round them. Channels the cells carry are not
  reported: at 0.5 mm cells every one from 0.585 mm up was carried, every one below 0.09 mm filled in; one more
  refinement level meshes the 0.085 mm ones right. Pockets that a lattice and another face bound (the sheet cutting a
  sliver off an edge of the cavity, 0.01–0.07 mm) are now reported too; the bracket example's skin went from 1.5 to
  1.6 mm, where it has none. Cost: the search 6–35 ms on a 20 mm cube and 40–60 ms on the bracket (1–3% of the
  regeneration), the mesh check under 10 ms a pass. Channels against a region and over the cavity's edges: below.
- *What lattices still left silent (2026-10-24):* the wall a sheet faces is now the part with the sheets taken out
  (+∞ for them in the tree, so a skin's child becomes the solid and a region's complement stays): a skin's inner faces,
  a region's faces with the solid beyond, or both. A region from 1.5 to 18.5 mm with no skin leaves the skin repro's
  0.0852 mm channels, now reported as "against its region" and resolved by one more level; a region that is the body
  leaves none (nothing is beyond its faces: the sheet's point must be on the part's boundary and the wall material).
  Where two faces of the wall meet the depth has a crease, and a channel over it is narrowest on the crease: Newton on
  the sheet's value, the two faces' depth difference and the sheet's normal in the plane of theirs, from each seed and
  each pair of the depth's pieces near it (∩/∪ children, a shell's inside, a profile's edges), kept where the sheet
  faces in between. A Schwarz P of 6 mm cells in an 11 mm box passes 0.0445 mm from two inner faces at once; only the
  lattice's own gap was reported, now the channel is. Over about 220 shifted boxes and brackets (gyroid, diamond, Schwarz P)
  crease channels of 0.004–0.3 mm were found, the cells filling those below about 0.25 mm, but no document was silent
  before: each had another report (often the pocket search, which takes a crease channel for a gap lost whole); those
  the cells carry are not reported (0.316 mm over a 7 mm box's edges). A corner of the cavity, three faces, is a dome
  the sheet seals, not a channel: the pocket search's. Lost pockets inside a strut lattice's junctions (the bcc of the
  format guide: 117 of them, 0.035 and 0.134 mm across) are real void, the field being the exact distance to the
  struts: the thin end of the crevice where two struts meet at 70.5°. They are not lost whole: up the field's slope
  from the pocket, in steps of half the depth, the gap reaches a point deeper than its distance to the polygons within
  0.15–0.27 mm, so the mesh carries the crevice and lost only its tip, as at any edge. Every pocket now gets this test
  instead of the lattice's exemption; the sliver bodies, the ball's cap and the sheet's sliver at the cavity's edge
  never reach the mesh on walks of 1–10 mm, and stay reported; the format guide's bcc is clean as it was. Lattice
  regenerations got 1.6–3.6× faster (the gyroid cube 4.2 → 1.2 s, the bracket 2.9 → 1.8 s, the diamond cylinder 1.8 →
  0.5 s, the bcc example 1.4 → 0.4 s): the old code named the surfaces round every lattice pocket before dropping it.
  The channel search is 80–130 ms on the gyroid cube and the bracket (from 35–45; 10–20 ms of it the creases), 15–20 ms
  on the cylinder; 20-op unchanged (0.5/0.6/1.4–1.5 s at 0.5/0.25/0.1 mm, no lattice). The pocket search's budget of 32
  boxes is not sound and cannot be made so with these bounds: it runs out in 70% of the cells without a vertex in the
  smooth-blend example, and in a near-tangent sphere-on-plane cell the bound stays at 0.09 mm after 200,000 boxes, so a
  "not searched fully" report would fire on most blended parts. Measured instead: on 600 random documents, 13 example
  parts and 50 slivers and caps 0.1–10 µm thick at five grid offsets, 2000 boxes changed no result from silent to
  reported or back (in 8 only the pocket or size named); in the lattice examples 163 pockets of 1.6–2.9 µm depth are
  missed by 32 boxes and found by 5000, none lost whole. A thin piece lost whole spans several cells, and the deepest
  find in any of them reports it. Still silent: a pocket small in every direction, thinner than a cell's bound can
  resolve in 32 boxes, alone in its cell; a crease inside a field the search does not take apart (a blend, a sweep's section).
- *Seeds 0–2149, both sets, F-rep side (2026-10-24):* three crashes. Two (F-rep only 1495, 1867) were a surface
  component the cells flattened to nothing: its enclosed volume, −1.5e-15, made it a void, no body's box held it,
  and it got a body number past the last (`PieceContacts` indexed by it). A surface no body holds is now a body of its
  own. The third (1771) is the mesher's Newton projection dividing by a zero gradient where a blend's two gradients
  cancel; it now stops where the gradient vanishes. And a false vertex passed as real where a face's surface runs on
  past the face: a side wall's plane past its corner crossing a knob's edge, where an overhang narrows to 0.12 mm (1259, at 0.25 mm cells), or a block's
  side past its end on a cone it stands 0.007 mm proud of (1673). A vertex is now also false where a face's surfaces
  are gone from the field simplified round it (plain analytic faces meeting at more than 6°). Of the 4300 documents
  this changed one result, 1673, from silent to reported.

**Speed on a large blended part (2026-10-24).** The demo's pump stand (`Examples.PumpStand`: nine blends in nested
chains, four oblique legs, four keep-out bodies; 1.1 M polygons at 1 mm) took 6.6–6.8 s at 1 mm cells and 11.4–11.8 s
at 0.5 mm, `MassProperties.Of` on its mesh 32 s and 58 s. Profiled (Release, stage timers and sampling): at 1 mm half
the time went to the lost-piece search, the rest to meshing (about 1 M surface cells for the stand alone, 40% of them
without a vertex at 0.5 mm) and serial topology; mass properties sampled the whole tree three times per triangle on
one thread. Four changes, none loosening a guarantee:
- The piece search skips a surface whose path ends in a blend, a profile or another node: only a leaf gives a witness,
  so those searches could only end without a face (85% of the search time). Its blocks are searched in parallel and
  their faces taken in block order, as before. Mesh and diagnostics unchanged.
- A blend's bounds are the blend of its children's bounds: the smooth min and max rise with both arguments, so their
  values at the intervals' ends bound them, where the bounds were k/4 wide wherever the blend was active and no cell
  within k/4 of a blend was ever certified. Strictly tighter, so the octree round blends is smaller (cache format 5)
  and the mesh changes where a cell now simplifies to one patch (1,126,197 → 1,122,583 polygons on the stand). The
  pocket search, which runs on these bounds, now finds a cusp that a shell thinner than half a blend's width leaves in
  its cavity (0.05 mm, 0.12 mm long, in the smooth-blends example, silent before); that example's shell went from 1.5
  to 2 mm, where it has none.
- Bodies are meshed and given topology at once, so the keep-outs and the stand's serial topology overlap.
- `MassProperties.Of` sums runs of 4096 triangles in parallel, each with the field simplified round it (exact there),
  and adds the runs in order: deterministic, the same per-triangle terms.

Result: the stand 2.5–2.6 s at 1 mm and 4.2 s at 0.5 mm, mass properties 1.2–1.4 s and 2 s. Nothing else slower
(interleaved runs on a loaded machine, best of 3): 20-op 0.45–0.6 → 0.36–0.41 s at 0.5 mm, 0.51–0.56 → 0.48–0.6 s at
0.25 mm; the examples the same within the noise (±40 ms) except smooth blends 0.52 → 0.21 s, the pipe bend 0.9–1.0 →
0.32 s (a sweep's faces end in a sweep node: futile searches) and diamond infill 0.39 → 0.30 s. Not done: the meshing itself (about 2 s of the 2.5), whose cell count is set by the chord tolerance on the
legs and the keep-outs; exact bounds for oblique cylinders were tried and gained nothing measurable.

**Build methods and the end-to-end demo (2026-10-24, `demo-plan.md`, write-up in `demo.md`).** `Isoform.Build`
rationalises a design into exact plates and tubes by a build document (`isoform-build/0`: sections bound by face name
in priority order, material, tolerance, keep-out bodies, interfaces), packs them as a bundle (one document per distinct
part, instances pinned by hash), writes STEP, DXF, a cut list and a bill of materials, and verifies the bundle against
the design in a report (JSON and markdown, the same bytes for the same inputs). `build` and `verify` are MCP tools
and chat tools, native only. On the pump stand (`Examples.PumpStand`, now parametric in `height` and `splay`): three
parts in nine instances, 23.79 kg; legs Ø48.3 × 3.2 at 609.7/583.6 mm, ends 15.1°; the top's eight blend runs
fitted by 48 arcs within 0.026 mm. The report proves the contacts (8, no interference), the fits, the pump and socket
keep-outs, every interface's surface match and the legs' −0.85 mm, and, with the bounds for sketched plates (the
Excess entry), the plates inside the design within 0.023 mm, the hose 10 mm clear and every hole covered: verdict
pass, every bound certified at 20,000 boxes. Height 600 → 700 and splay 110 → 140 through `apply_edits`: every name still binds, top and feet
unchanged, legs 718.5/690.3 mm at 16.3°, and `DocumentDiff` between the bundles names exactly that. Time on a loaded
desktop at 1 mm: about 24 s for the build tool, of which 16 s verification (assembly meshing, volumes, minimum walls)
and 3.5 s regenerating the design.

**Review.** An independent read of the K4 code found 15 problems (wrong geometry without a diagnostic in several);
all fixed, each with a regression test (`ReviewFindingTests`).

## 15. Where this stands (2026-10-20)

**Is it adding something?** The pieces exist elsewhere: implicit kernels (nTop, libfive, Fidget) never fail a
boolean but have no faces or exact output; B-rep kernels (Parasolid, ACIS, OCCT) are exact but fail or silently go
wrong on booleans and fillets, and their face naming is brittle; text CAD for agents exists (CadQuery on OCCT, Zoo's
KCL). What we have not seen together is: operations that cannot fail silently (certified, or a machine-readable
reason), every face with a stable name through edits, cheap robust field queries, a canonical hashable document an
agent can edit and verify, and exact output, as an open-source .NET kernel.

**Risks and what follows from them:**
1. *Exactness rests on K6.* Today the output is a mesh. A thin K6 spike (planar and cylindrical parts → STEP, OCCT
   reads it back valid) comes right after K5, not at the end.
2. *Breadth.* Mature kernels have decades of breadth; ours follows what the build methods of section 12 need.
3. *Proof by demo.* One end-to-end case should show the point: a frame or bracket design model rationalised into
   plates and tubes, exported as exact STEP, with a verification report. *Done (2026-10-24):* the pump stand of
   `demo.md`, run by MCP or chat, with a design edit re-run; verdict pass, every bound certified.
4. *Oracles.* New operations are checked against analytic answers; K7 adds OCCT cross-checks.

## 16. Assemblies (planned)

A layer above the kernel, built on what exists: canonical hashed documents, stable face names, rigid frames.

1. *Assembly document* (`isoform-assembly/0`): components with stable instance ids, a part (path + content hash, so
   regeneration is reproducible and the assembly's hash covers its parts), a placement frame, or a sub-assembly.
2. *Geometry without merging:* an instance is its part's field seen through the inverse placement (exact and
   1-Lipschitz under rigid motion). Identical parts share one compiled field and mesh, cached by hash. Faces get
   occurrence paths: `bracket-left/plate/z+`.
3. *Mates by name:* "`shaft/big` concentric with `bracket-left/bore/wall`", "`plate/z-` flush on `base/z+`".
   Because names survive edits, mates do too; analytic faces keep the rigid-body solver small. Joints (rigid,
   rotational, slider) fit the same scheme.
4. *Queries across instances:* interference, clearance, mass properties, bill of materials (K5 applied per pair).
5. *Generated assemblies:* the build methods of section 12 output an assembly of exact parts; the verification loop
   unions its instances and compares them with the design model. Hand-made and generated assemblies are the same
   document.

Before that: part parameters (one bolt document, several lengths) and exact STEP with assembly structure (K6).
Order: K5 → K6 spike → assembly document and placed instances with interference/clearance → mates → parameters.
