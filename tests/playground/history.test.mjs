// Document history and commits (wwwroot/js/lib/history.js, lib/commit.js) over the real store and a fake kernel:
// node --test
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { createStore } from '../../src/Isoform.Playground/wwwroot/js/store.js';
import { createCommits } from '../../src/Isoform.Playground/wwwroot/js/lib/commit.js';
import { describe, recorded } from '../../src/Isoform.Playground/wwwroot/js/lib/history.js';

// The fake kernel: canonical text is JSON with two-space indent, edits are {set, value} on dotted paths.
const canonical = value => JSON.stringify(value, null, 2);
const sha = text => createHash('sha256').update(text).digest('hex');
const tick = () => new Promise(done => setTimeout(done));

function apply(document, edits) {
  const doc = JSON.parse(document);
  for (const [i, { set, value }] of edits.entries()) {
    const keys = set.split('.'), last = keys.pop();
    const owner = keys.reduce((o, k) => o?.[k], doc);
    if (owner?.[last] === undefined) return { problem: `${set} does not exist`, failedEdit: i };
    owner[last] = value;
  }
  const text = canonical(doc);
  return { text, hash: sha(text) };
}

// Every leaf that differs as a set: enough for documents whose shape does not change.
function diff(before, after) {
  const edits = [];
  const walk = (a, b, path) => {
    if (a && typeof a === 'object') for (const k of Object.keys(a)) walk(a[k], b[k], [...path, k]);
    else if (a !== b) edits.push({ set: path.join('.'), value: b });
  };
  walk(JSON.parse(before), JSON.parse(after), []);
  return { edits };
}

function setup(document = { plate: { size: [100, 50, 10] }, hole: { diameter: 8 } }) {
  const store = createStore({ text: canonical(document) });
  const calls = [], flashes = [];
  const call = async (op, args) => {
    calls.push(op);
    await tick();
    return { result: op === 'apply' ? apply(args.document, args.edits) : diff(args.before, args.after) };
  };
  // Like main.js: regenerates store.text as it is when asked, commits with the intent.
  const regenerate = async intent => {
    const text = store.state.text;
    await tick();
    let result;
    try { result = { text: canonical(JSON.parse(text)), faces: [], error: null }; } catch (e) { result = { error: e.message }; }
    if (!result.error) result.hash = sha(result.text);
    store.committed(result, intent);
    return result;
  };
  const commits = createCommits(store, { call, regenerate, flash: (before, after) => flashes.push([before, after]) });
  return { store, commits, calls, flashes, regenerate, hashes: () => store.state.history.map(e => e.hash) };
}

test('recorded: a new document is pushed, the same one is not, a restore only moves the index', () => {
  const a = { hash: 'a' }, b = { hash: 'b' }, c = { hash: 'c' };
  let h = recorded({ history: [], historyAt: -1 }, a);
  assert.deepEqual(h, { history: [a], historyAt: 0 });
  h = recorded(recorded(h, b), c);
  assert.deepEqual(h, { history: [a, b, c], historyAt: 2 });
  assert.equal(recorded(h, { hash: 'c' }).history, h.history, 'the shown document again: nothing changes');
  assert.deepEqual(recorded(h, { hash: 'a' }, 0), { history: [a, b, c], historyAt: 0 });
  // A commit after an undo drops what could have been redone.
  const d = { hash: 'd' };
  assert.deepEqual(recorded({ history: [a, b, c], historyAt: 0 }, d), { history: [a, d], historyAt: 1 });
  // A restore whose document came back different is an ordinary commit.
  assert.deepEqual(recorded({ history: [a, b], historyAt: 1 }, d, 0), { history: [a, b, d], historyAt: 2 });
});

test('describe: the first edit and how many more', () => {
  assert.equal(describe([{ set: 'plate.size.0', value: 120 }]), 'set plate.size.0 = 120');
  assert.equal(describe([{ set: 'a', value: 'x' }, { remove: 'b' }]), 'set a = "x" (+1)');
  assert.equal(describe([{ remove: 'slot' }]), 'remove slot');
  assert.equal(describe([]), 'no change');
});

