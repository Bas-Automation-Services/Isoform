# Isoform.Interactive

The kernel behind an interactive front end, with no browser dependency: `Session` regenerates documents and keeps the
last good result per slot, `ViewJson` writes results as JSON, `MeshBuffers` holds the mesh as flat arrays. The
playground (`src/Isoform.Playground`) wraps it in `[JSExport]`s; tests and the MCP server use it directly.

## Exports

Strings in and out; meshes as MemoryViews (copy them before the next call). In the worker protocol a request
`{id, op, args}` names an export as `op` with its arguments in order as `args`; the reply is `{id, ok, result|error, ms}`.

| Export | Returns |
|---|---|
| `Regenerate(doc, editsJson, cell, chord, slot, snap)` | result v2 (below); `editsJson` is an edit, an array of edits, or `[]`; `snap` puts the lattice on the world grid (`ModelingSettings.SnapGrid`): a drag's previews |
| `Positions()`, `Faces()` | last regeneration: 9 doubles and one face index per triangle |
| `Lines()`, `LineEdges()` | sharp segments: 6 doubles each, and per segment its index in `lineFaces` |
| `SmoothLines()`, `SmoothLineEdges()` | the same for tangent segments |
| `Apply(doc, editsJson)` | `{text, hash}`, or `{problem, failedEdit?}` |
| `Diff(old, new)` | `{edits}` with `Apply(old, edits).text` the canonical `new`; `{error}` if one does not read |
| `Handles(slot)` | `{slot, hash, handles}`: the drag handles of the slot's last good regeneration (below) |
| `Examples()` | `{name: canonical text}` |
| `Resolve(refsJson, slot)` | pinned references against the slot's last good result (below) |
| `Share(doc, editsJson, cell, chord, snap, index, count)`, `Import(bytes)`, `Sweep()` | the worker pool's region exchange |

Slots: `"main"` for what is committed, `"preview"` for a drag in progress (any name works). A failed regeneration
leaves its slot's last good result in place but empties the buffers. The region cache keeps what the last regeneration
used, so a preview belongs in a worker of its own.

## Result v2

```jsonc
{
  "version": 2,
  "slot": "main",
  "format": "isoform/0",              // or "isoform-bundle/0"
  "hash": "9f8e…",                    // SHA-256 of text
  "text": "{\n  \"format\": …",       // canonical text of the edited document
  "diagnostics": [{"code": "below-resolution", "feature": "hole", "message": "…"}],   // feature may be null
  "faces": [["plate/z+"], ["hole/wall"], …],          // per face index: its source names (bundle: "lower/plate/z+")
  "bodies": [{"name": "plate", "faces": [0, 1, …]}],  // bundle: one per instance, in instance order
  "edges": 24, "vertices": 16,                        // topology counts
  "triangles": 41234, "lines": 3120, "smoothLines": 410,   // buffer lengths in triangles and segments
  "lineFaces": [[0, 3], [0, 5], …],                  // face pairs (lower first) the lines separate: the edges
  "thinSpots": [{"at": [x, y, z], "faces": ["plate/x-", "hole/wall"], "thickness": 0.2}],
  "instances": [{"id": "upper", "part": "bracket.json", "hash": "…", "matrix": [16 numbers]}],  // bundle only, else []
  "refinement": 3,                                     // ModelingSettings.Refinement: cells may be halved locally this often (0 for a preview)
  "refined": [{"min": [x, y, z], "max": [x, y, z], "cell": 0.125}],   // boxes meshed with a finer cell; parts only
  "reused": 120, "meshed": 8,                          // regions from the cache / meshed for this run
  "timing": {"edits": 1.2, "regenerate": 310.5, "buffers": 12.0}   // ms
}
```

On failure: `{"version": 2, "slot", "error", "failedEdit"?, "timing"}`; `failedEdit` (from 0) when an edit could
not be applied, absent when the document itself does not read.

- A picked line: segment `s` of `Lines()` separates faces `lineFaces[LineEdges()[s]]`.
- `matrix` is the instance placement, column-major as three.js `Matrix4.fromArray` reads it. The mesh is already
  in world coordinates; the matrix is for moving an instance without regenerating.
- Thin spots name their faces by source, so the front end lights both faces and shows the thickness.

## Handles

`Handles.Of(regeneration)` (export `Handles(slot)`, written by `HandleJson`) gives one contract for every feature:
where a handle sits, how it moves, and which document fields it edits. Dragging it by `t` sets each edit's `path` to
`base + per·t`.

