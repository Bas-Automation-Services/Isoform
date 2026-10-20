// Wires the page: the kernel workers, the store, the viewer, picking and the panels.
import { adaptResult, createClient } from './kernel-client.js';
import { createStore } from './store.js';
import { createViewer } from './viewer.js';
import { createPicking } from './picking.js';
import { createEditor } from './panels/editor.js';
import { createDiagnostics } from './panels/diagnostics.js';
import { createSelection } from './panels/selection.js';
import { pretty } from './panels/editor.js';
import { createTree } from './panels/tree.js';
import { createInspector } from './panels/inspector.js';

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
createDiagnostics(store, { stats: $('stats'), list: $('diagnostics'), workers: workers.length });

/** Regenerates the document in the store; latest wins, so edits made meanwhile fold into one next run. */
async function regenerate({ fit: refit = false } = {}) {
  if (refit) fit = true;
  const text = store.state.text;
  store.set({ regenerating: true });
  const reply = await client.latest('regenerate', 'regenerate', { document: text, cell: +$('cell').value, chord: +$('chord').value });
  if (reply) store.committed(adaptResult(reply.result, reply.ms));
}

// --- Feature tree and inspector (J4) ---
// Every inspector change is an edit list and goes through commit, the one place to swap for J3's
// applyEdits(edits, label, origin): the kernel applies the edits, regenerates, and returns the edited canonical text.
async function commit(edits, label) {
  store.set({ regenerating: true });
  const reply = await client.latest('regenerate', 'regenerate', { document: store.state.text, edits, cell: +$('cell').value, chord: +$('chord').value });
  if (!reply) return;
  const result = adaptResult(reply.result, reply.ms);
  if (!result.error) store.set({ text: pretty(JSON.parse(result.text)) });
  store.committed(result, 'inspector');
}
createTree(store, viewer, $('tree'));
createInspector(store, $('inspector'), commit);
// --- end J4 ---

const editor = createEditor(store, { editor: $('editor'), parse: $('parse'), examplesBox: $('examples'), regenerate });
$('cell').addEventListener('change', () => regenerate());
$('chord').addEventListener('change', () => regenerate());

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

Object.assign(window.isoform, { feature: () => store.state.feature }); // J4: the selected tree item's key

await client.start();
editor.load((await client.call('examples')).result);
await regenerate({ fit: true });
