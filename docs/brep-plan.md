# Managed B-rep kernel: plan

Goal: find out whether a fully managed C# B-rep kernel next to the F-rep core can become Isoform's exact backend
(long term: behind `Isoform.Build`), without OCCT in the modelling path. Exploratory; it replaces nothing yet.

Decisions (2026-10-25):
- Manifold first: two uses per edge, no non-manifold topology until a feature needs it.
- Reusable math (transforms, predicates, 2D distances) goes into `BasAutomation.Geometry`, on feature branches; no
  merges or breaking API there without review.
- The kernel (`Isoform.Brep`) depends on `BasAutomation.Geometry` only: no document, modelling, STEP or F-rep code.
  An adapter (`Isoform.Brep.Modeling`) puts it behind `IModeler` and converts its bodies to `Isoform.Step.Brep`.
- Experiment 2's scope is decided after experiment 1's results.

## Build

The kernel needs `RigidTransform3D` and `Predicates`, which are not in a released `BasAutomation.Geometry` yet
(2.0.0 does not have them). Build against a source checkout:

```sh
dotnet test tests/Isoform.Brep.Tests -p:BasAutomationGeometryProject=<path to BasAutomation.Geometry.csproj>
```

`Directory.Build.targets` swaps the package for the project in every project that references it.

## Experiment 1: one exact extrude (done, 2026-10-26)

**Kernel (`Isoform.Brep`):** analytic carriers (lines, circles, planes, cylinders, with p-curves), an immutable body
of flat arrays with typed handles, `BodyCheck` (edge uses and senses, loop closure in space and in (u, v), p-curves
on their edges, Euler–Poincaré, the sign of the volume), `MassProperties` (volume and area by Green's theorem in each
face's (u, v), no tessellation) and `Extrusion` (a region of lines, arcs and full circles with holes, swept between
two heights; bad regions are refused with a reason).

