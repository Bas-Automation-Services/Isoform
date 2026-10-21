# Playground tests

Checks the playground: the front end's pure modules and the published WebAssembly kernel in Node, and the page in
headless Edge or Chrome. Needs Node 22 or later (built-ins only, no `npm install`) and, for the browser run, Edge or
Chrome.

```sh
node tests/playground/run.mjs --publish            # publish to out/playground, then test it (publish takes ~2 min)
node tests/playground/run.mjs                      # test what is already in out/playground/wwwroot
node tests/playground/run.mjs path/to/wwwroot      # or any other published wwwroot (or the folder above it)
node tests/playground/run.mjs --server             # the browser run against the native kernel server
```

The parts on their own:

```sh
node --test tests/playground/{kernel-client,store,diagnose,references,diff,history,dragmath,features,preview,share,sketch}.test.mjs   # front end only, no publish
node --test tests/playground/kernel.test.mjs       # kernel only; ISOFORM_PLAYGROUND=<wwwroot> to point elsewhere
node --test tests/playground/server.test.mjs       # native kernel server; builds and starts one unless ISOFORM_SERVER=<its url>
node tests/playground/e2e.mjs [wwwroot]            # browser only; ISOFORM_SERVER=<url>: the page from a running server
node tests/playground/sketch.e2e.mjs [wwwroot]     # sketch mode in the browser, the same way
node tests/playground/serve.mjs [wwwroot] [port]   # serve the page to look at it yourself (default port 8137)
```

Environment: `ISOFORM_BROWSER` is the browser executable when Edge or Chrome is not in its standard location;
`ISOFORM_TIMEOUT` is how long, in ms, the browser run waits for one example (default 120000). three.js is vendored, so
nothing needs a network connection.

## What is checked

**`kernel-client.test.mjs`** drives `wwwroot/js/kernel-client.js` with fake workers: requests `{id, op, args}` and
replies `{id, ok, result|error, ms}`; `latest(channel, …)` keeps one request in flight, lets the newest waiting one win
and drops stale replies; `regenerate` fans out to the pool and only the primary's reply counts; `start` links the
helpers to the primary; `adaptResult` keeps result v2 as written and adds the mesh buffers.

**`store.test.mjs`**: the selection is face names, so it follows a face that is renumbered and drops one that is gone;
a document that does not read keeps the last result; history gets one entry per changed document; the status line has
the form `e2e.mjs` reads.

