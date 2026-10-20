# Isoform.Interactive

The kernel behind an interactive front end, with no browser dependency: `Session` regenerates documents and keeps the
last good result per slot, `ViewJson` writes results as JSON, `MeshBuffers` holds the mesh as flat arrays. The
playground (`src/Isoform.Playground`) wraps it in `[JSExport]`s; tests and the MCP server use it directly.

## Exports

Strings in and out; meshes as MemoryViews (copy them before the next call). In the worker protocol a request
`{id, op, args}` names an export as `op` with its arguments in order as `args`; the reply is `{id, ok, result|error, ms}`.

| Export | Returns |
|---|---|
| `Regenerate(doc, editsJson, cell, chord, slot)` | result v2 (below); `editsJson` is an edit, an array of edits, or `[]` |
| `Positions()`, `Faces()` | last regeneration: 9 doubles and one face index per triangle |
| `Lines()`, `LineEdges()` | sharp segments: 6 doubles each, and per segment its index in `lineFaces` |
| `SmoothLines()`, `SmoothLineEdges()` | the same for tangent segments |
| `Apply(doc, editsJson)` | `{text, hash}`, or `{problem, failedEdit?}` |
| `Diff(old, new)` | `{edits}` with `Apply(old, edits).text` the canonical `new`; `{error}` if one does not read |
| `Examples()` | `{name: canonical text}` |
| `Resolve(refsJson, slot)` | pinned references against the slot's last good result (below) |
| `Share(doc, editsJson, cell, chord, index, count)`, `Import(bytes)`, `Sweep()` | the worker pool's region exchange |

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
