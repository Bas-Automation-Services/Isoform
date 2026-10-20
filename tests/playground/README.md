# Playground tests

Checks the playground: the front end's pure modules and the published WebAssembly kernel in Node, and the page in
headless Edge or Chrome. Needs Node 22 or later (built-ins only, no `npm install`) and, for the browser run, Edge or
Chrome.

```sh
node tests/playground/run.mjs --publish            # publish to out/playground, then test it (publish takes ~2 min)
node tests/playground/run.mjs                      # test what is already in out/playground/wwwroot
node tests/playground/run.mjs path/to/wwwroot      # or any other published wwwroot (or the folder above it)
```

The parts on their own:

```sh
node --test tests/playground/kernel-client.test.mjs tests/playground/store.test.mjs tests/playground/diagnose.test.mjs \
  tests/playground/features.test.mjs   # front end only
node --test tests/playground/kernel.test.mjs       # kernel only; ISOFORM_PLAYGROUND=<wwwroot> to point elsewhere
node tests/playground/e2e.mjs [wwwroot]            # browser only
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

**`diagnose.test.mjs`** (`wwwroot/js/lib/diagnose.js`): diagnostics grouped by code with readable titles, errors
first; the face names a message mentions (whole names only), else for a feature that left no faces the ones its
definition names (a fillet's edge); a thin spot's thinnest point; the cell that resolves a below-resolution report
(size / 2, at least half the current cell, on a round value: a 0.6 mm wall at 0.5 mm cells gives 0.25 mm); preview
results grey out their below-resolution reports; the valid / N problems verdict.

**`features.test.mjs`** (`wwwroot/js/lib/features.js`, the tree and inspector model): the tree lists a part's
features in order, grouped by the body each works on when there are several, and a bundle's components and mates; a
face belongs to the item its name starts with; the inspector's fields come from the feature JSON, each with the
`DocumentEdits` path it sets (`rib.profile.edges.0.to.1`, `components.pin.placement.origin.2`, `parameters.width`);
typed values and scrubbing give numbers or `=expressions`.

**`kernel.test.mjs`** loads `_framework/dotnet.js` from the published folder in Node and calls the same exports the
page's worker calls. For every example from `Examples()`, at the page's default settings (0.5 mm cells, 0.05 mm chord):

- `Regenerate` returns result v2 (`src/Isoform.Interactive/README.md`) with no `error`, the example's own canonical
  text and a hash, at least one face and triangle, and no `invalid-parameter` diagnostic;
- `Positions()` and `Faces()` hold three corners and one face index per triangle, the line buffers two end points and
  one `lineFaces` index per segment.

It prints the kernel time, triangles, faces and diagnostics per example. A broken document must come back as
`{error}`, not as an exception, and a bad edit as `{error, failedEdit}`. `Apply(old, Diff(old, new))` must give `new`.

**`e2e.mjs`** serves the folder with `serve.mjs`, opens `?example=N` for every example and waits (in real time) for the
stats line under the editor (`#stats`) to show a result. An example passes when that line names one or more faces,
does not start with `Error:` or say the document does not parse, and the page logged no uncaught exception or console
error. Per example it prints the kernel time from the stats line and the page time (navigation to result, including
loading the runtime). Then it clicks a face of example 0, regenerates at another cell size and checks that the same
face names are still selected. Last, diagnostics: example 0 with its hole moved to leave a 0.6 mm wall reports a
below-resolution thin spot between `plate/y+` and `hole/wall`; clicking it in the list lights both faces in two colours
and flies the camera there (`out/screenshots/diagnostics.png`); "Resolve at 0.25 mm" regenerates the same document at
0.25 mm cells with the report gone and the verdict Valid; "back to 0.5 mm" brings it back; a fillet radius of 12 mm gives
`fillet-invalid`, lights the edge it names and keeps the rest of the part. Then the tree and inspector: it selects
`hole` in the tree, types a diameter and scrubs it (each must change the document and its hash, and keep `hole`
selected), re-picks the fillet's edge, and checks that selecting a face selects its feature in the tree.

## Screenshots

`tests/playground/out/screenshots/example-N.png` (git-ignored), 1500×900, one per example, numbered as in `?example=N`,
taken two frames after the result arrived. Each shows the editor on the left with the stats line at the bottom, and
the part on the right, framed to fit, one colour per face, sharp edges dark and tangent edges faint. Things to look
for: a part missing or cut off (framing), faces that share a colour where they should not (face naming), holes in the
mesh, stray triangles, and red diagnostics under the stats line.

## Writing browser tests

`browser.mjs` is the layer for further tests (drag handles, undo, the agent panel). `launch()` gives a browser,
`newPage()` a tab with:

- `open(url)`, `evaluate(expression)` (awaits promises, returns plain values), `waitFor(expression, { timeout })`;
- `click([x, y], { button, modifiers })`, `drag(from, to, { steps })`, `mouse(type, point, options)` for anything
  else, `center(selector)`;
- `frames(n)` before a screenshot, `screenshot(file)`, and `errors` (exceptions and console errors since `open`).

Coordinates are CSS pixels in the viewport. The page exposes `window.isoform`: `ready` (a result is shown),
`document()`, `hash()`, `selection()` (face names), `faces()` (each face's names), `select(names)` and `edit(text)`
(resolves once that text has regenerated); for diagnostics `diagnostics()` (as listed), `focusDiagnostic(index)`,
`marked()` (the faces lit for the one in focus, with their colours), `cell()` (the cell the result was made at),
`target()` (where the camera looks) and `resolve(cell)`; for the tree, `feature()` (the selected item's key). Later tasks add to it, for example handles:

```js
const page = await browser.newPage();
await page.open(`${site.url}?example=0`);
await page.waitFor('window.isoform?.ready');
const [h] = await page.evaluate('window.isoform.handles()');   // assumed to include screen positions
await page.drag(h.screen, [h.screen[0] + 40, h.screen[1]]);
await page.waitFor(`window.isoform.document().includes('"end": 10')`);
```
