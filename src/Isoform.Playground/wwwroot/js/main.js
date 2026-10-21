// Wires the page: the kernel workers, the store, the viewer, picking and the panels.
import { adaptResult, connectKernel, createClient } from './kernel-client.js';
import { createStore, shown } from './store.js';
import { createViewer } from './viewer.js';
import { createPicking } from './picking.js';
import { createHandles } from './handles.js';
import { createEditor } from './panels/editor.js';
import { createDiagnostics } from './panels/diagnostics.js';
import { createSelection } from './panels/selection.js';
import { createReferences } from './panels/references.js';
import { createCommits } from './lib/commit.js';
import { createAgent } from './panels/agent.js';
import { createHistory } from './panels/history.js';
import { createFlash } from './panels/flash.js';
import { createTree } from './panels/tree.js';
import { createInspector } from './panels/inspector.js';
import { createPreview } from './lib/preview.js';
import { createGhosts } from './ghosts.js';
import { createTour } from './panels/tour.js';
import { createShare } from './panels/share.js';
import { createSketchMode } from './sketch/mode.js';

const $ = id => document.getElementById(id);
const store = createStore();

// Anything that goes wrong shows in the status line instead of leaving the page stuck.
const report = e => store.set({ failure: String(e?.message ?? e) });
addEventListener('error', e => report(e.error ?? e.message));
addEventListener('unhandledrejection', e => report(e.reason));

// The native kernel (src/Isoform.Server) when there is one: the page came from it (it says so in the navigation's
// Server-Timing), or ?kernel=ws://localhost:5250/kernel points at one. Two connections, each a session of its own,
// like the workers below: one for what is committed, one for the drag previews. Without it, or with ?kernel=wasm, the
// kernel runs in the page's workers, so a shared link works on any static host.
const params = new URLSearchParams(location.search);
const served = performance.getEntriesByType('navigation')[0]?.serverTiming?.some(t => t.name === 'isoform-kernel');
const kernelUrl = params.get('kernel') ?? (served ? `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/kernel` : 'wasm');
const sockets = kernelUrl === 'wasm' ? [] : await Promise.all([connectKernel(kernelUrl), connectKernel(kernelUrl)]);
const native = sockets.length > 0 && sockets.every(Boolean);
if (!native) sockets.forEach(s => s?.terminate());
if (native) document.querySelector('#panel header .muted').textContent = 'kernel playground · runs natively on this machine';

// Else a pool of workers, each with its own kernel, meshes the regions of the part in parallel (up to 8; ?workers=N
// sets the number). The first worker gathers the others' regions and regenerates; only it answers.
const asked = +params.get('workers');
const workers = native ? [sockets[0]] : Array.from({ length: asked > 0 ? asked : Math.min(navigator.hardwareConcurrency || 1, 8) },
  () => new Worker(new URL('./kernel-worker.js', import.meta.url), { type: 'module' }));
workers.forEach((worker, i) => { worker.onerror = e => report(e.message ?? `kernel worker ${i} failed to start`); });
const client = createClient(workers);
const kernel = native ? 'native' : `wasm, ${workers.length} worker${workers.length > 1 ? 's' : ''}`;

const viewer = createViewer($('view'));
let fit = true; // frame the camera on the next result shown: the first, and after picking an example
// What is on screen: a drag's preview while there is one, else the committed result (store.shown).
store.subscribe((s, before) => {
  if (shown(s) === shown(before) || !shown(s)) return;
  viewer.show(shown(s), fit);
  fit = false;
});
createPicking(viewer, store, $('tooltip'));
createSelection(store, $('selection'));
// The cell size the page opens with; the diagnostics offer it as the way back after resolving at a finer one.
const defaultCell = +[...$('cell').options].find(o => o.defaultSelected).value;
const diagnostics = createDiagnostics(store, {
  stats: $('stats'), list: $('diagnostics'), kernel, viewer, view: $('view'), badge: $('verdict'), defaultCell,
  resolve: cell => useCell(cell),
});
const references = createReferences(store, { box: $('references'), viewer, resolve: refs => client.call('resolve', { refs }).then(r => r.result) });

