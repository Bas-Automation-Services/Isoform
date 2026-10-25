# Coarse cells along edges and in walls: plan

Status (2026-10-25): proposed, not started.

## Why

The octree is adaptive (`DualContouring`): a cell holding one plane stays coarse at any size, one curved surface while
its chords stay within the tolerance. Every other cell (an edge, a corner, two surfaces) goes to the finest size.
On parts made of plates and tubes that rule sets the cost:

| Part (0.5 mm cells, chord 0.05) | Octree polygons | Share at finest size | What forces it |
|---|---|---|---|
| Top plate, 12 mm, 48-arc outline, 9 holes | 64,575 | about half on its flat faces | bands of finest cells along every edge |
| Leg, Ø48.3 × 3.2 tube, 610 mm | 159,096 | 21% (median cell 1 mm) | the 3.2 mm wall: a cube reaching both sides is two surfaces |
| Foot, 12 mm plate, one hole | 23,284 | about half | edges |

Meshing, the lost-face and pocket searches, topology and the display simplification all scale with this count.
A plain tube costing more than the blended design around it is the symptom.

## What changes

1. **Straight edges.** A cell whose field simplifies to two planes (`max`/`min` of two plane leaves) holds a straight
   edge; its vertex already lands on the edge by the quadric. Let it stay coarse up to the unit size, as a single
   plane does. Neighbouring coarse cells then trace the edge with collinear vertices.
2. **Curved edges between analytic surfaces.** Plane ∩ cylinder (a rim or an ellipse) and cylinder ∩ cylinder: allow
   cells up to the chord size of the edge curve, `√(4ρε)` with ρ the curve's smallest radius of curvature, as
   `MaxCurvedCell` does for surfaces.
3. **Walls.** A cell whose field simplifies to two disjoint patches (both faces of a plate, inside and outside of a
   tube) gets a vertex per patch, as a finest cell with several pieces does today, at coarse size, when the interval
   bounds prove the two patches do not meet within the cell (their gap at least one cell).

Corners (three surfaces or more) and everything not analytic stay at the finest size.

## What must keep holding

- K3's guarantee: no feature of 2 × cell or more is missed, smaller ones are reported. Coarse cells may only be
  taken where the bounds certify what they hold; the torture suite and `EditSuiteTests` stay green.
- Thin spots (`FindThin`), pockets and lost faces assume finest cells near several surfaces. Each search gets the
  coarse two-surface cells explicitly: a coarse wall cell is certified thicker than a cell, so it reports nothing.
- Topology is read from the mesh and its owners: coarse edge cells must give the same faces, edges and vertices
  (`FrepTopologyTests`, the face counts of the examples and the stand).
- Determinism and the region cache: the cache format changes (version bump).

## Steps

1. **Spike, straight edges only** (1–2 days): rule 1 behind a setting, on the plates. Measure polygons and time on
   the stand, run the full suite, look at the topology and diagnostics diffs. Stop here if the searches need more
   than the coarse cells passed to them.
2. Rule 3 (walls), measured on the legs: the largest single gain.
3. Rule 2 (curved edges).
4. The default switched on, cache version bumped, `research-plan.md` §14 updated with the numbers.

## Measured by

Octree polygons and regeneration time of the stand's three parts and of the examples (`Isoform.Bench regen`,
`stand`), before and after; the full test suite; the browser suite.
