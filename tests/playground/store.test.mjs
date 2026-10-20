// The page's state (wwwroot/js/store.js): selection by face name across regenerations, history: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createStore, facesNamed, pick } from '../../src/Isoform.Playground/wwwroot/js/store.js';
import { statusLine } from '../../src/Isoform.Playground/wwwroot/js/panels/diagnostics.js';

/** A result as adaptResult makes it, with faces in the given order. */
const result = (...names) => ({
  error: null, ms: 812.4, text: null, hash: null,
  faces: names.map(n => ({ names: Array.isArray(n) ? n : [n], body: 'plate' })),
  bodies: [{ name: 'plate', faces: names.map((_, i) => i) }],
  diagnostics: [], thinSpots: [],
  counts: { meshed: 3, reused: 5, triangles: 19598, edges: 12, vertices: 8 },
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
  const r = result('top', ['side', 'top'], 'bottom', 'top');
  assert.deepEqual(facesNamed(r, ['top']), [0, 3]);
  assert.deepEqual(facesNamed(r, ['bottom', 'side']), [1, 2]);
  assert.deepEqual(facesNamed(null, ['top']), []);
});

test('the selection follows the names when the faces are renumbered', () => {
  const store = createStore();
  store.committed('v1', result('plate/x+', 'plate/y+', 'hole/wall'));
  store.set({ selection: ['hole/wall', 'plate/x+'] });
  store.committed('v2', result('hole/wall', 'fillet/blend', 'plate/x+', 'plate/y+'));
  assert.deepEqual(store.state.selection, ['hole/wall', 'plate/x+']);
  assert.deepEqual(facesNamed(store.state.result, store.state.selection), [0, 2]);
});

test('a selected face that is gone drops out of the selection', () => {
  const store = createStore();
  store.committed('v1', result('a', 'b'));
  store.set({ selection: ['a', 'b'] });
  store.committed('v2', result('b', 'c'));
  assert.deepEqual(store.state.selection, ['b']);
});

test('a document that does not read keeps the last result and selection', () => {
  const store = createStore();
  const good = result('a');
  store.committed('v1', good);
  store.set({ selection: ['a'], regenerating: true });
  store.committed('{', { error: 'unexpected end', ms: 1 });
  assert.equal(store.state.result, good);
  assert.deepEqual(store.state.selection, ['a']);
  assert.equal(store.state.problem, 'unexpected end');
  assert.equal(store.state.regenerating, false);
  store.committed('v1', result('a'));
  assert.equal(store.state.problem, null);
});

test('history: one entry per changed document', () => {
  const store = createStore();
  store.committed('v1', result('a'));
  store.committed('v1', result('a'));
  store.committed('v2', result('a'), 'drag');
  assert.deepEqual(store.state.history, [{ text: 'v1', hash: null, origin: 'editor' }, { text: 'v2', hash: null, origin: 'drag' }]);
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
  assert.equal(statusLine(s.state, 1), 'Loading the kernel…');
  s.committed('v1', result('a', 'b'));
  const line = statusLine(s.state, 4);
  assert.match(line, /^812 ms · 4 workers, 3 of 8 regions meshed · 19.598 triangles · 2 faces, 12 edges, 8 vertices · 1 body$/);
  assert.match(line, /\b[1-9]\d* faces/);
  assert.equal(statusLine({ ...s.state, regenerating: true }, 4), 'Regenerating…');
  assert.equal(statusLine({ ...s.state, problem: 'x' }, 4), 'The document does not parse.');
  assert.equal(statusLine({ ...s.state, failure: 'worker died' }, 4), 'Error: worker died');
});
