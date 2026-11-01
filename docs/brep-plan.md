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

## Experiment 10: revolves and patterns (done, 2026-11-01)

Chosen from the candidates (5), so that the playground's parts go through the kernel.

**Kernel:** a region may now have edges on the axis it turns round (they sweep nothing; their place in the face
numbering is kept) and lines across the axis that end on it, which sweep a disc (one circle) or, in a part turn, a
sector; the part turn's two caps meet along the axis edge. A cone or arc reaching the axis needs an apex (a pointed
cone, a dome) and is still refused by name.

**Modeler:**
- *Revolve:* round the sketch's y axis, or round a line of the sketch (`Axis`: the plane moved onto the line as the
  F-rep core does it), whole or part turns; faces after their profile edges, the ends of a part turn `id/start` and
  `id/end`. The loops are turned counter-clockwise (holes clockwise) before the kernel sees them, names with them, so
  that the kernel's face numbering is the profile's.
- *Pattern:* copies of blocks, extrudes, revolves, holes and plane cuts, each in the body of the feature it copies
  (a new solid's copies join it), copy k of face s named `id/k:s`. A `Motion` places a copy: the feature's sketch
  frame (or hole spot, or cut plane) moved; a mirror keeps the frame's moved origin, Z and X, so its Y turns over and
  the profile is mirrored in its x axis to stay where the mirror puts it, and the kernel only ever sees right-handed
  frames. Copies of cuts only are cut out together (in one boolean where they do not touch); others go copy by
  copy, feature by feature, in the F-rep core's order.
- *Tools unmerged:* a tool's faces are no longer merged on one surface when it is made, only when it is applied (a
  boolean merges what it leaves; a new solid, or a join that meets nothing, is merged as it is added).

**STEP:** an image along u or v is kept exactly straight. A cone's seam whose u drifted down by 1e-15 between its ends
had OCCT's reader take the seam's two images the wrong way round (two of six countersinks in a bolt circle).

**Results:** 8 more documents: the playground's pin (discs on the axis, a chamfered tip), a half turn closed on the
axis, a tube revolved round a construction line, a row of revolved bores, a countersunk bolt circle, tabs round a
disc (rotated blocks), a mirrored tab, and a mirrored boss with a hole through it (a join and a cut). All 59 documents
match the F-rep core and `ExactBrep`, have exact volumes and read back in OCCT valid without healing. Tests:
`tests/Isoform.Brep.Tests` 261, `BrepKernelStepTests` 59. Of the playground's examples, five build (the bracket's row
of revolved bores and the flange's countersunk bolt circle among them).

**Naming difference found:** a circle drawn as two named half arcs is one face named by both. When later cuts leave
a piece of it within one half, the F-rep core names that piece by its arc alone; the kernel keeps both names, since
it merged the halves when the solid was made. A sketch circle (one entity) is named the same by both.

## Experiment 11: sweeps and combined bodies (done, 2026-11-01)

Chosen from the candidates (1 and 2): both join what the kernel makes already.

- *Sweeps:* the F-rep core's sweep planning is now `Sweeps.Plan` (public, unchanged): from the profile's plane and the
  path it gives the pieces (`SweepPiece`, one per path entity, from where the plane crosses the path square, with the
  same refusals) and the regions in section coordinates. The modeler builds each piece in the kernel (the section
  extruded along a line, turned round an arc's axis in the piece's (distance, height) coordinates, which mirrors it)
  and joins them in order; the joints, where neighbouring pieces' ends lie on one plane and their sides touch
  tangentially, go through the booleans as fillets did. Sides `id/<path entity>:<profile entity>`, an open path's
  ends `id/start` and `id/end`; a closed path has none (a circle path is one whole turn). Patterns copy sweeps too.
- *Combined bodies:* `CombineBodies` joins another body into the current one or cuts it out, used up (its features
  then belong to the current body) or kept; faces keep their names.
- *Comparison:* corners are compared by place: the F-rep side's solids share the vertices where they touch (a clamp
  in its seat), the kernel's each have their own.

**Results:** 6 more documents: the playground's pipe bend (cylinders and a torus) with and without its flange, a bar
round a closed stadium path, a ring swept round a circle, a body cutting its seat and kept, a body joined in. All 65
match the F-rep core and `ExactBrep`, have exact volumes and read back in OCCT valid without healing. Tests:
`tests/Isoform.Brep.Tests` 279, `BrepKernelStepTests` 65, the F-rep core's sweep tests 76. Seven of the playground's
13 examples build.