/**
 * Regenerates the document in the store; latest wins, so edits made meanwhile fold into one next run. The rest is
 * the commit's intent for the history (store.committed). Resolves to the result, or null when overtaken.
 */
async function regenerate({ fit: refit = false, ...intent } = {}) {
  if (refit) fit = true;
  const text = store.state.text;
  store.set({ regenerating: true });
  const reply = await client.latest('regenerate', 'regenerate', { document: text, cell: +$('cell').value, chord: +$('chord').value });
  if (!reply) return null;
  const result = adaptResult(reply.result, reply.ms);
  store.committed(result, intent);
  return result;
}

const editor = createEditor(store, { editor: $('editor'), parse: $('parse'), examplesBox: $('examples'), regenerate });
$('cell').addEventListener('change', () => regenerate());

/** Regenerates at this cell size, keeping the view; the cell select shows it, with an option of its own if needed. */
function useCell(cell) {
  pickCell(cell);
  return regenerate();
}
/** Sets the cell select to this size, for the next regeneration. */
function pickCell(cell) {
  const box = $('cell');
  if (![...box.options].some(o => +o.value === cell))
    box.add(new Option(`${cell} mm`, String(cell)), [...box.options].find(o => +o.value < cell) ?? null);
  box.value = [...box.options].find(o => +o.value === cell).value;
}
$('chord').addEventListener('change', () => regenerate());

// Commits, history and the agent view (lib/commit.js): applyEdits is how panels change the document.
const commits = createCommits(store, { call: client.call, regenerate, flash: createFlash($('editor')) });
createAgent(store, $('agent'), commits);
createHistory(store, $('history'), commits);

// Feature tree and inspector (J4): every inspector change is an edit list, committed as one history entry.
createTree(store, viewer, $('tree'), { open: it => it.type === 'sketch' && sketching.edit(it.id) });
createInspector(store, $('inspector'), (edits, label) => commits.applyEdits(edits, label, 'inspector'));

// For the browser tests and the console: plain data out, a promise per regeneration.
window.isoform = {
  get ready() { return !!store.state.result; },
  kernel: native ? 'native' : 'wasm',
  document: () => store.state.text,
  hash: () => store.state.hash,
  selection: () => store.state.selection,
  faces: () => store.state.result?.faces ?? [],
  select: names => store.set({ selection: names }),
  edit(text) {
    store.set({ text });
    return regenerate();
  },
};

// Commits for the tests and the console: each resolves, once regenerated, to {hash} or {error, failedEdit?}, or
// null (nothing to undo, or overtaken).
const outcome = r => r && (r.error ? { error: r.error, failedEdit: r.failedEdit } : { hash: r.hash });
Object.assign(window.isoform, {
  applyEdits: (edits, label, origin) => commits.applyEdits(edits, label, origin).then(outcome),
  undo: () => commits.undo().then(outcome),
  redo: () => commits.redo().then(outcome),
  history: () => ({ at: store.state.historyAt, entries: store.state.history.map(({ hash, edits, label, origin }) => ({ hash, edits, label, origin })) }),
});

// Diagnostics (J5) for the browser tests: the list as shown, the faces lit for the one in focus, the cell in use.
Object.assign(window.isoform, {
  diagnostics: () => diagnostics.items(),
  focusDiagnostic: (index, feature) => diagnostics.focus(index, feature),
  marked: () => viewer.marks().map(([f, color]) => ({ face: store.state.result.faces[f][0], color })),
  cell: () => store.state.result?.cell ?? null,
  /** The boxes the kernel meshed with a finer cell: [{min, max, cell}]. */
  refined: () => store.state.result?.refined ?? [],
  target: () => viewer.target(),
  resolve: cell => useCell(cell),
});
Object.assign(window.isoform, { feature: () => store.state.feature }); // J4: the selected tree item's key

// Reference pins: pins() as the store keeps them; pin(target) pins a face name, an edge [a, b], or (no target) the
// selection, and resolves to the pin.
Object.assign(window.isoform, {
  pins: () => store.state.pins ?? [],
  pin: target => references.pin(typeof target === 'string' ? { face: target } : Array.isArray(target) ? { edge: target } : undefined),
});

