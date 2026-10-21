# Playground vNext: plan

Goal: show what the kernel does that others don't. Edits never fail silently, faces keep their names, the
document is text an agent edits, results are certified, output is exact. Phase 1 must be a strong demo on its own.

## Findings that shape the plan

- `wwwroot/main.js` holds the whole front end. `Playground.Regenerate` (`src/Isoform.Playground/Playground.cs`) returns
  faces, bodies, diagnostics, bare thin-spot points and line segments. It does not return the canonical text or hash,
  which faces a line separates, the sources at a thin spot, or per-instance transforms.
- Selection survives regeneration by face index (`same(previous.sources[f], info.faces[f])`). It should go through the
  reference layer (`ReferenceResolver`).
- `DocumentEdits.Apply` handles part documents only; nothing turns two documents into an edit list.
- The playground project is browser-only, so tests cannot reference it. New logic needs a plain library behind thin
  `[JSExport]` wrappers.
- The kernel already has what handles and picking need:
  - `CompiledPart.Surfaces[owner]`: exact geometry per face.
  - `Quad.Owner` and `RayHit.Owner`: index into it.
  - `ThinSpot.OwnerA/OwnerB/Thickness`.
  - `Profiles.FrameOf` / `Profiles.Problem`.
  - The K5 queries, and `ExactBrep` + `StepWriter`.
- Region meshing (worker pool, `RegionCache`, `SnapGrid`) points to the worker design: a primary worker owns the session,
  helpers mesh shares, and the grid is snapped while dragging.
- Moving an assembly instance needs no regeneration: parts are cached by hash, so the instance mesh is sent with its own
  matrix and the drag runs at 60 fps.

## 1. Interaction design

**Selection.**
- Click a face, or a line to get an edge (lines carry their face pair).
- Selecting a face also selects its feature: the feature's faces get a tint and its handles appear.

**Handles come from C#**, one contract for all features:

```json
{"id":"plate:x+","feature":"plate","kind":"linear","anchor":[100,25,5],"axis":[1,0,0],
 "value":100,"min":0.001,"step":1,"faces":["plate/x+"],"label":"plate width",
 "edits":[{"path":"plate.size.0","base":100,"per":[1]}]}
```

- `kind` is one of `linear`, `planar`, `angle` or `frame` (assembly instance, TransformControls).
- Each edited value is `base + per·t`.
- A field that is a bare parameter reference edits `parameters.<name>`; any other expression is read-only.

| Feature | Handles (path) |
|---|---|
| block | 6 face arrows (`origin.i`, `size.i`) |
| hole | planar centre (`center.0/1`); radial ring (`diameter`, per = 2) |
| extrude | start/end arrows (`start`, `end`); planar handle per profile vertex (`profile.edges.k.to.0/1`) |
| revolve | `angle` arc |
| fillet / chamfer | arrow on the bisector at the middle of the face (`radius` / `distance`) |
| shell / offset | arrow on the largest face (`thickness` / `distance`) |
| pattern | arrow at the last copy (`linear.spacing`), +/− count |
| assembly component | `frame` (`components.<id>.placement.*`) |

**Inspector.** Generated from the feature JSON: scrubbable numbers, a select for `op`, face-name chips with re-pick for
`edge`/`loop`.

**Drag to document.**
- The pointer gives `t`: the closest point to the handle's axis or plane, snapped and clamped. The edits are
  `[{"set":path,"value":base+per·t}]`.
- While dragging: a JS ghost draws straight away, and a latest-wins preview worker regenerates at a coarse cell. The
  result is badged "preview"; its below-resolution diagnostics are greyed out (coarse cells give false reports).
- On release: the primary worker applies the edits, checks the rules, regenerates in full, and returns the canonical
  text and hash.
- With incremental regeneration and `SnapGrid`: preview at full resolution when it runs under ~120 ms.

**Undo/redo is document history.** Each entry is `{text, hash, edits, label, origin}`.
- One drag is one entry; typing coalesces into one entry per committed regeneration.
- A git-log style panel lists the hashes, and clicking one restores it.

**Agent view.** Every commit shows:
- the edit list as JSON;
- a line diff of the text;
- `hash a1b2… → 9f8e…`.

