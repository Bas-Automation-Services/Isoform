// The drag preview (wwwroot/js/lib/preview.js) over the real store and a fake preview worker. node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { budget, createPreview, startLevel } from '../../src/Isoform.Playground/wwwroot/js/lib/preview.js';
import { createStore, shown } from '../../src/Isoform.Playground/wwwroot/js/store.js';

const handle = { id: 'plate:x+', kind: 'linear' };

/** A result v2 as the worker returns it, for the edits it was asked for. */
const reply = ({ edits, cell }, ms) => ({
  result: {
    info: { version: 2, hash: `h${JSON.stringify(edits)}`, text: '{}', cell, diagnostics: [], faces: [['plate/x+']], bodies: [{ name: 'plate', faces: [0] }], meshed: 1, reused: 9 },
    positions: new Float32Array(9), faces: new Int32Array(1), sharp: new Float32Array(0), smooth: new Float32Array(0), sharpEdges: new Int32Array(0), smoothEdges: new Int32Array(0),
  },
  ms,
});

/** A fake worker: requests wait until answered, ms(args) says how long each took. */
function fakeWorker(ms = () => 40) {
  const requests = [];
  return {
    requests,
    call: args => new Promise(resolve => requests.push({ args, answer: () => resolve(reply(args, ms(args))) })),
    async answer() { requests.shift().answer(); await new Promise(r => setTimeout(r)); },
  };
}

function setup(ms, committedMs = 50) {
  const store = createStore({ result: { text: 'committed', cell: 0.5, timing: { regenerate: committedMs } } });
  const worker = fakeWorker(ms);
  const preview = createPreview(store, { call: worker.call, settings: () => ({ cell: 0.5, chord: 0.05 }) });
  return { store, worker, preview };
}

const edits = width => [{ set: 'plate.size.0', value: width }];

test('a drag warms the worker, then shows each reply as a preview of the committed document in the preview slot', async () => {
  const { store, worker, preview } = setup();
  preview.begin(handle);
  assert.deepEqual(worker.requests[0].args, { document: 'committed', edits: [], cell: 0.5, chord: 0.05, slot: 'preview', snap: true });
  await worker.answer();
  assert.equal(store.state.preview, null, 'the warm-up is not shown');

  preview.move(edits(101), 1);
  await worker.answer();
  const p = store.state.preview;
  assert.equal(p.preview, true);
  assert.equal(p.t, 1);
  assert.equal(p.handle, 'plate:x+');
  assert.equal(p.hash, 'h[{"set":"plate.size.0","value":101}]');
  assert.equal(shown(store.state), p, 'the preview is on screen');
  assert.equal(store.state.result.text, 'committed', 'the committed result stays');
});

test('latest wins: moves made while one is under way fold into the newest, and every reply that comes back shows', async () => {
  const { store, worker, preview } = setup();
  preview.begin(handle);
  preview.move(edits(101), 1);
  preview.move(edits(102), 2);
  preview.move(edits(103), 3);
  assert.equal(worker.requests.length, 1, 'one in flight');
  await worker.answer();
  assert.equal(worker.requests.length, 1);
  assert.deepEqual(worker.requests[0].args.edits, edits(103));
  preview.move(edits(104), 4);
  await worker.answer();
  assert.equal(store.state.preview.t, 3, 'shown although a newer move waits');
  await worker.answer();
  assert.equal(store.state.preview.t, 4);
});

test('end: a reply still under way is not shown, the last preview stays until drop', async () => {
  const { store, worker, preview } = setup();
  preview.begin(handle);
  preview.move(edits(101), 1);
  await worker.answer();
  await worker.answer();
  preview.move(edits(102), 2);
  preview.end();
  await worker.answer();
  assert.equal(store.state.preview.t, 1);
  assert.equal(worker.requests.length, 0);
  preview.drop();
  assert.equal(store.state.preview, null);
  assert.equal(shown(store.state), store.state.result);
});

test('full resolution while a preview keeps within budget; coarser once a warm reply does not', async () => {
  const { worker, preview } = setup(({ cell }) => (cell === 0.5 ? budget + 80 : 20));
  preview.begin(handle);
  await worker.answer(); // the cold warm-up does not count
  preview.move(edits(101), 1);
  assert.equal(worker.requests[0].args.cell, 0.5);
  await worker.answer();
  assert.equal(preview.level(), 1);
  preview.move(edits(102), 2);
  assert.deepEqual([worker.requests[0].args.cell, worker.requests[0].args.chord], [1, 0.1], 'twice as coarse, chord too');
  await worker.answer();
  preview.move(edits(103), 3);
  await worker.answer();
  assert.equal(preview.level(), 1, 'never finer during a drag');
  preview.end();
  // The next drag of this handle starts a step finer: it ran well within budget at 1 mm.
  preview.begin(handle);
  assert.equal(worker.requests[0].args.cell, 0.5);
});

test('a slow committed regeneration starts coarse; startLevel steps finer only after a fast drag', () => {
  const { worker, preview } = setup(undefined, budget + 1);
  preview.begin(handle);
  assert.equal(worker.requests[0].args.cell, 1);
  assert.equal(startLevel(null, 1), 1);
  assert.equal(startLevel({ level: 2, slowest: budget / 2 }, 0), 2);
  assert.equal(startLevel({ level: 2, slowest: budget / 6 }, 0), 1);
  assert.equal(startLevel({ level: 0, slowest: 1 }, 1), 0);
});