```jsonc
{
  "slot": "main",
  "hash": "9f8e…",                         // the regeneration the handles belong to; absent (and no handles) if none
  "handles": [
    {"id": "plate:x-", "feature": "plate", "kind": "linear", "label": "plate width",
     "anchor": [0, 24.8, 4.7], "axis": [-1, 0, 0],   // world coordinates; the anchor is on the face's exact surface
     "value": 100, "min": 0.001, "step": 1,          // value of edits[0]; min/max (when given) bound it, step snaps it
     "faces": ["plate/x-"],                          // what the handle moves, by source name
     "edits": [{"path": "plate.size.0", "base": 100, "per": [1]}, {"path": "plate.origin.0", "base": 0, "per": [-1]}]},
    {"id": "hole:center", "kind": "planar", "axis": [0, 0, 1], "u": [1, 0, 0], "v": [0, 1, 0], "value": [30, 25],
     "edits": [{"path": "hole.center.0", "base": 30, "per": [1, 0]}, {"path": "hole.center.1", "base": 25, "per": [0, 1]}], …},
    {"id": "pin:angle", "kind": "angle", "axis": [0, 0, 1], "center": [0, 0, 12], "value": 360, "max": 360, …},
    {"id": "row:spacing", "kind": "linear", …, "count": {"path": "row.linear.count", "value": 3, "min": 2}},
    {"id": "upper:placement", "feature": "upper", "kind": "frame", "anchor": […], "axis": […], "matrix": [16 numbers],
     "faces": [], "edits": [{"path": "components.upper.placement.origin", "base": [0, 20, 15]},
                            {"path": "components.upper.placement.x", "base": [1, 0, 0]},
                            {"path": "components.upper.placement.normal", "base": [0, 0, -1]}]},
    {"id": "plate:z+", "kind": "linear", …, "edits": [], "readOnly": true, "expression": "depth"}
  ]
}
```

- `t`: for `linear` the drag along `axis`; for `planar` the pair (along `u`, along `v`) in the plane with normal `axis`,
  so `per` and `value` have two entries; for `angle` degrees round `axis` through `center`, right-handed.
- `frame` (an assembly component) is dragged with TransformControls: its edits have no `per`; set `origin`, `x` (the
  matrix's first column) and `normal` (its third) from the moved matrix. `matrix` is the solved placement, column-major
  as in result v2; `feature` is the component (and instance) id. Frames have no `value` or `step`.
- A field that is a bare reference to a constant parameter (`"=width"` with `width: 100`) edits `parameters.width`.
  Any other expression, or a parameter that is itself an expression, makes the handle read-only: no edits, and
  `expression` says which. So does one parameter behind two fields that would move differently.
- `count` (patterns): the copy count for +/−; `path` is null when an expression sets it.
- Handles sit on the feature's own instance; pattern copies have none of their own.

| Feature | Handles (`id` after `feature:`) | Edits |
|---|---|---|
| block | `x-` … `z+`: an arrow on each face | `size.i` (per 1); a low face also `origin.i` (per −1) |
| hole | `center`: planar on the axis at the top of the wall; `diameter`: arrow on the wall | `center.0/1`; `diameter` (per 2) |
| extrude | `start`, `end`: arrows on the caps; `corner:<edge>` per profile corner (where the edge ends), planar at the end cap | `start` (per −1), `end`; `profile.edges.k.to.0/1`, the last corner also `profile.start.0/1` |
| revolve | `angle` at the end of the sweep; `corner:<edge>` in the sketch plane | `angle`; as extrude |
| fillet / chamfer | `radius` / `distance`: arrow on the bisector at the middle of the face (a loop's largest) | per sin(φ/2)/(1 − sin(φ/2)) / 1/cos(φ/2), for faces φ apart |
| shell | `thickness`: arrow on the largest outer face, pointing in | `thickness`; moves `<shell>/inner:<face>` |
| offset | `distance`: arrow on the largest moved face, outward | `distance` |
| lattice | `thickness`: arrow on the lattice face, along its normal; `cell`: arrow on it, away from the origin; `skin` as a shell's thickness | `thickness` (struts per 2), `cell` (per cell/\|q\| for the foot q of the strut's line: the lattice scales about the world origin), `skin`; a sheet lattice's arrows move its level set to first order only and name no face |
| pattern | `spacing` (linear), `angle` (circular below 360°), `plane` (mirror): at the last copy | `linear.spacing` (per 1/(count − 1)), `circular.angle`, `mirror.point.i` (per nᵢ/2) |
| assembly component | `placement`: a frame at the instance | `components.<id>.placement.origin/x/normal` |

## Reference pins

`ReferencePins` resolves pinned faces and edges through the kernel's reference layer (`ReferenceResolver`): by source
name, never by face index, so a pin says what an edit did to its face. `Resolve(refsJson, slot)` takes an array of pins:

```jsonc
[{"id": "p1", "face": "plate/z+"},                       // every piece of a source face
 {"id": "p2", "face": "plate/y+", "signature": ["…"]},   // one piece, by the sources around it
 {"id": "p3", "edge": ["plate/z+", "hole/wall"]},        // every edge between two sources
 {"id": "p4", "face": 7}, {"id": "p5", "edge": [3, 5]}]   // by face index: pinned on this result first
```

and answers `{slot, hash, pins: [{id, ref, status, faces, edges?}]}`, or `{error}` when the slot has no result or a pin
does not read. `ref` is the pin by name (keep it for the next edit); a face pinned by index is pinned as a piece only
when its source is already in pieces. `status` is `resolved`, `split` (`faces` lists every piece), `merged` (the face
is shared with another source), `gone` or `ambiguous` (a piece that can no longer be told apart; `faces` lists the
candidates). `edges` are the face pairs of the resolved edges, as in `lineFaces`. `ReferencePins.Report(before,
after)` is the same per source over a whole result: what the MCP server's `apply_edits` reports as `references`.