A new `DocumentDiff.Between(old, new)` turns hand-typed changes back into edits. It guarantees
`Apply(old, Diff) == new`.

## 2. Drawing mode

**Starting a sketch.**
- Select a planar face, then press Sketch.
- `FaceFrame` refines the hit with `Queries.Raycast` and takes the exact plane of `RayHit.Owner`. The origin is the
  world origin projected onto that plane, so sketch coordinates read as world coordinates.
- `FaceOutline` gives the face's outline in sketch coordinates: exact from `ExactBrep` when it builds, otherwise from the
  mesh lines.
- The view switches to an orthographic camera with a grid and the outline.

**Tools.** Line/polyline, rectangle, circle (two arcs, bulge 1), centre/3-point/tangent arc (bulge = tan(θ/4)), slot.

**Snapping.** Grid, endpoints and midpoints, outline vertices and circle centres, H/V and 15° inference. Values can be
typed.

**Dimensions.** Click a segment and type its length or radius. There is no constraint solver yet.

**Validation.** `ValidateProfile` wraps `Profiles.Problem` and flags segments under 2 × cell.

**Finishing.** Extrude (join, cut or new body) or revolve about a picked line.
- `SketchFeature` builds the feature with authored edge ids (`bottom/right/top/left`, `a/b`, `e1…`), which can be
  renamed before committing.
- The feature is inserted, its depth handle activates, and the result is one history entry.

**Context actions.**
- Edge: fillet / chamfer.
- Face: loop fillet or chamfer, hole here (±Z faces only), shell, offset, pattern this feature.

## 3. Showcase moments (impact over effort)

| # | Moment | Phase |
|---|---|---|
| 1 | Agent view: edits, diff and hash on every change; undo returns the identical hash | 1 |
| 2 | Diagnostics in place: fly to the thin spot, both faces lit with the thickness; "Resolve at 0.25 mm" | 1 |
| 3 | Drag a fillet radius past what fits: a precise `fillet-invalid`, the rest stays valid | 1 |
| 4 | Drag a block face or hole: pinned references show Resolved / Split / Merged / Gone / Ambiguous | 1 |
| 5 | Guided tour: scripted edits like an agent, back to the original hash | 1 |
| 6 | Sketch on a face → extrude/cut with authored names | 2 |
| 7 | Assembly: drag an instance without regeneration; live interference with certified clearance; free DOF | 3 |
| 8 | STEP download: exact output, or a diagnostic naming the unsupported faces | 3 |
| 9 | Measure: certified distance interval, min-wall chord, mass and centroid | 3 |
| 10 | Wall-thickness heat map | 3 |
| 11 | Parameter sliders and per-instance overrides | 3 |
| 12 | Share link (`#doc=` deflate + base64url) | 1 |
| 13 | Live MCP bridge: watch an agent's edits stream in | 3 |

Phase 1 demo, about 2 minutes:
1. Drag `plate/x+`: one `set` in the diff, the hash changes.
2. Drag the hole towards the edge: a below-resolution thin spot with both names; resolve it at a finer cell.
3. Carry on until tangent: `tangent-contact`.
4. Drag the fillet radius up: `fillet-invalid`.
5. Undo four times: back to the original hash.

## 4. Architecture

**C#**
- `src/Isoform.Interactive` (class library, reusable by the MCP server):
  - `Session`, `ViewJson` (result v2: text, hash, thin-spot sources, `lineFaces`, instances), `MeshBuffers`;
  - `Handles` (+ `Handles.Profiles`);
  - `FacePick`, `SketchFeatures`, `ReferencePins`, `QueryJson`, `StepExport`.
  - Trim-safe JSON only: `Utf8JsonWriter` or source-generated contexts.
- `src/Isoform.Document`: `DocumentEdits` (partial) + `DocumentEdits.Assembly` (placement paths, edits inside
  bundles), `DocumentDiff`.
- `src/Isoform.Playground`: `[JSExport]` wrappers only, one partial file per area.

**Exports.** Strings in and out; meshes as MemoryView.

```
Regenerate(doc, editsJson, cell, chord, slot, snap)
Apply(doc, editsJson)                      → {text, hash, problem}
Diff(old, new)
Handles(slot)
FaceFrame(face, p, d)
FaceOutline(face)
ValidateProfile(json)
SketchFeature(json)
Resolve(refsJson)
Distance(p)
MinimumWall()
Mass(density)
Clearances(within)
WallMap()
Step()
```

