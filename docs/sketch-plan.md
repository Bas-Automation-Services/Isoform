# Sketching milestone: plan

Goal: a user, or an agent, draws a constrained sketch on a face, dimensions it, and turns it into a feature. Around
that, the things that make the playground a tool rather than a demo: a native kernel for speed, files and exact
output, holes on any face, and an agent that edits the same session.

Decisions (2026-10-21): the UI grows inside the web playground; the milestone covers constrained sketches, the drawing
UI, a native kernel backend, an agent chat panel, files with STEP and DXF out, and holes on any face. Plan first.

## Findings that shape the plan

- **Sketches today are inline.** `Extrude` and `Revolve` carry a `SketchPlane` (origin, x, normal) and a `Profile`: a
  start point and edges with an absolute `To` and a `Bulge`. Coordinates are absolute; there are no constraints.
- **Face names already come from the sketch.** A profile edge `top` gives the face `rib/top`. Sketch entities keep that:
  their ids become face names.
- **Dimensions can ride on expressions.** `PartDocument.Expressions` maps edit paths (`rib.profile.edges.1.to.0`) to
  expressions over parameters, and `Parametric.Resolve` turns them into numbers before regeneration.
- **A solver scheme exists.** `MateSolver` is damped Gauss-Newton with a least-squares pseudo-inverse: directions no
  mate fixes stay put, and the rank left over is the number of free degrees of freedom. A 2D sketch solver is the same
  scheme with points as unknowns.
- **Holes are Z-only and through-all** (`Hole(Id, Center[2], Diameter)`).
- **The playground protocol is transport-neutral.** Workers answer `{id, op, args}` with `{id, ok, result|error, ms}`;
  a WebSocket server can speak it unchanged.
- **The agent tools exist.** The MCP server offers `regenerate`, `apply_edits`, `query`, `validate`, `export_step`,
  `assembly` and `describe_format`.
- **Exact output exists for STEP** (`ExactBrep`, `StepWriter`). There is no DXF writer.
- **Phase 2 of `playground-plan.md`** (sketch mode S1–S6) was planned without a solver and never built. This plan
  replaces it.

## 1. Document model

A sketch is a feature of its own. Extrudes, revolves and holes refer to it.

```json
{"id": "base", "type": "sketch",
 "on": {"face": "plate/z+"},
 "points": {"a": [0, 0], "b": [60, 0], "c": [60, 40], "d": [0, 40], "h1": [8, 20]},
 "entities": [
   {"id": "bottom", "line": ["a", "b"]}, {"id": "right", "line": ["b", "c"]},
   {"id": "top", "line": ["c", "d"]},    {"id": "left", "line": ["d", "a"]},
   {"id": "axis", "line": ["a", "c"], "construction": true}],
 "constraints": [
   {"horizontal": "bottom"}, {"vertical": "right"}, {"horizontal": "top"}, {"vertical": "left"},
   {"fix": "a"},
   {"length": "bottom", "value": "=width"}, {"length": "left", "value": 40},
   {"distance": ["h1", "left"], "value": 8}, {"midpoint": ["h1", "axis"]}]}
```

- **Points carry their last solved coordinates.** Regeneration is then deterministic, the text diffs as geometry, and
  the solver starts from a good guess. The constraints are the intent; the coordinates are the solution.
- **Entities:** `line [p, q]`, `arc {center, from, to}` (counter-clockwise), `circle {center, radius}`, `point`.
  `construction: true` takes part in constraints but never in profiles.
- **Constraints (v1):** coincident, horizontal, vertical, parallel, perpendicular, tangent, equal (length or radius),
  midpoint, symmetric (about a line), point on line/arc/circle, fix; dimensions length, distance (point-point,
  point-line), horizontal and vertical distance, radius, diameter, angle.
- **Dimension values** are numbers or expressions (`"=width"`), stored through `Expressions` under paths such as
  `base.constraints.5.value`, so parameters drive sketches as they drive everything else.