// --- Drag preview (J2) ----------------------------------------------------------------------------------------
// While a handle drags, a ghost of its faces moves at once (ghosts.js) and a preview worker regenerates the edited
// document in its "preview" slot, latest wins (lib/preview.js). The preview has a worker of its own rather than a
// pool worker: the region cache keeps only what the last regeneration used, so previews in the pool would throw away
// the committed document's regions and the release would mesh everything again. One more kernel costs one more wasm
// heap (window.isoform.memory(): 70-80 MB over the demo, as much as the pool's primary), against up to 8 in the pool.
// It starts with the page, so it is loaded by the first drag. With the native kernel it is the second connection.
const previewWorker = native ? sockets[1] : new Worker(new URL('./kernel-worker.js', import.meta.url), { type: 'module' });
previewWorker.onerror = e => report(e.message ?? 'the preview worker failed to start');
const previewClient = createClient([previewWorker]);
const previews = createPreview(store, {
  call: args => previewClient.call('regenerate', args),
  settings: () => ({ cell: store.state.result?.cell ?? +$('cell').value, chord: +$('chord').value }),
});
const ghosts = createGhosts(viewer, store);
// The release commits through applyEdits: one history entry per drag. The preview and ghost stay on screen until the
// commit's result is in, so the part does not jump back meanwhile.
const drags = {
  start: handle => { $('tooltip').hidden = true; ghosts.begin(handle); previews.begin(handle); },
  preview: (edits, handle, { t }) => { ghosts.move(t); previews.move(edits, t); },
  end: (handle, moved) => {
    previews.end();
    if (!moved) { previews.drop(); ghosts.clear(); }
  },
  commit: (edits, handle) => commits.applyEdits(edits, undefined, 'handle').finally(() => { previews.drop(); ghosts.clear(); }),
};
Object.assign(window.isoform, {
  /** The preview on screen: {cell, ms, t, handle, hash, diagnostics}, or null. */
  preview: () => (p => p && { cell: p.cell, ms: p.ms, t: p.t, handle: p.handle, hash: p.hash, diagnostics: p.diagnostics.map(d => d.code) })(store.state.preview),
  previewTimings: () => previews.timings,
  ghost: () => ghosts.state(),
  /** Bytes of wasm memory (its high-water mark) of the pool's primary kernel and of the preview's. */
  memory: async () => ({ primary: (await client.call('memory')).result, preview: (await previewClient.call('memory')).result }),
});
// --- end drag preview -------------------------------------------------------------------------------------------

// Handles (J1): the selected feature's handles; a drag previews (J2, above) and its release commits its edits as one
// history entry (J3's applyEdits).
const handles = createHandles(viewer, store, {
  fetch: () => client.call('handles', { slot: 'main' }).then(r => r.result.handles),
  ...drags,
});
window.isoform.handles = handles.list;
window.isoform.dragging = handles.dragging;

// --- Sketch mode (B1-B5, docs/sketch-plan.md) ---------------------------------------------------------------------
// A flat face selected, Sketch (or S) draws on it; a sketch in the tree double-clicked is edited. Its finish is one
// commit of origin 'sketch'.
const sketching = createSketchMode(store, {
  viewer, view: $('view'), column: $('model'), call: client.call, latest: client.latest,
  applyEdits: (edits, label, origin) => commits.applyEdits(edits, label, origin),
});
window.isoform.sketch = sketching.api;
// --- end sketch mode --------------------------------------------------------------------------------------------

// --- Guided tour and share links (T1) ---------------------------------------------------------------------------
// The tour commits its edits like an agent (origin 'tour'); a #doc= link opens its document instead of an example.
const tour = createTour(store, {
  box: $('view'), agentBox: $('agent'), examplesBox: $('examples'), viewer, commits, diagnostics, references,
  call: client.call, regenerate, pickCell, defaultCell,
});
$('tour').onclick = () => tour.start();
const share = createShare(store, { button: $('share'), status: $('share-status'), examplesBox: $('examples'), regenerate });
Object.assign(window.isoform, { tour, shareLink: () => share.link(), shareStatus: () => share.status() });
// --- end tour and share -----------------------------------------------------------------------------------------

await client.start();
editor.load((await client.call('examples')).result);
await share.open();
await regenerate({ fit: true });
