// The worker protocol of the page (wwwroot/js/kernel-client.js) against fake workers: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptResult, createClient, unpack } from '../../src/Isoform.Playground/wwwroot/js/kernel-client.js';

/** Records the requests it is sent and answers them when the test says so. */
class FakeWorker {
  sent = [];
  postMessage(message, transfer) { this.sent.push({ ...message, transfer }); }
  reply(request, result) { this.onmessage({ data: { id: request.id, ok: true, result, ms: 7 } }); }
  fail(request, error) { this.onmessage({ data: { id: request.id, ok: false, error, ms: 7 } }); }
}

const settled = () => new Promise(done => setImmediate(done));

test('call sends {id, op, args} and resolves to {result, ms}', async () => {
  const w = new FakeWorker();
  const client = createClient([w]);
  const reply = client.call('examples', { a: 1 });
  assert.deepEqual(w.sent.map(({ op, args }) => ({ op, args })), [{ op: 'examples', args: { a: 1 } }]);
  w.reply(w.sent[0], { x: 2 });
  assert.deepEqual(await reply, { result: { x: 2 }, ms: 7 });
});

test('a failed request rejects with the worker error', async () => {
  const w = new FakeWorker();
  const reply = createClient([w]).call('examples');
  w.fail(w.sent[0], 'boom');
  await assert.rejects(reply, /boom/);
});

test('latest: one request in flight, the newest waiting one wins, stale replies are dropped', async () => {
  const w = new FakeWorker();
  const client = createClient([w]);
  const a = client.latest('regen', 'examples', 'a');
  const b = client.latest('regen', 'examples', 'b');
  const c = client.latest('regen', 'examples', 'c');
  assert.equal(w.sent.length, 1, 'b and c wait for a');
  assert.equal(await b, null, 'b was replaced by c before it was sent');

  w.reply(w.sent[0], 'A');
  assert.equal(await a, null, 'a came back while c was waiting');
  await settled();
  assert.deepEqual(w.sent.map(m => m.args), ['a', 'c']);

  w.reply(w.sent[1], 'C');
  assert.deepEqual(await c, { result: 'C', ms: 7 });
});

test('latest: a reply with nothing waiting is used, and a failure lets the next request go', async () => {
  const w = new FakeWorker();
  const client = createClient([w]);
  const a = client.latest('regen', 'examples', 'a');
  w.fail(w.sent[0], 'bad');
  await assert.rejects(a, /bad/);
  const b = client.latest('regen', 'examples', 'b');
  await settled();
  w.reply(w.sent[1], 'B');
  assert.equal((await b).result, 'B');
});

test('channels are independent', async () => {
  const w = new FakeWorker();
  const client = createClient([w]);
  client.latest('one', 'examples', 1);
  client.latest('two', 'examples', 2);
  assert.deepEqual(w.sent.map(m => m.args), [1, 2]);
});

test('regenerate fans out to the pool; only the primary answers, a helper only by failing', async () => {
  const [p, h1, h2] = [new FakeWorker(), new FakeWorker(), new FakeWorker()];
  const client = createClient([p, h1, h2]);
  const first = client.call('regenerate', { document: 'x' });
  assert.deepEqual([p, h1, h2].map(w => w.sent.length), [1, 1, 1]);
  assert.equal(h1.sent[0].id, p.sent[0].id, 'one id across the pool: the helpers tag their regions with it');
  h1.reply(h1.sent[0], null);
  h2.reply(h2.sent[0], null);
  p.reply(p.sent[0], 'mesh');
  assert.equal((await first).result, 'mesh');

  const second = client.call('regenerate', { document: 'y' });
  h2.fail(h2.sent[1], 'helper died');
  await assert.rejects(second, /helper died/);

  client.call('examples');
  assert.deepEqual([p, h1, h2].map(w => w.sent.length), [3, 2, 2], 'other ops go to the primary only');
});