- **Placement:** `on` is a plane (`{"plane": SketchPlane}`) or a face by name (`{"face": "plate/z+"}`). A face sketch
  takes the face's exact plane at regeneration, so it follows the face when the face moves. A face that is gone,
  split or not flat gives a diagnostic (`sketch-plane-lost`) naming it.
- **Using a sketch:** `extrude` and `revolve` accept `"sketch": "base"` and `"regions"`: the loops to use, each named
  by the entities round it (`["bottom", "right", "top", "left"]`), or all closed loops when omitted. The inline
  `profile` stays for documents and agents that write coordinates.
- **Names:** a side face is named after its entity (`boss/bottom`); holes from sketch points after the point
  (`holes/h1:wall`). Ids are stable once made; the UI makes `e1`, `e2`, … and lets the user rename.

## 2. Solver

`src/Isoform.Sketching`, plain C#, no UI dependency.

- **Unknowns:** the coordinates of every point not fixed, and the radius of every circle.
- **Residuals:** one or more per constraint, scaled to millimetres (angles through unit vectors), so one tolerance
  fits all.
- **Method:** damped Gauss-Newton (Levenberg-Marquardt) with a least-squares pseudo-inverse, as in `MateSolver`,
  from the stored coordinates. The least-norm step moves what is free as little as possible, so a sketch does not jump.
- **Degrees of freedom:** n − rank(J) at the solution. The null space of J gives which points can still move: entities
  are coloured free or fully constrained.
- **Problems, never silent:**
  - `sketch-conflict`: the residual stays above the tolerance. The smallest set of constraints that cannot all hold is
    found by deletion (drop one, re-solve) and named.
  - `sketch-redundant`: a constraint that changes neither rank nor residual (a warning).
  - `sketch-open`: a region asked for is not a closed loop, or loops cross.
- **Dragging:** the dragged point gets a soft, heavily weighted pull to the cursor; everything else solves round it.
- **Deterministic:** fixed ordering, no randomness. The same document gives the same coordinates bit for bit.
- **Regions:** the solved entities form a planar arrangement; its bounded faces are the regions, turned into the
  existing `Profile` (lines and bulged arcs). Extrude and revolve geometry is unchanged below that.
- **Tests:** every constraint kind holds to 1e-10 after solving from a perturbed start; canonical sketches have known
  free degrees (fully dimensioned rectangle 0, free triangle 6); conflicts are named; solving twice is bit-identical;
  drags move only what is free.

## 3. Holes on any face

