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

- `Plane3D`'s U and V axes in `BasAutomation.Geometry` are left-handed (U × V = −N). The kernel builds its own
  frames; anything that shares `Plane3D` with it has to know.
- `BasAutomation.Geometry` types compare with a tolerance in `==` but hash exactly, so equal values can hash apart.
- The 2D segment distances in `Profile.cs` could move to `BasAutomation.Geometry`.
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

## Experiment 5: candidates (to decide)

1. **Inventor as a second STEP reader:** import the 33 STEP files in Inventor and check solids, face counts and
   volumes. It is the reader the parts end up in, and the STEP conventions above were only tested against OCCT.
   Interactive, so it is a checked report, not a unit test.
2. **Fillets and chamfers between planes:** constant-radius fillets between two planes are cylinders, and chamfers
   are planes. The work is in edge selection by face names, the set-back faces, and the ends where a fillet meets
   other faces. Fillets against a perpendicular cylinder need a torus carrier.
3. **Two cylinders at an angle:** cross bores. Their intersection is a quartic, so the first curve without a closed
   form, traced numerically or held as a B-spline.
4. **Revolve:** cones, spheres and tori as carriers.

Recommended: 1 (small, and it settles the open STEP question), then 2.