test('applyEdits: one history entry with its edits, label and origin; the editor text changes and flashes', async () => {
  const { store, commits, flashes, regenerate } = setup();
  await regenerate();
  const before = store.state.text;
  const result = await commits.applyEdits({ set: 'plate.size.0', value: 120 }, 'drag plate/x+', 'handle');
  assert.equal(result.hash, store.state.hash);
  assert.equal(store.state.history.length, 2);
  assert.equal(store.state.historyAt, 1);
  const { edits, label, origin, text } = store.state.history[1];
  assert.deepEqual({ edits, label, origin }, { edits: [{ set: 'plate.size.0', value: 120 }], label: 'drag plate/x+', origin: 'handle' });
  assert.equal(JSON.parse(text).plate.size[0], 120);
  // The editor shows arrays of numbers on one line (pretty), the history keeps the canonical text.
  assert.match(store.state.text, /"size": \[120, 50, 10\]/);
  assert.deepEqual(flashes, [[before, store.state.text]]);
  // No label: a summary of the edits.
  await commits.applyEdits([{ set: 'hole.diameter', value: 6 }]);
  assert.equal(store.state.history[2].label, 'set hole.diameter = 6');
});

test('edits that do not apply commit nothing: the problem and the rejected list are shown', async () => {
  const { store, commits, flashes, regenerate, hashes } = setup();
  await regenerate();
  const [text, before] = [store.state.text, hashes()];
  const result = await commits.applyEdits([{ set: 'hole.diameter', value: 6 }, { set: 'slot.width', value: 3 }], 'drag', 'handle');
  assert.deepEqual(result, { error: 'slot.width does not exist', failedEdit: 1 });
  assert.deepEqual(hashes(), before);
  assert.equal(store.state.text, text);
  assert.equal(store.state.problem, 'slot.width does not exist');
  assert.deepEqual(store.state.rejected, { edits: [{ set: 'hole.diameter', value: 6 }, { set: 'slot.width', value: 3 }], label: 'drag', origin: 'handle', problem: 'slot.width does not exist', failedEdit: 1 });
  assert.deepEqual(flashes, []);
  // The next good commit clears both.
  await commits.applyEdits({ set: 'hole.diameter', value: 6 });
  assert.equal(store.state.problem, null);
  assert.equal(store.state.rejected, null);
});

test('undo back to the start gives the identical hash; redo walks forward again', async () => {
  const { store, commits, regenerate, hashes } = setup();
  await regenerate();
  const original = store.state.hash;
  for (const value of [110, 120, 130, 140]) await commits.applyEdits({ set: 'plate.size.0', value }, 'drag', 'handle');
  const all = hashes();
  assert.equal(all.length, 5);
  for (let i = 0; i < 4; i++) await commits.undo();
  assert.equal(store.state.hash, original);
  assert.equal(store.state.historyAt, 0);
  assert.deepEqual(hashes(), all, 'undo adds no entries');
  assert.equal(await commits.undo(), null, 'nothing left to undo');
  await commits.redo();
  await commits.redo();
  assert.equal(store.state.hash, all[2]);
  await commits.restore(4);
  assert.equal(store.state.hash, all[4]);
  assert.equal(await commits.redo(), null, 'nothing left to redo');
});

test('undo pressed again before the first came back keeps stepping', async () => {
  const { store, commits, regenerate } = setup();
  await regenerate();
  const original = store.state.hash;
  await commits.applyEdits({ set: 'plate.size.0', value: 110 });
  await commits.applyEdits({ set: 'plate.size.0', value: 120 });
  await Promise.all([commits.undo(), commits.undo()]);
  assert.equal(store.state.historyAt, 0);
  assert.equal(store.state.hash, original);
});

test('a commit after an undo drops the entries that redo would have brought back', async () => {
  const { store, commits, regenerate } = setup();
  await regenerate();
  await commits.applyEdits({ set: 'plate.size.0', value: 110 });
  await commits.applyEdits({ set: 'plate.size.0', value: 120 });
  await commits.undo();
  await commits.applyEdits({ set: 'hole.diameter', value: 5 });
  assert.deepEqual(store.state.history.map(e => e.label), ['opened', 'set plate.size.0 = 110', 'set hole.diameter = 5']);
  assert.equal(store.state.historyAt, 2);
});

test('typing: one entry per regeneration, its edits from the kernel diff; the first entry is the opened document', async () => {
  const { store, calls, flashes, regenerate } = setup();
  await regenerate();
  store.set({ text: store.state.text.replace('"diameter": 8', '"diameter": 9') });
  await regenerate();
  await tick(); await tick();
  const [opened, typed] = store.state.history;
  assert.deepEqual([opened.edits, opened.label, opened.origin], [[], 'opened', 'editor']);
  assert.deepEqual([typed.edits, typed.label, typed.origin], [[{ set: 'hole.diameter', value: 9 }], 'set hole.diameter = 9', 'editor']);
  assert.deepEqual(calls, ['diff']);
  assert.deepEqual(flashes, [], 'typing does not flash');
  // A text that does not read is no entry.
  store.set({ text: '{ "plate": ' });
  await regenerate();
  assert.equal(store.state.history.length, 2);
  assert.match(store.state.problem, /JSON/);
});
