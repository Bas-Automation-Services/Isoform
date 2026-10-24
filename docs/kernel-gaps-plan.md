# Kernel gaps: plan

Goal: close the four gaps left after K1–K7 and the sketching milestone, before the end-to-end demo of
`research-plan.md` §15.3: the last silent failure, the display triangle count, the missing fillets, and the missing
sketch tools. Everything stays inside the exactness boundary; what would cross it is named and deferred.

Decisions (2026-10-23): these four come before the demo. Plan first.

## Status (2026-10-24)

**Done:** R1–R4, T1–T6, X1, F1–F11, S1–S12 (details in `research-plan.md` §14). Merged after them on 2026-10-23/24:
a body touching itself in a point is reported (`SelfContacts`), corners are checked on their faces' own surfaces
(false vertices), lattice channels against a skin are found and refined (`LatticeChannels`), a horn torus's split apex
is one vertex, fillets and chamfers follow a side's whole tangent run, and the crashes of seeds 0–2149 are fixed.
Then sweeps: they can be patterned (copies `row/k:pipe/…`, exact in STEP) and be a lattice's region; a fillet or
chamfer on a cap edge is exact (also where a short first or last leg puts the bend's term over the cap), one on a
profile corner rounds the whole path (cylinders along lines, tori round arcs), and an inside fillet where a straight
piece meets a face across it is exact. Every other sweep edge is refused: along the path against another face, a cap
or face across a bend, the rim of a swept cut.

**Open:**
- S13, the tapered extrude (optional).
- §5, approximated faces: its own milestone, after the demo.
- Known silent gaps: a corner touching another face's inside, and two edges crossing in a point (`SelfContacts` sees
  only a corner on an edge); lattice channels without a skin (against a region's faces) or at the cavity's edges and
  corners; pockets in the junctions of strut lattices.

## Findings that shape the plan

- **Silent failure, seed in `RobustnessReproTests`:** a 0.012 × 0.009 mm sliver body at a plate corner just outside
  a hole. No grid node or edge touches it, so the mesh has nothing there; every check (`FindThin`, `LostFaces`,
  the piece search, `FalseEdges`, `PieceContacts`) starts from something the mesh made, and the nearest edge is
  5.8 mm away. Worked out by hand at 0.5 mm cells; the first task confirms it with a run.
- **Triangle count is an output problem, solved on the mesh, not through a B-rep.** Isoform is not a B-rep kernel;
  tessellating `ExactBrep` would only help the analytic parts (which leave as STEP anyway), not the blend and lattice
  parts that leave as mesh, and would make display depend on the most fragile code. Topology, diagnostics,
  `ExactBrep` and `MinimumWall` keep the octree mesh as it is; only `MeshBuffers.Of` (playground, server reply), the
  bench count and a later mesh export take a simplified copy.
- **The mesh already knows enough to simplify itself.** Every polygon carries its surface (`Quad.Owner`) and its face
  (`FaceOfQuad`); every surface has a kind (`SurfaceKind.Plane`, …) and an exact distance (`Surface.Distance`, and
  `SampleSurface` for fillets, blends and lattices). Vertices lie on their surfaces to ~1e-15, so on a planar face
  the interior vertices carry no information at all.
- **Fillets stop at `EdgeSamples`** (`Compiler.cs:1184`), which accepts plane–plane and plane ⊥ cylinder only. Coaxial
  revolved pairs are lines and circles in the (ρ, z) half-plane, so their fillets are tori: exact, and `ExactBrep.Rolled`
  already writes them. Three equal-radius plane–plane fillets at a vertex meet in a sphere. Unequal radii and
  non-coaxial pairs have no closed form.
- **Corners mitre** because each fillet is its own child of the host intersection (`Compiler.cs:764`).
- **A shell has no offset** (`OffsetOf` returns null, `Compiler.cs:462`); `ShellField(OffsetOf(child, d), t + 2d, …)`
  is the whole fix.
- **Sketch fillet and trim are document edits** on lines and arcs: no new geometry, all of K1 and K6 hold.
- **Closed-form surface intersections live in `Isoform.Step`**, above `Isoform.Modeling`. Both coaxial fillets and
  projected sketch edges need them in the compiler: one shared move to `Isoform.Geometry`.
- **A flat line-and-arc sweep is exact:** lines sweep to planes and cylinders, arcs to cones, tori and spheres, joined
  by a min of pieces cut by their end planes. A tapered extrude (lines → planes, arcs → cones) covers much of loft.

## 1. Last silent failure (R)

Seed the lost-piece search from the whole body box, not only from mesh edges: a pruned descent to 8-cell blocks,
dropping a box when `Bounds` proves it empty or full or when fewer than 3 surfaces straddle zero after `Simplify`.
Merge with the edge-seeded blocks. The verdict rule is unchanged (a witness wider than the tolerance, away from the
surface's own polygons), so no new refusals beyond places it already judges.

Not covered, written down as a skipped repro: a lost body bounded by two surfaces, and a sliver within 2.5 mm of its
own surfaces' polygons. Those need a mesher-side check on vertex-less cells (a `RegionMesh` change): later.

## 2. Face simplification (T)

A pass in `Isoform.Meshing` over the finished octree mesh, after every diagnostic has run: `Simplify(mesh, faceOfQuad,
surfaces, chord) → (mesh, faceOfTriangle)`. Each face is simplified on its own with its boundary shared with its
neighbours, so the result stays watertight and every triangle keeps its face name. The field is the only judge.

1. **Planar faces, zero error:** drop the interior vertices and triangulate the face's boundary loops in its plane
   (holes included).
2. **Straight edges between two planar faces, zero error:** drop the collinear vertices along them from both faces at
   once.
3. **Curved faces, bounded error:** edge collapse inside the face, boundary locked, accepted only when the new
   triangles stay within the chord of the face's own surface (`Surface.Distance`, or `SampleSurface` for fillets,
   blends and lattices) and none flips. A cylinder wall collapses along its axis for free.

Steps 1 and 2 are most of the win on machined parts; step 3 covers everything, blends and lattices included. Drag
previews skip the pass (time budget). It lowers triangles out, not octree time or memory.

## 3. Fillets (F)

In order of cost and risk:

1. Offset of a shell.
2. A single-edge fillet on a swept edge the profile is not behind, then on the rim of a cut: take away the wedge
   `max(cap, side, −loop, clips)` for the chosen edge, extended along tangent neighbours. Caps ⊥ the sweep only.
3. Coaxial pairs: line–line (plane ⊥ axis, cylinder, cone) through `RoundedEdgeField` as is; sphere and torus pairs
   through a new meridian field |(ρ, z) − C| − r. Profile corners next to arcs in `WithCorner`.
4. Corners: the vertex sphere for three equal-radius plane–plane fillets, named `<id>/corner:a+b+c`; then the
   horn-torus filler for two flush inside fillets in a join (prototype its STEP read-back first).

Unequal-radius corners keep the mitre and say so; non-coaxial cone, sphere and torus edges keep their diagnostic.

## 4. Sketch tools (S)

1. **Fillet and trim** in a new `SketchEdits` (shared by the page, MCP and chat). Fillet: two tangent points, an arc,
   two tangent constraints, a radius; the old corner stays as a construction point. Trim: closed-form intersections,
   split, delete the picked piece; constraints on a deleted piece are dropped and listed, never silently.
2. **Projected edges:** first a one-off copy, then a linked construction entity `{"project": [faceA, faceB]}` resolved
   each regeneration from the two named surfaces; `sketch-reference-lost` when it cannot be. Edges that project to
   an ellipse are refused.
3. **Sweep along a flat line-and-arc path:** open chains from `SketchRegions`, profile ⊥ path at its start, arcs
   tighter than the profile refused. Faces `sweep/<path entity>:<profile entity>`.
4. **Tapered extrude.**

## 5. Out of scope: approximated faces

Splines, lofts, 3D and helical sweeps, unequal-radius corners, non-coaxial fillets, and an export path for smooth
blends all need B-spline curves and surfaces with fitted edges in `ExactBrep` and `StepWriter`. That is one milestone
of its own, after the demo.

## 6. Work breakdown

Each task is about half a day and owns its files.

**R: silent failure** (`FrepModeler.Lost`, `RobustnessReproTests`)
- **R1** Pruned descent; unskip the repro; variants at the other corners and a thin void.
- **R2** Skipped repro for the two-surface and masked cases.
- **R3** Release timings on 20-op (0.5 / 0.25 / 0.1 mm) and random parts.
- **R4** Seeds 150–1149 and 1150–2149 against OCCT: 0 silent, no new refusals where both backends agree; §14 text.

**T: face simplification** (`Isoform.Meshing` new `FaceSimplifier.cs`, `MeshBuffers`, `Session`, bench)
- **T1** Face boundary loops from the mesh: per face, ordered vertex loops, outer and holes.
- **T2** Planar faces: polygon-with-holes triangulation in the plane (ear clipping with hole bridges, robust
  orientation tests).
- **T3** Collinear vertices on plane–plane edges, removed from both faces.
- **T4** Curved faces: edge collapse with the chord check against the owner surface (2 tasks).
- **T5** Session and server: simplified mesh for display, drag previews unchanged; `LineFaces` still right.
- **T6** Tests and bench: watertight and outward, face of every triangle unchanged, vertices on their surfaces,
  chord bound held, volume unchanged on planar parts, bit-identical on repeat; triangle counts on 20-op and a blend
  and lattice part; §14 text.

**X: shared** — **X1** Closed-form surface intersections from `Isoform.Step` to `Isoform.Geometry`.

**F: fillets** (`Compiler`, `Fields`, `ProfileField`, new `Geometry` files, `ExactBrep` tests)
- **F1** Offset of a shell.
- **F2** Single-edge wedge on a swept edge; **F3** on a cut's rim.
- **F4** Coaxial line–line pairs (after X1).
- **F5** Meridian field for sphere and torus pairs; **F6** its STEP check.
- **F7** Profile corners next to arcs (2 tasks).
- **F8** Vertex sphere field; **F9** third-fillet detection and naming; **F10** sphere faces in STEP.
- **F11** Horn-torus corner filler in joins, and its STEP (2 tasks).

**S: sketch tools** (`Isoform.Sketching`, compiler sketch prep, `sketch/*.js`, MCP)
- **S1** Sketch fillet; **S2** intersections; **S3** trim with constraint remapping (1.5); **S4** UI (1.5); **S5** MCP.
- **S6** Projected copy; **S7** linked projection and solver rows (after X1; 1.5); **S8** diagnostics; **S9** UI.
- **S10** Open-chain paths and sweep pieces (2); **S11** sweep field, names, checks (2); **S12** UI and MCP.
- **S13** Tapered extrude (3 tasks; optional).

About 45 half-days in all.

**Order**
1. R first: small, and it restores "0 silent" in §14.
2. Then three streams that touch different files: T (Meshing, Interactive), F1–F3 (Compiler edge code), S1–S5
   (Sketching, JS).
3. X1 before F4 and S7.
4. F8–F11 last.

## Risks

- **Face loops from a dual-contoured mesh** can touch themselves where a face pinches to a point; T1 must split them
  rather than triangulate across.
- **Tangent vertices** at a corner sphere make Gauss-Newton ill-conditioned (`ExactBrep`, the K7 false-vertex check).
- **Horn torus** (major radius = minor) may not read back in OCCT: prototype before F11.
- **Two meshes:** the page picks on the simplified one; picks still resolve through `FaceNear` on the octree mesh.
- **The descent in R** must stay cheap on fillet-heavy trees; R3 measures it.
