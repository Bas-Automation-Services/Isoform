# Playground tests

Checks the published WebAssembly playground: the kernel in Node, and the page in headless Edge or Chrome. Needs Node 22
or later (built-ins only, no `npm install`) and, for the browser run, Edge or Chrome.

```sh
node tests/playground/run.mjs --publish            # publish to out/playground, then test it (publish takes ~2 min)
node tests/playground/run.mjs                      # test what is already in out/playground/wwwroot
node tests/playground/run.mjs path/to/wwwroot      # or any other published wwwroot (or the folder above it)
```

The parts on their own:

```sh
node --test tests/playground/kernel.test.mjs       # kernel only; ISOFORM_PLAYGROUND=<wwwroot> to point elsewhere
node tests/playground/e2e.mjs [wwwroot]            # browser only
node tests/playground/serve.mjs [wwwroot] [port]   # serve the page to look at it yourself (default port 8137)
```

Environment: `ISOFORM_BROWSER` is the browser executable when Edge or Chrome is not in its standard location;
`ISOFORM_TIMEOUT` is how long, in ms, the browser run waits for one example (default 120000). The page loads three.js
from a CDN, so the browser run needs a network connection.

## What is checked

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
loading the runtime).

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

Coordinates are CSS pixels in the viewport. For example, once the page exposes `window.isoform`:

```js
const page = await browser.newPage();
await page.open(`${site.url}?example=0`);
await page.waitFor('window.isoform?.ready');
const [h] = await page.evaluate('window.isoform.handles()');   // assumed to include screen positions
await page.drag(h.screen, [h.screen[0] + 40, h.screen[1]]);
await page.waitFor(`window.isoform.document().includes('"end": 10')`);
```