## Experiment 12: spheres and poles (done, 2026-11-01)

Chosen from the candidates (1, its sphere half), for the playground's turned shaft.

- *Carrier:* `Sphere`, round its frame's Z: u round the axis, v the latitude in [−π/2, π/2]; its normal is its own
  (Su vanishes at the poles).
- *Poles:* where a face runs up to a pole its (u, v) boundary runs along v = ±π/2 over a single point. That is a
  degenerate edge on a `Pole` curve (one point for every t, t the u it runs over), used once, by that face: it closes
  the loop in (u, v), so tracing, locating, sewing and `MassProperties` take it unchanged (it runs along u alone and
  adds nothing to the integrals). `BodyCheck` allows it one use and leaves it out of the Euler–Poincaré count;
  sewing gives it no partner; the topology skips it; STEP leaves it out (a loop through a pole closes there, as
  Isoform's export has it), carrying the u it turns on to the next image.
- *Intersections:* a sphere is a torus without a hole (its section a circle on the axis), so a plane square to its
  axis and a cylinder or cone round it meet it through the torus's routines; other planes and cylinders (circles
  that are not parallels) are refused. Ray hits, (u, v), side and distance, parallels and meridians as p-curves.
- *Mass properties:* closed form on a sphere too, R² cos v (c · n + R) for the volume, R² cos v for the area.
- *Revolve:* an arc round a centre on the axis sweeps a sphere, a cap closed at a pole where it ends on the axis, a
  whole ball where both ends do; part turns close the poles with their ends.