**`dragmath.test.mjs`** drives `wwwroot/js/lib/dragmath.js` with the handles in `handles.fixture.json` (the kernel's
handle JSON, `src/Isoform.Interactive/README.md`; `kernel.test.mjs` checks it against the kernel): the pointer ray gives
`t` as the closest point to a linear handle's axis, the hit on a planar handle's plane along the kernel's `u` and `v`
(as given, whatever their handedness), or the angle about an angle handle's axis through its `center`; a drag counts
from where it was grabbed and an angle keeps turning past half a turn; the value snaps to `step` (a plane snaps both
its values, shift a tenth of the step) and `edits[0]` stays within `min`/`max`; the edits are `{set, value:
base + per·t}`; a pattern's −/+ step its `count`; a face's feature is its name up to the first `/` (a pattern copy's
`row/2:bore/wall` is `row`'s); a selected face shows its feature's handles and never an assembly frame.

**`preview.test.mjs`** drives `wwwroot/js/lib/preview.js` (the drag preview) with a fake preview worker: a drag first
regenerates the committed document unchanged (not shown, it warms the region cache), then each move regenerates the
edits in the `preview` slot with the lattice snapped; one request in flight, the newest waiting one wins, and every
reply that comes back shows (`store.preview`, `preview: true`) until the drag ends; after the release the last
preview stays until it is dropped. The cell is the committed one while a warm preview keeps within 120 ms, else twice
(then four times) as coarse, never finer during a drag; the next drag of a handle starts a step finer when its
previews ran in a fifth of the budget.

**`diagnose.test.mjs`** (`wwwroot/js/lib/diagnose.js`): diagnostics grouped by code with readable titles, errors
first; the face names a message mentions (whole names only), else for a feature that left no faces the ones its
definition names (a fillet's edge); a thin spot's thinnest point; the cell that resolves a below-resolution report
(size / 2, at least half the current cell, on a round value: a 0.6 mm wall at 0.5 mm cells gives 0.25 mm); preview
results grey out their below-resolution reports; the valid / N problems verdict.

**`references.test.mjs`**: what a selection pins (an edge, a face by name, or by index when it is the face
clicked), and the kernel's replies folded into the pins by id.

**`diff.test.mjs`**: the line diff (`wwwroot/js/lib/diff.js`) accounts for both texts in order, is shortest (a moved
line costs two), numbers lines in their own text; `hunks` keeps context around changes, `changedLines` gives the lines
the editor flash lights.

**`history.test.mjs`** drives `lib/history.js` and `lib/commit.js` over the real store and a fake kernel:
`applyEdits` pushes one entry with its edits, label and origin and flashes the editor; edits that do not apply commit
nothing and set `problem` and `rejected`; undo back to the start gives the identical hash, repeated undo keeps stepping,
a commit after undo drops the redo entries; a typed entry gets its edits from the kernel diff.

**`features.test.mjs`** (`wwwroot/js/lib/features.js`, the tree and inspector model): the tree lists a part's
features in order, grouped by the body each works on when there are several, and a bundle's components and mates; a
face belongs to the item its name starts with; the inspector's fields come from the feature JSON, each with the
`DocumentEdits` path it sets (`rib.profile.edges.0.to.1`, `components.pin.placement.origin.2`, `parameters.width`);
typed values and scrubbing give numbers or `=expressions`.

**`share.test.mjs`** (`wwwroot/js/lib/share.js`, share links): `#doc=` holds the deflate-raw of the canonical text in
base64url and `&hash=` its first 16 hex digits. Text packs and unpacks to itself (UTF-8 included); the packed form is
URL-safe; a 400 kB document compresses to under a fifth and comes back exactly; a link drops the page's query and old
fragment; a fragment without `doc` reads as nothing, and a broken pack is rejected.

**`sketch.test.mjs`** (`wwwroot/js/sketch/`, sketch mode's pure parts): ids are the first free `p1`, `p2`, … and
`e1`, `e2`, … over points and entities alike, and features get `extrude1`, `sketch1`, …; each tool's entities and
constraints (a rectangle's four lines round shared corners with two horizontals and two verticals, a circle through a
snapped point gets `on`, an arc is stored counter-clockwise with its end on the radius, a slot's lines are tangent to
both arcs and the arcs equal); a pick on a point reuses it (the format's coincident); removing, renaming (every
reference follows; the kernel's name rules) and construction; the constrain buttons per selection. Inference snaps to
points, then the origin (a new point fixed there), midpoints, lines and arcs (only where the arc runs), then level and
plumb with the segment's start; Shift turns it off; a line on from an arc's end along its tangent, and an arc from a
line's end centred on its normal, get tangent. Dimensions: what fits one or two picks, what they measure, typed values
(numbers above 0, `=expressions` kept, an angle keeps the sense it has), labels off the geometry away from the
sketch's middle. Regions: a click inside the outer loop and outside its holes, loops chained whatever their order and
direction, arcs sampled. Colours: free, fixed and conflicting entities and the summary line. The finish: a new sketch
is inserted with its feature after it (extrude `0…depth`, cut `−depth…1`, revolve with its axis; `op: new` when the
document has no body yet), an edited one is one `set`; the working document holds the sketch in its place or at the
end; the view maps the plane with x right and y up and zooms about the pointer.

**`sketch.e2e.mjs`** drives sketch mode in the browser, against the published folder or (`ISOFORM_SERVER`) the native
server. On example 0 it selects `plate/z+`, presses the Sketch button, draws a rectangle with two clicks (four lines,
two horizontal and two vertical, 4 free degrees), clicks its bottom line and a spot below it with the Dimension tool
and types 20 in the value box (the line becomes 20 long, 3 free degrees, one region), picks the region in the Select
tool, sets the depth to 5 and finishes: the document gains `sketch1` and `extrude1` as one history entry of origin
`sketch` with two inserts, and the part has the faces `extrude1/e1`…`e4` and `extrude1/end` with no sketch diagnostic
(`out/screenshots/sketch-draw.png`, `sketch-extrude.png`). Then it double-clicks `sketch1` in the tree, sets the
dimension to 30 in the panel and saves: one history entry with one `set`, the stored points 30 apart. Last, S on a
selected face starts a new sketch, a line is drawn, and Escape (pressed until the mode ends) leaves the document's
hash as it was.

**`kernel.test.mjs`** loads `_framework/dotnet.js` from the published folder in Node and calls the same exports the
page's worker calls. For every example from `Examples()`, at the page's default settings (0.5 mm cells, 0.05 mm chord):

- `Regenerate` returns result v2 (`src/Isoform.Interactive/README.md`) with no `error`, the example's own canonical
  text and a hash, at least one face and triangle, and no `invalid-parameter` diagnostic;
- `Positions()` and `Faces()` hold three corners and one face index per triangle, the line buffers two end points and
  one `lineFaces` index per segment.

It prints the kernel time, triangles, faces and diagnostics per example. A broken document must come back as
`{error}`, not as an exception, and a bad edit as `{error, failedEdit}`. `Apply(old, Diff(old, new))` must give `new`.
`Handles(slot)` gives the handles of that slot's last regeneration, with its hash.
`Resolve` pins `plate/z+` by index, returns it by name, and reports it split after a cut across it.
`FaceFrame` gives `plate/z+`'s plane (and an error for a face that is not flat), and `Sketch` solves a triangle on it
with a point dragged: free degrees, solved points and its one region.

**`server.test.mjs`** drives the native kernel server (`src/Isoform.Server`, started by `server.mjs` on a free port)
through the page's own `connectKernel` and `createClient`: every example regenerates to result v2 whose mesh buffers
agree with its counts (positions and lines as `Float32Array`, indices as `Int32Array`, as the worker gives them), with
the same hash, faces and diagnostic codes as the wasm kernel; a broken document and a bad edit come back as `{info:
{error}}` without a mesh, an unknown op as a failed reply; `apply`, `diff`, `handles` and `resolve` answer exactly as
the wasm exports do; a second connection is a session of its own (the preview slot is not the main one's, and not
refined). `kernel-client.test.mjs` checks `unpack`, the reading of the server's binary reply.

**`e2e.mjs`** serves the folder with `serve.mjs` (with `ISOFORM_SERVER`, the native server serves it instead, and each
example checks that the page uses the native kernel, `window.isoform.kernel`), opens `?example=N` for every example and waits (in real time) for the
stats line under the editor (`#stats`) to show a result. An example passes when that line names one or more faces,
does not start with `Error:` or say the document does not parse, and the page logged no uncaught exception or console
error. Per example it prints the kernel time from the stats line and the page time (navigation to result, including
loading the runtime). Then it clicks a face of example 0, regenerates at another cell size and checks that the same
face names are still selected. Last, diagnostics: example 0 with its hole moved to leave a 0.6 mm wall reports a
below-resolution thin spot between `plate/y+` and `hole/wall`; clicking it in the list lights both faces in two colours
and flies the camera there (`out/screenshots/diagnostics.png`); "Resolve at 0.25 mm" regenerates the same document at
0.25 mm cells with the report gone and the verdict Valid; "back to 0.5 mm" brings it back; a fillet radius of 12 mm gives
`fillet-invalid`, lights the edge it names and keeps the rest of the part. Then pins: `plate/z+` pinned on example 0
(`window.isoform.pin`), a slot edited in across it, and the pin shows `split`, lit when clicked (`out/screenshots/pins.png`).
Then the history on example 0: `applyEdits` commits one entry (agent view shows its hash and edit, the editor
flashes), an edit that does not apply leaves the hash alone, undo returns the original hash and redo the edited one
(`out/screenshots/history.png`).
Last it selects `plate/x+` on example 0, drags its handle 40 px outwards with mouse events, and checks that
`plate.size.0` grew and the hash changed (skipped while the kernel has no `Handles` export).
Then the tree and inspector: it selects `hole` in the tree, types a diameter and scrubs it (each must change
the document and its hash, and keep `hole` selected), re-picks the fillet's edge, and checks that selecting a face
selects its feature in the tree.
Then drags (`drags.mjs`), one per handle kind in the examples: the hole centre (planar) along its `u`, the fillet
radius outwards, the turned shaft's revolve angle back from 360°, and `+` on the bolt row's count; each must change its
field the right way, and while it drags a preview must be on screen, badged, with the ghost moved from where the
preview was made to the pointer (and shown before any preview is back). Last the phase-1 demo of
`docs/playground-plan.md` as one scenario on example 0: drag `plate/x+` (one `set` in the agent view), drag the hole
towards the y+ edge with shift until a below-resolution thin spot names `plate/y+` and `hole/wall`, resolve it, carry
on to y = 45 (`tangent-contact`), drag the fillet radius past 11 mm (`fillet-invalid`), undo four times to the original
hash. It prints what the previews took per cell and the wasm memory of the primary and preview kernels.

Then the guided tour (`wwwroot/js/panels/tour.js`) on example 0: starting it pins its four faces; it steps to the end
with `tour.next()` (once Back and Next again, which must return to the step before's hash), and every step in its
`script` must have reported its `expect` code and not its `gone` one, with the pin statuses it expects (`plate/y+`
split at tangent, `fillet/face` gone at fillet-invalid); the edits are history entries of origin `tour`, and the last
undo shows the hash the tour started from. Escape closes it. Screenshots `tour-2.png` (thin spot), `tour-5.png`
(fillet-invalid) and `tour-9.png` (back at the original hash). Last, a share link: example 0 with an edit, its
`#doc=` link (the Share button puts it in the address bar), opened in the page again, gives the same hash and says so.

## Screenshots

`tests/playground/out/screenshots/example-N.png` (git-ignored), 1500×900, one per example, numbered as in `?example=N`,
taken two frames after the result arrived. Each shows the editor on the left with the stats line at the bottom, and
the part on the right, framed to fit, one colour per face, sharp edges dark and tangent edges faint. Things to look
for: a part missing or cut off (framing), faces that share a colour where they should not (face naming), holes in the
mesh, stray triangles, and red diagnostics under the stats line. `handle-drag.png` is example 0 in the handle drag, just
before the release: the plate tinted, its handles drawn over it, and the dragged arrow's label with the live width.
`drag-planar.png`, `drag-angle.png` and `drag-count.png` are the handle kinds mid-drag (the preview badge in the
verdict, the ghost in amber); `demo-*.png` the demo's steps in order.

## Writing browser tests

`browser.mjs` is the layer for further tests (drag handles, undo, the agent panel). `launch()` gives a browser,
`newPage()` a tab with:

- `open(url)`, `evaluate(expression)` (awaits promises, returns plain values), `waitFor(expression, { timeout })`;
- `click([x, y], { button, modifiers })`, `drag(from, to, { steps })`, `mouse(type, point, options)` for anything
  else, `center(selector)`;
- `frames(n)` before a screenshot, `screenshot(file)`, and `errors` (exceptions and console errors since `open`).

Coordinates are CSS pixels in the viewport. The page exposes `window.isoform`: `ready` (a result is shown), `kernel` (`'native'` or `'wasm'`),
`document()`, `hash()`, `selection()` (face names), `faces()` (each face's names), `select(names)` and `edit(text)`
(resolves once that text has regenerated); for diagnostics `diagnostics()` (as listed), `focusDiagnostic(index)`,
`marked()` (the faces lit for the one in focus, with their colours), `cell()` (the cell the result was made at),
`target()` (where the camera looks) and `resolve(cell)`; for reference pins `pins()` and `pin(name | [a, b])` (resolves to the pin); for commits `applyEdits(edits, label, origin)`, `undo()`, `redo()` (each
resolves to `{hash}`, `{error, failedEdit?}` or null) and `history()` (`{at, entries: [{hash, edits, label, origin}]}`); for the tree, `feature()` (the selected item's key);
for handles `handles()`: the shown handles (those of the
selected face's feature), each `{id, feature, kind, label, value, faces, readOnly, count, screen, direction}` with `screen`
the point to grab it by and `direction` the unit screen direction of its axis (where `t` grows; a planar handle also
has `directionV`, its `v`'s), `dragging()` (the drag under way: `{id, t, value, edits}`, or null), `preview()` (the
preview on screen: `{cell, ms, t, handle, hash, diagnostics}`, or null), `previewTimings()`, `ghost()` and `memory()`.
For the tour, `tour`: `start()`, `next()`, `back()` (each resolves to that step's log entry), `play()`, `pause()`,
`exit()` and `state()` (`{active, index, count, playing, busy, original, log: [{index, title, hash, before, codes, ok,
problems}]}`); for share links `shareLink()` (resolves to the link to the shown document) and `shareStatus()`.
For sketch mode, `sketch`: `active()`, `enter()` (on the selected face), `edit(id)`, `tool(name)`, `toScreen([u, v])`
(a sketch point in viewport pixels, to click at), `state()` (`{tool, sketch, editing, selection, picked, solved,
freeDegrees, problems, regions, finish, current}`, `current` once the last change is solved), `finishChanged(patch)`,
`finish()` and `cancel()`.
Later tasks add to it. For example:

```js
const page = await browser.newPage();
await page.open(`${site.url}?example=0`);
await page.waitFor('window.isoform?.ready');
await page.evaluate(`window.isoform.select(['plate/x+'])`);
const h = await page.waitFor(`window.isoform.handles().find(h => h.id === 'plate:x+')`);
await page.drag(h.screen, [h.screen[0] + 40 * h.direction[0], h.screen[1] + 40 * h.direction[1]]);
await page.waitFor(`!window.isoform.document().includes('"size": [100, 50, 10]')`);
```
