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
F-rep core names them (`boss/a+boss/b`, `boss/w+plate/w`). `StepConversion` follows two conventions of OCCT's STEP
reader, both found by experiment:
- a circle edge's range starts in [0, 2π);
- a cylinder face is written in the period of u that ends in (0, 2π], with a seam met first along the use the reader
  gives its first p-curve.

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
- The two STEP conventions above come from OCCT's behaviour, not from its source; if another reader disagrees,
  moving the seam to the STEP cylinder's u = 0 would satisfy both.

## Experiment 3: candidates (to decide)

1. **Holes and sketches on faces:** a hole is a cut by a cylinder; a sketch on a face needs only the face's frame.
   Cheap now, and it covers most of what `Isoform.Build`'s plates need.
2. **Ellipses:** a plane at an angle to a cylinder (chamfered bores, angled cuts). The first curve that is not a line
   or circle: a new carrier, its p-curves on both surfaces, and intersections with the others.
3. **Revolve:** cones, spheres and tori as carriers. Widens the geometry; booleans with them need 2 first.

Recommended: 1, then 2.
