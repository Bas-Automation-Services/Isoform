// The page's state (wwwroot/js/store.js): selection by face name across regenerations, history: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createStore, facesNamed, pick } from '../../src/Isoform.Playground/wwwroot/js/store.js';
import { statusLine } from '../../src/Isoform.Playground/wwwroot/js/panels/diagnostics.js';

/** A result as adaptResult makes it (result v2), for document text, with faces in the given order. */
const result = (text, ...names) => ({
  version: 2, slot: 'main', format: 'isoform/0', text, hash: `hash of ${text}`, error: null, ms: 812.4,
  faces: names.map(n => Array.isArray(n) ? n : [n]),
  bodies: [{ name: 'plate', faces: names.map((_, i) => i) }],
  diagnostics: [], thinSpots: [], lineFaces: [], instances: [],
  meshed: 3, reused: 5, triangles: 19598, edges: 12, vertices: 8,
});

test('pick: a face, a second with shift for an edge, nothing clears', () => {
  assert.deepEqual(pick([], 'a'), ['a']);
  assert.deepEqual(pick(['a'], 'b'), ['b']);
  assert.deepEqual(pick(['a'], 'b', true), ['a', 'b']);
  assert.deepEqual(pick(['a'], 'a', true), ['a']);
  assert.deepEqual(pick(['a', 'b'], 'c', true), ['c']);
  assert.deepEqual(pick(['a', 'b'], null), []);
});

test('facesNamed: by first name, every face of a split name', () => {
  const r = result('v1', 'top', ['side', 'top'], 'bottom', 'top');
  assert.deepEqual(facesNamed(r, ['top']), [0, 3]);
  assert.deepEqual(facesNamed(r, ['bottom', 'side']), [1, 2]);
  assert.deepEqual(facesNamed(null, ['top']), []);
});

test('the selection follows the names when the faces are renumbered', () => {
  const store = createStore();
  store.committed(result('v1', 'plate/x+', 'plate/y+', 'hole/wall'));
  store.set({ selection: ['hole/wall', 'plate/x+'] });
  store.committed(result('v2', 'hole/wall', 'fillet/blend', 'plate/x+', 'plate/y+'));
  assert.deepEqual(store.state.selection, ['hole/wall', 'plate/x+']);
  assert.deepEqual(facesNamed(store.state.result, store.state.selection), [0, 2]);
});

test('a selected face that is gone drops out of the selection', () => {
  const store = createStore();
  store.committed(result('v1', 'a', 'b'));
  store.set({ selection: ['a', 'b'] });
  store.committed(result('v2', 'b', 'c'));
  assert.deepEqual(store.state.selection, ['b']);
});

test('a failed regeneration keeps the last result and selection', () => {
  const store = createStore();
  const good = result('v1', 'a');
  store.committed(good);
  store.set({ selection: ['a'], regenerating: true });
  store.committed({ version: 2, slot: 'main', error: 'unexpected end', ms: 1 });
  assert.equal(store.state.result, good);
  assert.equal(store.state.hash, 'hash of v1');
  assert.deepEqual(store.state.selection, ['a']);
  assert.equal(store.state.problem, 'unexpected end');
  assert.equal(store.state.regenerating, false);
  store.committed(result('v1', 'a'));
  assert.equal(store.state.problem, null);
});

test('history: one entry per changed document, with its canonical text, hash and what made it', () => {
  const store = createStore();
  store.committed(result('v1', 'a'));
  store.committed(result('v1', 'a'));
  store.committed(result('v2', 'a'), { origin: 'handle', edits: [{ set: 'plate.size.0', value: 120 }], label: 'drag plate/x+' });
  store.committed(result('v3', 'a'), 'drag');
  assert.deepEqual(store.state.history, [
    { text: 'v1', hash: 'hash of v1', edits: null, label: null, origin: 'editor' },
    { text: 'v2', hash: 'hash of v2', edits: [{ set: 'plate.size.0', value: 120 }], label: 'drag plate/x+', origin: 'handle' },
    { text: 'v3', hash: 'hash of v3', edits: null, label: null, origin: 'drag' },
  ]);
  assert.equal(store.state.historyAt, 2);
  store.committed(result('v1', 'a'), { restore: 0 });
  assert.equal(store.state.history.length, 3);
  assert.equal(store.state.historyAt, 0);
});

test('subscribers get the new and the previous state', () => {
  const store = createStore();
  const seen = [];
  const stop = store.subscribe((s, before) => seen.push([before.text, s.text]));
  store.set({ text: 'x' });
  stop();
  store.set({ text: 'y' });
  assert.deepEqual(seen, [['', 'x']]);
});

test('status line: the form the browser run reads', () => {
  const s = createStore();
  assert.equal(statusLine(s.state, 'native'), 'Loading the kernel…');
  s.committed(result('v1', 'a', 'b'));
  const line = statusLine(s.state, 'wasm, 4 workers');
  assert.match(line, /^812 ms · wasm, 4 workers, 3 of 8 regions meshed · 19.598 triangles · 2 faces, 12 edges, 8 vertices · 1 body$/);
  assert.match(line, /\b[1-9]\d* faces/);
  assert.match(statusLine(s.state, 'native'), /^812 ms · native, 3 of 8 regions meshed · /);
  assert.equal(statusLine({ ...s.state, regenerating: true }, 'native'), 'Regenerating…');
  assert.equal(statusLine({ ...s.state, problem: 'x' }, 'native'), 'The document does not parse.');
  assert.equal(statusLine({ ...s.state, failure: 'worker died' }, 'native'), 'Error: worker died');
});
