// Wires the page: the kernel workers, the store, the viewer, picking and the panels.
import { adaptResult, createClient } from './kernel-client.js';
import { createStore } from './store.js';
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

const $ = id => document.getElementById(id);
const store = createStore();

// Anything that goes wrong shows in the status line instead of leaving the page stuck.
const report = e => store.set({ failure: String(e?.message ?? e) });
addEventListener('error', e => report(e.error ?? e.message));
addEventListener('unhandledrejection', e => report(e.reason));

// A pool of workers, each with its own kernel, meshes the regions of the part in parallel (up to 8; ?workers=N sets
// the number). The first worker gathers the others' regions and regenerates; only it answers.
const asked = +new URLSearchParams(location.search).get('workers');
const workers = Array.from({ length: asked > 0 ? asked : Math.min(navigator.hardwareConcurrency || 1, 8) },
  () => new Worker(new URL('./kernel-worker.js', import.meta.url), { type: 'module' }));
workers.forEach((worker, i) => { worker.onerror = e => report(e.message ?? `kernel worker ${i} failed to start`); });
const client = createClient(workers);

const viewer = createViewer($('view'));
let fit = true; // frame the camera on the next result shown: the first, and after picking an example
store.subscribe((s, before) => {
  if (s.result === before.result) return;
  viewer.show(s.result, fit);
  fit = false;
});
createPicking(viewer, store, $('tooltip'));
createSelection(store, $('selection'));
// The cell size the page opens with; the diagnostics offer it as the way back after resolving at a finer one.
const defaultCell = +[...$('cell').options].find(o => o.defaultSelected).value;
const diagnostics = createDiagnostics(store, {
  stats: $('stats'), list: $('diagnostics'), workers: workers.length, viewer, view: $('view'), badge: $('verdict'), defaultCell,
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
  const box = $('cell');
  if (![...box.options].some(o => +o.value === cell))
    box.add(new Option(`${cell} mm`, String(cell)), [...box.options].find(o => +o.value < cell) ?? null);
  box.value = [...box.options].find(o => +o.value === cell).value;
  return regenerate();
}
$('chord').addEventListener('change', () => regenerate());

// Commits, history and the agent view (lib/commit.js): applyEdits is how panels change the document.
const commits = createCommits(store, { call: client.call, regenerate, flash: createFlash($('editor')) });
createAgent(store, $('agent'), commits);
createHistory(store, $('history'), commits);

// For the browser tests and the console: plain data out, a promise per regeneration.
window.isoform = {
  get ready() { return !!store.state.result; },
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
  target: () => viewer.target(),
  resolve: cell => useCell(cell),
});

// Reference pins: pins() as the store keeps them; pin(target) pins a face name, an edge [a, b], or (no target) the
// selection, and resolves to the pin.
Object.assign(window.isoform, {
  pins: () => store.state.pins ?? [],
  pin: target => references.pin(typeof target === 'string' ? { face: target } : Array.isArray(target) ? { edge: target } : undefined),
});

// Handles (J1): the selected feature's handles. A released drag commits its edits as one history entry (J3's
// applyEdits); J2 adds the preview pipeline.
const handles = createHandles(viewer, store, {
  fetch: () => client.call('handles', { slot: 'main' }).then(r => r.result.handles),
  commit: edits => commits.applyEdits(edits, undefined, 'handle'),
});
window.isoform.handles = handles.list;

await client.start();
editor.load((await client.call('examples')).result);
await regenerate({ fit: true });