test('start links each helper to the primary with a port and waits for every worker', async () => {
  const [p, h1, h2] = [new FakeWorker(), new FakeWorker(), new FakeWorker()];
  let started = false;
  const start = createClient([p, h1, h2]).start().then(() => { started = true; });
  assert.deepEqual([p, h1, h2].map(w => [w.sent[0].op, w.sent[0].args.index, w.sent[0].args.count, w.sent[0].args.ports.length]),
    [['pool', 0, 3, 2], ['pool', 1, 3, 1], ['pool', 2, 3, 1]]);
  assert.deepEqual(p.sent[0].transfer, p.sent[0].args.ports, 'ports are transferred');
  p.reply(p.sent[0], null);
  h1.reply(h1.sent[0], null);
  await settled();
  assert.equal(started, false);
  h2.reply(h2.sent[0], null);
  await start;
  for (const w of [p, h1, h2]) for (const port of w.sent[0].args.ports) port.close();
});

test('adaptResult: result v2 as written, plus the mesh, the round trip and each face\'s body', () => {
  const positions = new Float32Array(9), faces = new Int32Array([1]), sharpEdges = new Int32Array([0]);
  const info = {
    version: 2, slot: 'main', format: 'isoform/0', hash: 'ab', text: '{}', meshed: 2, reused: 1, triangles: 1, edges: 3,
    vertices: 2, faces: [['a/x+'], ['a/y+', 'b/z-']], bodies: [{ name: 'a', faces: [0] }, { name: 'b', faces: [1] }],
    diagnostics: [], thinSpots: [{ at: [1, 2, 3], faces: ['a/x+', 'a/y+'], thickness: 0.2 }], lineFaces: [[0, 1]], instances: [],
  };
  const r = adaptResult({ info, positions, faces, sharp: new Float32Array(6), smooth: new Float32Array(), sharpEdges, smoothEdges: new Int32Array() }, 12);
  const { error, ms, bodyOfFace, mesh, ...v2 } = r;
  assert.deepEqual(v2, info);
  assert.equal(error, null);
  assert.equal(ms, 12);
  assert.deepEqual(bodyOfFace, ['a', 'b']);
  assert.equal(mesh.faceOfTriangle, faces);
  assert.equal(mesh.sharpEdges, sharpEdges);

  const failed = adaptResult({ info: { version: 2, slot: 'main', error: 'no such path', failedEdit: 0, timing: {} } }, 3);
  assert.equal(failed.error, 'no such path');
  assert.equal(failed.failedEdit, 0);
  assert.equal(failed.ms, 3);
});

test('unpack reads the native kernel\'s binary reply into the worker\'s: the header, then each mesh buffer', () => {
  // As KernelSocket.Pack writes it: the header's length, the header padded with spaces to four bytes, the buffers.
  const header = new TextEncoder().encode(JSON.stringify({ id: 4, ok: true, result: { info: { hash: 'ab' } }, ms: 5, lengths: [9, 1, 6, 0, 1, 0] }));
  const length = Math.ceil(header.length / 4) * 4;
  const buffer = new ArrayBuffer(4 + length + 4 * 17);
  new DataView(buffer).setInt32(0, length, true);
  new Uint8Array(buffer, 4, length).fill(32).set(header);
  new Float32Array(buffer, 4 + length, 9).set([0, 1, 2, 3, 4, 5, 6, 7, 8.5]);
  new Int32Array(buffer, 4 + length + 36, 1).set([3]);
  new Float32Array(buffer, 4 + length + 40, 6).set([1, 1, 1, 2, 2, 2]);
  new Int32Array(buffer, 4 + length + 64, 1).set([0]);

  const { result, ...reply } = unpack(buffer);
  assert.deepEqual(reply, { id: 4, ok: true, ms: 5 });
  assert.deepEqual(result.info, { hash: 'ab' });
  assert.ok(result.positions instanceof Float32Array && result.faces instanceof Int32Array && result.sharpEdges instanceof Int32Array);
  assert.deepEqual([...result.positions], [0, 1, 2, 3, 4, 5, 6, 7, 8.5]);
  assert.deepEqual([...result.faces], [3]);
  assert.deepEqual([...result.sharp], [1, 1, 1, 2, 2, 2]);
  assert.deepEqual([...result.smooth], []);
  assert.deepEqual([...result.sharpEdges], [0]);
  assert.deepEqual([...result.smoothEdges], []);
});