**Worker protocol.** Requests are `{id, op, args}` and replies `{id, ok, result|error, ms}`.
- `kernel-client.js` offers `call(op, args)` and `latest(channel, op, args)`: one request in flight per channel, stale
  replies dropped.
- Two workers: a primary worker and a preview worker. Once the pool lands, helpers mesh shares.

**Front end.** Static ES modules, no build step. three r0.160.0 is vendored into `wwwroot/vendor/three/` (pins the
TransformControls API and lets tests run offline).

```
wwwroot/js/  main  kernel-client  store  viewer  picking  handles  ghosts
            lib/{diff,dragmath,share}        sketch/{mode,tools,profile,snapping,finish}
            panels/{editor,agent,history,tree,inspector,diagnostics,references,measure,assembly,tour,context,export,parameters}
```

**Testing.**
- **C#:** `tests/Isoform.Tests/Interactive`.
  - Handle contract over all examples: apply t = 0.5, then the named faces moved by 0.5 along the axis.
  - `Apply(old, Diff(old,new)) == new`.
  - Every example exports STEP or returns unsupported, never throws.
- **Node** (`node --test`): the pure modules, plus the published wasm driven through the protocol.
- **Headless Edge** over the DevTools protocol: open an example, read handle positions from `window.isoform`, drag with
  dispatched mouse events, and assert the edits, hash, diagnostics and undo; save screenshots.

## 5. Work breakdown (each task about half a day, each owns its files)

**Phase 0**
- **F1** Interactive lib + session + result JSON v2.
- **F2** JS module split, vendored three, kernel-client, store; selection by name.
- **F3** Test harness: Node and headless Edge scripts.

**Phase 1**
- **H1** Handles for block, hole, extrude start/end, shell, offset.
- **H2** Handles for fillet/chamfer, revolve, pattern, profile vertices.
- **E1** Assembly/bundle edits + `Apply` export.
- **E2** `DocumentDiff`.
- **J1** Handle rendering + drag math.
- **J2** Preview/commit pipeline, history, ghosts.
- **J3** Agent panel + editor flash.
- **J4** Feature tree + inspector.
- **J5** Diagnostics made visual.
- **R1** Reference pins.
- **T1** Tour, demo examples, share link.

**Phase 2**
- **S1** `FaceFrame` / `FaceOutline`.
- **S2** `ValidateProfile` / `SketchFeature`.
- **S3** Sketch mode shell.
- **S4** Tools + profile math.
- **S5** Snapping + typed dimensions.
- **S6** Finish with depth handle.
- **C1** Context menus.

**Phase 3**
- **Q1** Query exports.
- **Q2** Measure tool + mass panel.
- **Q3** Wall heat map.
- **X1** STEP download.
- **A1** Assembly drag + live clearances.
- **P1** Parameter sliders.
- **I1** Pool + incremental previews.
- **M1** MCP bridge.

**Order.**
1. F1–F3 in parallel.
2. H1, H2, E1, E2, J1, J3 and R1 in parallel.
3. J2 after F2 and J1; J4 and J5 after F2; T1 closes phase 1.
4. Phase 2 needs H1 and F1.

## Risks

- **Live dragging in the browser (0.3–2 s per regeneration):** relies on ghosts, latest-wins and coarse previews.
  Coarse cells give false below-resolution reports, so they are labelled and hidden. Until `SnapGrid`, the lattice
  shimmers while dragging.
- **Picking on the mesh** is only accurate to the chord error. Planes, snaps and anchors come from the exact surfaces;
  edges are picked by face pair.
- **TransformControls** only for assembly frames; feature handles are custom. Pin three r160.
- **Merged faces** need a chooser. Renaming a feature breaks references (rename-with-references later).
- **Parameters:** handles must not overwrite expressions.
- **Kernel limits that become visible:** holes are Z-only; revolve needs x ≥ 0; cuts need an overshoot; STEP does not
  cover loop fillets yet.
- **Memory:** two or more wasm instances; free preview results.
- **No constraint solver** for sketch dimensions; say so in the UI.