**Results:** 4 more documents: the playground's turned shaft (dome, cone, undercut fillet, chamfered lead), a ball,
a domed post with a bore down its axis (the cylinder takes the pole away), a domed post cut flat square to its axis.
All 69 documents match the F-rep core and `ExactBrep` and have exact volumes; OCCT reads all back valid, without
healing but for the two with a pole (a ball, the shaft's dome), which need it exactly as Isoform's own export of a
dome does (`StepExportTests` marks them so: STEP cannot say a degenerate edge). Tests: `tests/Isoform.Brep.Tests` 294,
`BrepKernelStepTests` 69. Eight of the playground's 13 examples build.

Not yet: pointed cones (`ExactBrep` has no apex either, so nothing to compare with), spheres off a revolve's axis
(ball corners).

## Experiment 13: offsets (done, 2026-11-01)

Chosen from the candidates (2), for the playground's "Mirror and offset".

- *What an offset is:* the F-rep core moves the faces of its tree: max(a, b) − d = max(a − d, b − d), so each
  feature's own faces move and the booleans between them stay. Flat edges of a block stay sharp; an extruded or
  revolved profile grows in its plane, and where two of its edges part (a convex corner growing, a reflex one
  shrinking) a round corner fills the gap, a cylinder (extrude) or torus (revolve) named `offset/corner:a+b`; a
  cut's tool shrinks as the part grows; a fillet's radius grows with its faces.
- *The kernel does the same by history:* an offset rebuilds every feature before it with its tool grown by the
  offsets after it (a pattern's copies by those after the pattern): blocks bigger, profiles grown (`ProfileOffset`:
  each segment moved right of travel by its own distance, neighbours met, round corners where they part; a
  revolve's axis edges stay and its corners there stay sharp), caps moved, holes narrower and shallower, plane cuts
  back, fillets rounder (tighter round a concave edge, sharp once gone). Sketch planes and the faces holes are
  drilled into stay where they were first placed, as the F-rep core places them once.
- *Names:* tools are named through the pattern's renaming as they are built (as the F-rep core's `Named`), so a
  copy's corner is `grow/corner:mirror/1:rib/a+mirror/1:rib/b`.
- *Refused:* an offset of a part with more than one body (the F-rep offsets only the current one), of a part turn
  (its ends move off the axis), a sweep, a chamfer; a profile whose offset uses up a segment.

**Results:** 5 more documents: the playground's mirrored ribs grown, a pocket and bore grown (the tools shrink), a
domed post grown (a torus corner at its base, the dome still a sphere), a filleted plate grown, an L shrunk (its
reflex corner turns round). All 74 documents match the F-rep core and `ExactBrep` and have exact volumes; OCCT reads
them all back valid. Tests: `tests/Isoform.Brep.Tests` 311, `BrepKernelStepTests` 74. Nine of the playground's 13
examples build; the other four are a blended extrude with a shell and three lattices.

## Experiment 14: shells (done, 2026-11-01)

Chosen from the candidates (2).

- *Cavity:* the history before the shell rebuilt with every face moved in by the wall (cut tools grown by it, so a
  bore leaves a tube through the cavity and a pocket a dent in its roof), corners met sharp, its faces named
  `id/inner:face`, cut out of the solid as a void. It is the offsets' rebuild with no round corners.
- *Sharp, as CAD shells (Bas, 2026-11-01), round on request:* by default each inner face is an outer face moved, so
  the cavity has no faces of its own and its corners are sharp where the outside's are. The F-rep core's cavity was a
  level set of its profiles' distance fields, round opposite a reflex corner and round a grown cut's corner, named as
  the flat faces either side, which its own exact export refused. Now a shell mitres its profiles' corners
  (`ProfileField.Mitred`, in the field and the tape: the larger or smaller of the two faces' values nearest a corner,
  the same outside surface), so both engines and both exports agree. `Shell.Round` (`"round": true`, written only
  when set) keeps the old round cavity, the wall exactly the thickness, its rounds named `id/corner:a+b` in both
  engines (the F-rep core gives the profile a corner face there, its corner line, whose inner face is the round).
- *STEP:* the writer turns a void's faces round, so for a seam on a cavity's face the first use as written decides
  (as `ExactBrep.SeamsForwardLast` has it): the first seam on a void face, the tube round a bore.
- *Refused:* a shell after an offset or another shell, an offset after a shell, a shell of a part with more than one
  body.

**Results:** 8 more documents: an L hollowed (sharp and round), a plate with a bore, with a pocket, with a fillet (the cavity's round
the fillet moved in), a domed post (a pole inside too), a plate rounded after shelling. All 82 have exact volumes and
match the F-rep core's names, topology and exact faces; OCCT reads all back valid. Round shells are checked against the
F-rep core too. Tests: `tests/Isoform.Brep.Tests` 335, `BrepKernelStepTests` 82. Still nine of the 13 examples: "Smooth blends and a shell"
stops at its blended extrude.

## Experiment 15: ball corners (done, 2026-11-01)

Chosen from the candidates (1, its sphere half).

- *Where:* a fillet whose edge ends between two earlier fillets of its radius at a square corner (each fillet's axis
  through the point r inside all three faces, square to the other two), as the F-rep core's `VertexSphereField`. The
  ball is named `id/corner:a+b+c` after the three fillets, sorted, the last one's id first. Unequal radii, other
  angles, chamfers and concave edges are still refused there.
- *How:* the three fillets run on past the corner, as they always have, and cross in a mitre that holds the ball;
  then a cap is cut away: the cube from the ball's centre 3r/2 along the three faces' normals, less the ball (a
  boolean: a plane at any angle meets a sphere in a circle now). The fillet cylinders only touch the ball, so the
  cut needs no new surface pair.
- *Fixed on the way:* a sphere face's box now holds the whole sphere (a ball's only edges are its seam and poles, so
  a box of its edges missed most of it, and pieces inside it were taken as outside); a plane's circle on a sphere
  starts square to its axis (a meridian started at a pole had no half-plane); a meridian's piece beyond a pole takes
  the mirrored line (u + π, ±π − v).
- *Sphere:* its axis square to the corner's diagonal and in none of the side planes ((x − 2y + z)/√6 in the
  corner's directions), so the poles and the seam stay off the piece. `ExactBrep` turns its corner balls the same
  way, to a different axis; spheres are compared by centre and radius.
- *(u, v):* `SphereCircle2`, a circle on a sphere as longitude and latitude, u kept continuous by its winding round
  the axis; `CurveSegment` scales v too.

**Results:** 3 more documents: a corner of three fillets, the same with the upright edge first, a plate with all four
top corners balled. All 85 have exact volumes and match the F-rep core and `ExactBrep`; OCCT reads them all back
valid without healing (no pole on a corner ball). Tests: `tests/Isoform.Brep.Tests` 346, `BrepKernelStepTests` 85.

## Experiment 16: pointed cones (done, 2026-11-01)