- `hole` gains `on` (a flat face by name), `at` (points in the face's sketch coordinates, or `{"sketch": "base",
  "points": ["h1", "h2"]}`), and `depth` (a number, or through). The hole runs along the face's inward normal.
- Without `on` a hole is what it is today: along Z, through all. Old documents read the same.
- Faces: `holes/h1:wall`, and `holes/h1:floor` for a blind hole. A pattern of a hole copies all its points.

## 4. Drawing UI (playground)

- **Enter:** pick a flat face, then Sketch (or double-click a sketch in the tree to edit it). Orthographic camera, grid,
  the face's outline from `ExactBrep` (from the mesh lines where that does not build).
- **Tools:** line and polyline, rectangle (adds its horizontals, verticals and corners), circle, arc (centre,
  3-point, tangent), slot, point; a construction toggle.
- **Inference while drawing:** endpoints, midpoints, centres and the face's outline snap and add coincident or
  point-on; near horizontal or vertical adds that constraint; continuing from an arc adds tangent. Each shows as a
  glyph; Shift draws without inference.
- **Dimensions:** click one entity or two, place the dimension, type a number or `=expression`. The parameters panel
  lists the names an expression can use.
- **Live solve:** the solver runs in the kernel on every move. A sketch has tens of unknowns, so a solve takes well
  under a millisecond even in WebAssembly; the 3D preview of the result is what needs the native backend.
- **Status:** free entities blue, fully constrained black, conflicts red with the constraints named in the
  diagnostics panel; a free-degrees counter.
- **Constraints panel:** every constraint, hover to highlight, delete, edit a value.
- **Finish:** click inside loops to pick regions, then extrude, cut or revolve with the depth handle. Sketch and
  feature are one history entry; the agent view shows the edits.

## 5. Native kernel backend

- `src/Isoform.Server`: a minimal ASP.NET host on localhost that serves the playground and runs a `Session` per
  connection over WebSocket, speaking the worker protocol unchanged, with all cores.
- The page tries the server first and falls back to the WebAssembly workers, so a shared link still works on its own.
- Target: a full-resolution drag preview of the bench part under 100 ms (WebAssembly takes 0.3–2 s).
- Localhost only. File access is limited to one folder the user picks.

## 6. Files and output

- **Open and save** `.isoform.json` documents and bundles: the File System Access API in Edge and Chrome, upload and
  download elsewhere, plain disk access through the server.
- **STEP download** through `ExactBrep` and `StepWriter`; faces it cannot write exactly give a diagnostic naming them.
- **DXF** (R12 ASCII: `LINE`, `ARC`, `CIRCLE`) of a sketch, and of a flat face's exact outline. A small `DxfWriter` in
  `Isoform.Step`, checked by reading its output back.

## 7. Agent chat panel

- A panel where the user types and Claude answers with edits. The agent's tools are the MCP tool set on the session's
  current document, plus sketch tools (add entities and constraints, solve, report degrees of freedom). Its edits go
  through `applyEdits` with origin `agent`, so the agent view, history and undo show them like any other.
- The model call runs in the native server, with the API key from its environment; the browser never holds a key.
  Without the server there is no chat.
- Use the latest Claude model; check the API guidance when building it.
- Showcase: "A 60 by 40 bracket, 5 mm thick, two 6 mm holes 8 mm in from the short edges, 3 mm fillets on the
  corners": a constrained sketch, an extrude, holes from sketch points, fillets, with every edit and the hash visible.

## 8. Work breakdown

Each task is about half a day and owns its files.

**Phase A: kernel, no UI**
- **A1** Sketch feature: document model, canonical format, edit paths, expressions in constraint values, validation
  rules.
- **A2** Solver: residuals, Levenberg-Marquardt, degrees of freedom, conflicts, redundancy, drag.
- **A3** Regions to profiles; extrude and revolve from a sketch; face names from entities.
- **A4** Sketch on a face by name; `sketch-plane-lost`.
- **A5** Holes on any face, blind holes, holes from sketch points.
- **A6** MCP: sketch tools, format guide, examples.

**Phase B: drawing UI**
- **B1** Sketch mode: enter on a face, orthographic camera, grid, outline; edit an existing sketch.
- **B2** Tools and inference.
- **B3** Dimensions, live solve, status colours.
- **B4** Constraints panel.
- **B5** Finish: regions, extrude/cut/revolve, depth handle, one history entry.

**Phase C: native backend and files**
- **C1** `Isoform.Server` with the worker protocol over WebSocket; client fallback to WebAssembly.
- **C2** Open and save.
- **C3** STEP download.
- **C4** `DxfWriter` and DXF download.

**Phase D: agent**
- **D1** Chat panel; model call in the server; sketch tools on the session.
- **D2** Showcase script in the tour.

**Order**
1. A1, then A2 and A3 in parallel; A4 and A5 after A1; A6 after A3.
2. C1 can run alongside phase A: it touches nothing in the kernel.
3. Phase B after A1–A3. C2–C4 any time after C1.
4. D after C1 and A6.

## Risks

- **Solver behaviour users notice:** arcs flipping to the other solution, sketches jumping on a drag. Mitigated by
  solving from the stored coordinates with least-norm steps; tested with drags.
- **Face sketches when the face changes:** a moved face carries its sketch; a split or vanished face is reported, not
  guessed. Picking the piece of a split face needs the reference layer's piece references.
- **Names:** auto ids (`e7`) make poor face names. The finish step offers renaming before the feature is committed.
- **3D feedback during sketching:** without the native server the result regenerates only on finish.
- **API key:** server-side only; never in the page, the document or the share link.
- **Not in this milestone:** sketch fillets and trims, projecting part edges into a sketch, splines, sweep and loft.