**Adapter (`Isoform.Brep.Modeling`):** `BrepModeler` builds a part of one plain extrude that starts it (an inline
profile, or a sketch's regions on a plane) with Isoform's face names (`id/<edge>`, `id/start`, `id/end`) and
Isoform's topology (seams are not edges; a vertex is where three or more faces meet). Anything else is refused with
`backend-failed` and the feature's id. `StepConversion` writes the bodies as `Isoform.Step.Brep` for `StepWriter`.

**Results:** on a mounting plate (rounded corners, four holes, a bore, a slot; from a sketch), an L bracket with a
concave fillet on an oblique plane, and two pads in one sketch (two bodies):

| Part | Kernel F/E/V | F-rep F/E/V | Names and neighbours | Surfaces and corners vs `ExactBrep` | OCCT read-back |
|---|---|---|---|---|---|
| mounting plate | 19/46/24 | 19/46/24 | same | same, corners within 1e-9 | valid, no healing, volume error 0 |
| bracket | 9/21/14 | 9/21/14 | same | same, corners within 1e-9 | valid, no healing, volume error 0 |
| two pads | 12/24/16, 2 bodies | 12/24/16, 2 bodies | same | same, corners within 1e-9 | valid, 2 solids, volume error 0 |

Volumes are exact to 1e-12 relative from the B-rep itself. Tests: `tests/Isoform.Brep.Tests` (46, under 3 s) and
`BrepKernelStepTests` in `tests/Isoform.Occt.Tests` (3; needs the native OCCT shim).

**What it shows:** for prismatic parts the managed kernel gives the same answer as the F-rep core and its exact
B-rep, names included, in milliseconds instead of a meshing pass, and its STEP needs no healing. It says nothing yet
about the hard part of a B-rep kernel: booleans, and faces that meet other faces in general position.

## Experiment 2: join and cut (done, 2026-10-26)

Scope (2026-10-26): extrudes that join or cut, bounded by planes and cylinders, with the F-rep core as the oracle.

**Boolean (`Booleans` in `Isoform.Brep`):**
- *Intersect:* every pair of faces whose boxes overlap meets in closed form (plane–plane lines; plane–cylinder
  circles and lines; parallel cylinders in lines). The curves are cut down to where they lie on both faces. Faces on
  one surface instead imprint each other's edges.
- *Split:* edges split where those pieces end, recorded per edge so that both faces of an edge split it alike. Each
  face is then cut in its own flat (u, v) (lengths true on cylinders): uses are traced into loops that keep the face
  on their left, and loops sorted into outer boundaries and holes.
- *Classify:* each piece is located against the other solid at a point well inside it (ray parity, retried when a
  ray grazes an edge): out, in, or on its boundary facing the same way or the other. Union keeps A out or on-same and
  B out; difference keeps A out or on-opposite and B in, turned round.
- *Merge and sew:* kept faces on one surface that share an edge are merged into one face (faces round a cylinder
  close with a seam), edges that continue each other through a two-edge vertex are joined, and the faces are sewn by
  edge geometry into shells. A shell with negative volume is a void of the solid around it; several solids come
  back as several bodies. Every result passes `BodyCheck`, or the boolean fails with the reason.
- *Refused, named:* ellipses (a plane at an angle to a cylinder), tangencies, cylinders at an angle.

**Adapter:** `BrepModeler` runs the features in order: new bodies, joins (the tool united with each body it meets)
and cuts (subtracted from each body). Faces merged on one surface are named by all their sources, sorted, as the
F-rep core names them (`boss/a+boss/b`, `boss/w+plate/w`). `StepConversion` followed two conventions of OCCT's
STEP reader, found by experiment; experiment 4 replaced them with Isoform's own export conventions.

**Results:** 14 documents on a 100 × 60 × 10 plate: a boss on top, pockets past and flush with the top, a window, a
bore, a boss flush with two sides, a notch, a bore along the plate, blocks over and against the end, a cut parting
it in two, a boss with a counterbored hole (three booleans in a row), a slot across a bore's seam, and a boss on a
pocket floor. On all 17 documents (with experiment 1's):
- faces, names, neighbours, edges, corners and bodies match the F-rep core;
- surfaces and corners agree with `ExactBrep` to 1e-9;
- volumes are exact;
- OCCT reads every file back as valid solids without healing, with a volume error of 3e-16 at most.

Regeneration in a Debug build, against the F-rep core at 1 mm cells (ms):

| Part | Kernel | F-rep |
|---|---|---|
| mounting plate (exp. 1) | 61 | 225 |
| boss on top | 18 | 71 |
| pocket flush with the top | 9 | 80 |
| bore along the plate | 21 | 88 |
| cut in two | 7 | 67 |
| boss with a counterbored hole | 69 | 99 |
| slot across a bore's seam | 34 | 75 |

Nothing in the kernel is optimised yet: vertex and edge matching are quadratic, every face pair is intersected
after a box test, and a join unites the tool with every body.

Tests: `tests/Isoform.Brep.Tests` (101, 5 s; `BooleanTests` for the kernel alone) and `BrepKernelStepTests` (17).

**What it shows:** the first booleans hold up. Coincident faces, splits that part a body, voids, seams and chained
booleans all come out right without special cases per feature, because classification and sewing go by geometry
alone. What is still open is the general case: faces that meet in curves other than lines and circles (ellipses,
the quartic of two cylinders at an angle), tangent contact, and sliver tolerances.

## Open questions

- Fixed on the Geometry branch (2026-11-01), not released: `Plane3D`'s V axis is now N × U, so its U/V axes are
  right-handed (they were left-handed, against its doc comment; no addin used them), and types whose `==` takes a
  tolerance hash to a constant, so equal values no longer hash apart. Whether to merge is open.
- The 2D segment distances in `Profile.cs` stay in the kernel: they work on its own segments (lines, arcs and
  `CurveSegment`), and `BasAutomation.Geometry` has no arc distance to move them onto.
- The STEP conventions come from OCCT's reader (experiment 4); whether other readers, Inventor's first, agree is
  not checked yet.

## Experiment 3: holes, blocks and sketches on faces (done, 2026-10-26)

**Adapter only; the kernel is unchanged.** `BrepModeler` now follows Isoform's history:
- *Features:* blocks; extrudes from inline profiles or sketches; holes. A hole is a cut by a cylinder along the outward
  normal where it goes in. Through all, it runs past the current body's extent both ways; blind, it runs from a floor
  at its depth out past the part. Faces are named `id/wall`, `id/floor` and `id/<point>:wall`.
- *Sketches:* solved where they stand in the history. A sketch on a face takes the face's plane as Isoform's
  `SketchPlanes.OfFace` does: outward normal, world origin and world x put onto the plane.
- *Bodies:* every feature works on the current body (`Feature.Body`), as in the F-rep core. Joins and cuts leave the
  other bodies as they are. (Experiment 2 had joined and cut every solid, which only agreed because its documents had
  one body.)
- *Refused, named:* sketches that project part edges, and faces not flat or not there.

**Results:** 9 more documents on a 100 × 50 × 10 block:
- a hole through, through a side face, blind in the top, and blind at sketch points;
- two holes overlapping, so cylinder meets cylinder;
- a constrained sketch with a bore;
- a boss sketched on the top face and bored through;
- a slot sketched on the front face and cut 20 deep;
- a hole in the second of two named bodies standing in each other.

All 26 documents match the F-rep core (faces, names, neighbours, edges, corners, bodies), agree with `ExactBrep`,
have exact volumes and read back in OCCT valid without healing. Tests: `tests/Isoform.Brep.Tests` 128,
`BrepKernelStepTests` 26.

## Experiment 4: ellipses and plane cuts (done, 2026-10-26)

**Kernel:**
- *Carrier:* `Ellipse3`. A plane at an angle to a cylinder now meets it in an ellipse: centre on the axis, minor axis
  R across it, major axis R / |n·a| down the slope.
- *P-curves:* on a plane an `Ellipse2`. On the cylinder a `Wave2`: u runs with the ellipse's angle and v is a cosine
  of it, because across the axis a plane section is the cylinder's own circle.
- *Intersections:* line–ellipse is a quadratic in the conic's frame. Circle–ellipse and ellipse–ellipse go through
  the line their planes share, or are solved numerically (sign changes, then bisection) when the planes coincide.
- *Flat segments:* `CurveSegment` measures a p-curve that is neither line nor arc in short sub-pieces, each a graph
  over its chord. Distance, crossings and area are exact to rounding, and so is the angle it subtends: a sub-chord's
  angle plus a whole turn where the point lies between sub-piece and chord. Tracing, classification and sewing are
  unchanged.
- *Still refused:* two cylinders at an angle (a quartic), tangencies.

**Adapter:** `PlaneCut` is a cut by a box standing on the plane and reaching past the body; its base is `id/face`.

**STEP:** `StepConversion` now follows Isoform's own export (`ExactBrep`) instead of the two experimental rules of
experiment 2:
- circle edges start at π/2 and ellipse edges in [π/2, 3π/2], clear of the 0 / 2π wrap;
- images come from `SurfaceParameters.Image` (degree-9 pieces where they are not straight), each shifted to start
  where the one before ended;
- a seam's last written use runs as the face is oriented;
- image u values within 1e-12 of a whole turn are put on it. A seam at u = 0 written at −2e-16 at one end is read a
  turn away and leaves the face open.

**Results:** 7 more documents on the 100 × 50 × 10 block:
- a 45° cut off the end;
- an oblique cut through a hole;
- one cut through two holes (two ellipses in one face);
- a tilted cut through a boss;
- a bore 30° off vertical;
- an angled post joined into the plate;
- a hole drilled into the angled face.

All 33 documents match the F-rep core, agree with `ExactBrep` (ellipse edges included), have exact volumes, and read
back in OCCT valid without healing. Tests: `tests/Isoform.Brep.Tests` 151 (`BooleanTests` 15), `BrepKernelStepTests`
33.

## Experiment 5: fillets and chamfers between flat faces (done, 2026-10-26)

Decided 2026-10-26: the Inventor check (importing the STEP files there as a second reader) waits until later.

**Kernel (`Blends`):** rounds or chamfers the straight edge between two flat faces.
- *Convex edge:* the body loses the prism between the edge and the ball rolled along it (a triangle for a
  chamfer). The prism runs past the edge's ends, so the faces the edge ends on cut the new face off there: an arc on a
  square end face, an ellipse on an angled one.
- *Concave edge:* the body gains the prism, which stops exactly on the end faces (so those must be square to the
  edge).
- *Guard:* every face other than the two and those at the edge's ends must come through unchanged in area.
  Otherwise the blend is refused: a radius too large for a face, or an edge that ends inside the material.
- *Fixes on the way:*
  - A plane tangent to a cylinder now meets it in its touching line (a fillet's cylinder on its own faces), where it
    had been refused.
  - Tracing tells apart uses that leave a node in one direction (a fillet's arc running tangentially off a straight
    edge) by curvature, between candidates as well as against the way back.

**Adapter:** `Fillet` and `Chamfer` on an edge (`Edge = [face, face]`) work on the solid that has both faces. The
new face is `id/face`, as in the F-rep core. Loop fillets and chamfers are refused.

**Results:** 7 more documents:
- a rounded top edge, a chamfered front edge and a rounded vertical corner of the block;
- an L bracket's inside fillet (concave);
- a rounded edge beside a hole;
- a rounded edge ending on a 45° face (an ellipse arc);
- a chamfer and a fillet on two separate edges.

All 40 documents match the F-rep core, agree with `ExactBrep`, have exact volumes and read back in OCCT without
healing. Tests: `tests/Isoform.Brep.Tests` 177 (`BlendTests` 6), `BrepKernelStepTests` 40.

**Refused, named:** fillets that meet at a corner (the F-rep core mitres them; here the second fillet's cylinder meets
the first's at right angles, a quartic), fillets against a cylinder (they need a torus), loop fillets.

## Experiment 6: mitres and tori (done, 2026-10-26)

**Mitres:** two cylinders of equal radius whose axes cross meet in two ellipses, in the planes through the crossing
that bisect the axes (p·(a − b) = 0 or p·(a + b) = 0). With that, a second fillet (or chamfer) sharing a face with an
earlier one is just its prism taken from the body that already has the first. That is Isoform's rounded max exactly,
and the corner comes out mitred. An edge that ends against two earlier fillets would close a corner of three, which
Isoform rounds with a ball; that is refused by name.

**Tori:**
- *Carrier:* `Torus`, periodic in u and v. Every place that handled a cylinder's periodic u now handles v as well:
  face windows, cut shifts, merge placement and STEP images.
- *Intersections:* tori meet planes square to their axis and cylinders round it in circles (one where they touch).
  Other planes and cylinders meet them in quartics and are refused.
- *Ray hits:* the quartic, solved on a fine grid with bisection.
- *Revolve:* `Revolution` turns a region in the (x, z) half-plane once round an axis. Lines across the axis sweep flat
  rings, lines along it cylinders, arcs tori, each curved face with its seam at u = 0. The faces are sewn by
  `Sewing` like any boolean's.
- *Fillets:* a whole circle where a flat face meets a cylinder square to it (a hole's rim, a boss's foot) is rounded
  by the spandrel turned round the axis. The ring is subtracted for a convex edge and added for a concave one, with the
  same area guard. A chamfer there would be a cone and is refused.

**Results:** 4 more documents: two fillets meeting in a mitre, a fillet meeting a chamfer, a filleted hole edge, a
boss with a rounded foot. All 44 documents match the F-rep core, agree with `ExactBrep`, have exact volumes, and read
back in OCCT valid without healing (tori included). The `ExactBrep` comparison no longer counts seam edges when it
finds corners, because each B-rep cuts its periodic faces open where it likes. Tests: `tests/Isoform.Brep.Tests` 197
(`BlendTests` 11, `RevolutionTests` 4), `BrepKernelStepTests` 44.

**A difference, kept out of the comparison:** a fillet on a swept profile's cap edge (a boss's top rim). The F-rep
core rounds it along the sweep and names it per side (`cap/face` and `cap/boss/rim`, 10 faces). The kernel makes one
torus face, `cap/face` (9 faces), with the exact volume. Which one Isoform should name is a question for the F-rep
side; `BlendTests` covers the kernel's.

## Experiment 7: loop fillets and chamfers (done, 2026-10-26)

**Kernel (`Blends.Loop`):** every edge round a flat face, one piece per edge with the spandrel (or chamfer
triangle) as its section.
- *Lines:* a prism along a line, stopped exactly where the next edge runs on tangentially, run on past a convex
  corner so that straight pieces mitre there. A concave edge's piece fills and stops at the sides it ends on.
- *Arcs:* a part turn of the section round the arc's axis (`Revolution` now turns through any angle and closes the
  ends with the region), between tangent neighbours, or a whole turn for a loop of one circle.
- *Applying:* the pieces go in one after another, every face's origins carried back to the body's or a piece's. One
  area guard runs at the end.
- *Refused, named:* reflex corners, sides not square to the face, arcs meeting a neighbour at a corner, chamfers
  round arcs (cones).

**Adapter:** `Fillet` and `Chamfer` with `Loop` name each piece after the face on the other side (`round/boss/a`), as
the F-rep core does.

**Fixes on the way:**
- *Faces that only touch:* surfaces the kernel cannot intersect (a straight fillet's cylinder against the next arc's
  torus, a torus against a side plane) are let through when neither face crosses the other's surface inside the other
  face. This is checked on a grid over each face and along its loops, with crossings found by bisection and located
  on the other face. A crossing finer than the grid could be missed; `BodyCheck` and the area guard stand behind it.
- *Winding on a chord:* the winding of a loop round a point on an arc's chord was undecided. The chord is seen at ±π
  and the point is on neither side of it, and this happens wherever the chord runs through other vertices (a cut
  through the end of a stadium). Arcs are now measured in two halves.
- *A finer F-rep mesh:* the `ExactBrep` comparison builds the F-rep side's exact B-rep at 0.5 mm cells. At 1 mm it
  cannot cut some fillet faces open.

**Results:** 4 more documents: a rectangle outline with a loop round (mitres), a pocket floor with a loop round
(concave), a stadium boss with a loop round (lines and tori meeting tangentially), the rim of a stadium cut-out with
a loop round (outer and inner loops). All 48 documents match the F-rep core, agree with `ExactBrep`, have exact
volumes and read back in OCCT without healing. Tests: `tests/Isoform.Brep.Tests` 213 (17 s; `BlendTests` 13,
`RevolutionTests` 6), `BrepKernelStepTests` 48.

**A difference, kept out of the comparison:** a loop chamfer round a rectangle. Both kernels make the same solid (the
volume is exact), but where four faces meet in one point at each corner, the F-rep core's topology splits the point
into an edge, in an order-dependent pattern, and its own exact export then refuses its counts. `BlendTests` covers
the kernel's.

## Experiment 8: cones (done, 2026-11-01)

Chosen from the candidates below (1), as it completes chamfers.

**Kernel:**
- *Carrier:* `Cone`, round its frame's Z: radius `Radius + Slope · v` at height v, above zero at the frame's origin
  (which flat (u, v) scales u by, as a cylinder's radius). Circles round the axis and the straight lines through the
  apex are lines in (u, v), so tracing, classification, sewing and `MassProperties` take it unchanged.
- *Intersections:* a plane square to the axis meets it in a circle; a plane through the apex in its generators (two,
  or one where the plane touches it: a chamfer's plane running on into a chamfer round an arc). A cylinder, cone or
  torus round the same axis meets it in circles, where their sections cross in the half-plane through the axis. Other
  planes (ellipses, parabolas, hyperbolas) are refused, and fall to the "faces only touch" check.
- *Ray hits:* the quadric, kept on the cone's own nappe.
- *Revolve:* a line at an angle to the axis sweeps a cone (regions touching the axis, which would need an apex, are
  still refused).
- *Chamfers:* round a whole circle where a flat face meets a cylinder square to it (a hole's rim, a boss's top), the
  triangle turned round the axis; round the arcs of a loop, a part turn of it between the tangent straight pieces.
  Only convex edges, as for straight chamfers.

**Adapter:** reaches and `FaceNear` know cones. **STEP:** a cone is written as Isoform's: apex, opening axis, half-angle.

**Results:** 3 more documents: a chamfered hole edge, a hole chamfered at both ends, a stadium boss with a loop
chamfer. All 51 documents match the F-rep core, agree with `ExactBrep`, have exact volumes and read back in OCCT
valid without healing (cones included). Kernel tests add cones
by revolution (whole and part turns, a frustum), chamfers on hole and boss rims and round a stadium, and booleans of
a cone against a torus and another cone round one axis, each checked by Pappus. Tests: `tests/Isoform.Brep.Tests` 231, `BrepKernelStepTests` 51.

**Comparison:** the F-rep side's chamfer faces keep the chamfer's size in `Surface.Radius`, which neither a plane nor
Isoform's cone uses; the `ExactBrep` comparison now leaves radius out for both and compares a cone by apex, opening
and angle.

## Experiment 9: speed (done, 2026-11-01)

Chosen from the candidates (4), before real parts go through the kernel. Measured first: `Isoform.Bench brep
[holes|loop|join|examples] [size…]` times a plate with an n × n grid of holes (one hole feature on a sketch's points),
an n-gon with its top rounded all round, n blocks joined onto a base, and the playground's examples; wall time and the
process's processor time (which a busy machine disturbs less). Profiles with `dotnet-trace`.

**What it cost, and the fix:**
- *Volume:* two thirds of the time. `MassProperties` integrated twice by quadrature (round each loop, and across the
  surface at every point of it: up to 192 evaluations each on a cylinder), and every boolean computes the volume
  twice (sewing tells solids from voids by it, `BodyCheck` the shell's orientation). The integral across the surface
  is now in closed form on planes, cylinders, cones and tori (a constant, or sines and cosines of u); quadrature
  stays for a surface without one.
- *Faces far from the tool:* every face was traced again and located against the other solid at a point well inside
  it. A face neither cut nor split comes through whole, and a piece of a face whose box misses the other solid's is
  outside it. Face boxes now hold the whole face (a circle's or ellipse's extremes within the edge, a torus face grown
  by the tube's diameter); they were the edges' samples, which a circle bulges past.
- *Matching points:* vertices in sewing and nodes in tracing were found by searching every point so far. `Welded`
  finds them in a grid of cells 2 Snap wide; merging faces on one surface only compares patches with a use between
  the same two points, and pairs the uses they share by their ends.
- *One shell:* is the solid; only several are built alone to tell solids from voids.
- *Locating:* the distance to a curve piece skips the sub-pieces that cannot come nearer than its nearest sample; a
  ray skips the faces whose box it misses; a curve's piece is located on the face with fewer edges first; the point
  well inside a face tries the three widest stretches on each line and gives a candidate up once it is no better.
- *Tools that do not touch:* `Booleans.Apart` groups tools whose boxes do not touch and `Booleans.Lumps` makes each
  group one body (not a valid body, several solids, but it locates points as they do together), applied in one
  boolean. The modeler cuts a hole feature's spots (and an extrude's regions) that way, and a loop fillet applies its
  pieces in two or three groups instead of one by one (a loop with convex and concave pieces still goes in order).

**Results** (processor seconds; before: the kernel of experiment 8 on the same busy machine, the 24 × 24 plate in
wall time on a quieter one):

| part | before | after |
| --- | --- | --- |
| plate with 16 × 16 holes, 262 faces | 24 | 0.6 |
| plate with 24 × 24 holes, 582 faces | 158 (wall) | 1.2 |
| plate with 32 × 32 holes, 1030 faces | | 1.2 |
| 192-gon with a loop round, 386 faces | 23 | 1.7 |
| 384-gon with a loop round, 770 faces | | 2.5 |
| 192 blocks joined one by one, 774 faces | 13 | 2.5 |
| 384 blocks joined one by one, 1542 faces | | 8 |

Tests add closed-form areas of a torus and a cone face, 45 bores cut in one boolean, overlapping bores grouped apart,
and a 100-hole grid through the modeler with every wall named. Tests: `tests/Isoform.Brep.Tests` 235,
`BrepKernelStepTests` 51.

**What is left:** each boolean still does work in proportion to the whole body (building every face's flat form,
sewing every face, `BodyCheck` with its volume), so n features on a body of F faces cost about n · F: some 10–20 ms
per boolean at a thousand faces. Booleans that only rebuild the faces they touch would remove that, at the price of
editing a body in place. Of the playground's examples, three build (in 0.2 s or less); the rest stop at features the
modeler does not take yet: Revolve (which the kernel can do), Extrude with a blend, Pattern, Sweep, and Lattice (which
stays with the F-rep core).

## Next: candidates (to decide)

1. **Ball corners:** three fillets meeting at a corner, a sphere carrier.
2. **Arcs meeting lines at a corner** in loop fillets: a torus mitring with a cylinder (a quartic, unless the radii
   make it a circle or ellipse).
3. **Inventor as a second STEP reader**, parked.
4. **Apexes:** revolves that reach the axis (a pointed cone, a disc), which need a degenerate edge.
5. **The playground's features:** Revolve in the modeler (the kernel revolves already), then Pattern (copies of
   features, which `Lumps` can cut or add in one boolean where they do not touch).
6. **Local booleans:** rebuild only the faces a boolean touches (see what is left above).

Recommended: 5, so that real parts go through the kernel and show what it refuses first.