A revolve's line at an angle that ends on the axis turns a cone closed at its apex: the apex is a `Pole` edge, as a
sphere's pole is (its p-curve the line where the radius is zero, the cone's frame at the line's other end). The cone
has a normal of its own, the same all along a generator (Su vanishes at the apex). An arc off the axis reaching it
(a torus with no hole) is still refused.

**Results:** 3 more documents: a pin with a drill point, a cone, a plate with a pointed conical dimple cut in. They
match the F-rep core's names and topology and have exact volumes; `ExactBrep` cannot cut a cone open at its apex,
so they are kept out of the exact-face comparison (`Documents.ExactInTheFrep`); OCCT reads them back valid with its
healing, as for the other poles.

**Cuts through a pole:** a plane through a dome's or a cone's axis runs through its pole, where the pole's edge is
now split by u (every point of it is one point): where the cut's pieces either side reach it, a meridian's or a
generator's u. A half domed post and a half cone match the F-rep core's names and topology with exact volumes
(`ExactBrep` refuses the half dome: it sees the solid touch itself at the pole). Through a pole the topology runs one
edge on between the same two faces where the file has two, so for a body with a pole OCCT's counts are taken
against the file's own. Tests: `tests/Isoform.Brep.Tests` 358, `BrepKernelStepTests` 90.

## Experiment 17: local booleans, step 1 (2026-11-01)

`Isoform.Bench brep drill n` drills n holes one feature at a time, each a boolean against the whole plate so far:
quadratic, 400 holes 10.4 s processor (192 joins 2.97 s wall). Profiled, 55% of it was `BodyCheck` on the whole
body after every boolean: every shell's volume and every loop's (u, v) area again.

- *Only what changed is checked:* a face that comes through a boolean whole (`Patch.Unchanged`, not flipped) was
  checked in a valid body; its loops are not checked again, and a shell holding one faces the way it did (every
  edge's uses run opposite, which is checked), so its volume is not found.
- *Exact (u, v) areas:* lines and circles in (u, v) in closed form, not by quadrature (a cut face holds every hole
  drilled before as a loop).

400 holes: 10.4 s → 6.5 s processor on a loaded machine, back to back; joins about 8% (their cost is elsewhere).
Still quadratic: every boolean welds every use's ends, sews every face, builds every face's flat (u, v) outline
(`FlatFace`) and merges surfaces over the whole body.

**Step 2 (designed, not done):** edit the body in place. Untouched faces keep their old vertices and edges (paired by
their old edge, no welding), only the uses round the cut faces are welded (against the vertices of the faces next
to them), and a body keeps its `Solid` with each untouched face's `FlatFace` carried over with its edges renumbered;
surfaces are merged only among new and neighbouring faces.

## Next: candidates (to decide)

What still stops the playground's examples, and what is left from before:

1. **Ball corners of other kinds:** corners not square; unequal radii (the F-rep core mitres them with a warning,
   and its exact export does not follow the mitre either).
3. **Arcs meeting lines at a corner** in loop fillets (a quartic, unless the radii make it a circle or ellipse).
4. **Inventor as a second STEP reader**, parked.
5. **Local booleans, step 2:** edit the body in place (see experiment 17).
6. **Mixed models (B-rep, then F-rep):** today one model goes through one engine, and the B-rep modeler refuses the
   whole model at the first feature it does not take. Both share the document, `IModeler`, the face names and
   topology (identical on every test document) and the exact output (`Isoform.Step.Brep`), which makes a hand-off
   possible: run the history in the kernel while it can, and at the first F-rep-only feature (lattice, smooth blend)
   turn the B-rep body into a field and go on in the F-rep core. Inside/outside is enough for lattices
   and blends (the kernel's point classification). Faces the F-rep features leave alone keep their exact form, so
   a part with an infill lattice could still export its skin exactly (`ExactBrep` refuses lattice and blend faces
   today). The other way, F-rep faces into the B-rep, gives only triangles for lattices and blends. Worth it once
   the kernel covers the common features; the product (playground, build tool, MCP, assemblies) uses the F-rep
   result throughout, so the mixed result must look like an F-rep one to them.

Blended extrudes and shells (smooth F-rep blends) and lattices stay with the F-rep core.

Recommended: 5 (local booleans) or 6 (mixed models), now that the playground's exact features are covered.
