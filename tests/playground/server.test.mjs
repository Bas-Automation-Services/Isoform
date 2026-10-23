// The native kernel server through the page's own client (kernel-client.js connectKernel): every example regenerates
// to result v2 with its mesh buffers, as the published wasm kernel gives it, and the other ops answer as the worker's
// do. Run with node --test; ISOFORM_SERVER is a running server's URL, else one is built and started.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { after, test } from 'node:test';
import { adaptResult, connectKernel, createClient } from '../../src/Isoform.Playground/wwwroot/js/kernel-client.js';
import { loadKernel, publishedRoot } from './published.mjs';
import { startServer } from './server.mjs';

const cell = 0.5, chord = 0.05;

const root = publishedRoot();
const server = process.env.ISOFORM_SERVER
  ? { kernel: `${process.env.ISOFORM_SERVER.replace(/^http/, 'ws').replace(/\/?$/, '/')}kernel`, close() {} }
  : await startServer(root);
const [main, preview] = await Promise.all([connectKernel(server.kernel), connectKernel(server.kernel)]);
after(() => {
  main?.terminate();
  preview?.terminate();
  server.close();
});
if (!main || !preview) throw new Error(`Nothing answers at ${server.kernel}`);
const client = createClient([main]), previewClient = createClient([preview]);
const call = async (op, args) => (await client.call(op, args)).result;
const wasm = await loadKernel(root);
const examples = Object.entries(await call('examples'));

test('the server has the examples the wasm kernel has, and takes the pool request as a pool of one', async () => {
  await client.start();
  assert.deepEqual(Object.fromEntries(examples), JSON.parse(wasm.Examples()));
});

for (const [i, [name, document]] of examples.entries()) {
  test(`example ${i}: ${name}`, async t => {
    const reply = await client.call('regenerate', { document, cell, chord });
    const r = adaptResult(reply.result, reply.ms);
    assert.equal(r.error, null);
    assert.equal(r.version, 2);
    assert.equal(r.slot, 'main');
    assert.equal(r.cell, cell);
    assert.equal(r.text, document);
    assert.ok(r.faces.length > 0 && r.triangles > 0);
    const { mesh } = r;
    assert.ok(mesh.positions instanceof Float32Array && mesh.faceOfTriangle instanceof Int32Array);
    assert.equal(mesh.positions.length, r.triangles * 9);
    assert.equal(mesh.faceOfTriangle.length, r.triangles);
    assert.equal(mesh.sharp.length, r.lines * 6);
    assert.equal(mesh.smooth.length, r.smoothLines * 6);
    assert.equal(mesh.sharpEdges.length, r.lines);
    assert.equal(mesh.smoothEdges.length, r.smoothLines);
    assert.ok([...mesh.sharpEdges, ...mesh.smoothEdges].every(k => k >= 0 && k < r.lineFaces.length));

    // The same kernel: the same document, faces and diagnostics as in WebAssembly.
    const w = JSON.parse(wasm.Regenerate(document, '[]', cell, chord, 'main', false));
    assert.equal(r.hash, w.hash);
    assert.deepEqual(r.faces, w.faces);
    assert.deepEqual(r.diagnostics.map(d => d.code), w.diagnostics.map(d => d.code));
    t.diagnostic(`native ${r.timing.regenerate.toFixed(0)} ms, wasm ${w.timing.regenerate.toFixed(0)} ms, ${r.triangles} triangles`);
  });
}

test('a broken document and a bad edit come back as errors, without a mesh', async () => {
  const broken = await call('regenerate', { document: '{"format": "isoform-part/', cell, chord });
  assert.equal(typeof broken.info.error, 'string');
  assert.equal(broken.positions, undefined);
  const bad = await call('regenerate', { document: examples[0][1], edits: [{ remove: 'slot' }], cell, chord });
  assert.equal(bad.info.failedEdit, 0);
  await assert.rejects(client.call('no-such-op'), /no-such-op/);
});

test('apply, diff, handles and resolve answer as the wasm kernel does', async () => {
  const [, document] = examples[0];
  const edit = { set: 'plate.size.0', value: 120 };
  assert.deepEqual(await call('apply', { document, edits: edit }), JSON.parse(wasm.Apply(document, JSON.stringify(edit))));
  const wider = JSON.parse(wasm.Apply(document, JSON.stringify(edit))).text;
  assert.deepEqual(await call('diff', { before: document, after: wider }), JSON.parse(wasm.Diff(document, wider)));

  await call('regenerate', { document, cell, chord });
  wasm.Regenerate(document, '[]', cell, chord, 'main', false);
  assert.deepEqual(await call('handles', { slot: 'main' }), JSON.parse(wasm.Handles('main')));
  const refs = [{ id: 'p', face: 'plate/z+' }];
  assert.deepEqual(await call('resolve', { refs }), JSON.parse(wasm.Resolve(JSON.stringify(refs), 'main')));
  assert.ok((await call('memory')) > 0);
});

test('step and dxf answer as the wasm kernel does', async () => {
  const [, document] = examples[0];
  await call('regenerate', { document, cell, chord });
  wasm.Regenerate(document, '[]', cell, chord, 'main', false);
  assert.deepEqual(await call('step', { name: 'plate' }), JSON.parse(wasm.Step('main', 'plate')));
  assert.deepEqual(await call('dxf', { face: 'plate/z+' }), JSON.parse(wasm.Dxf('main', '', '', 'plate/z+')));
  const sketched = readFileSync(new URL('./sketched.fixture.json', import.meta.url), 'utf8');
  assert.deepEqual(await call('dxf', { document: sketched, sketch: 'base' }), JSON.parse(wasm.Dxf('main', sketched, 'base', '')));
});

test('sketchEdit answers as the wasm kernel does', async () => {
  const sketched = readFileSync(new URL('./sketched.fixture.json', import.meta.url), 'utf8');
  const edit = { op: 'fillet', corner: 'c' };
  assert.deepEqual(await call('sketchEdit', { document: sketched, id: 'base', edit }), JSON.parse(wasm.SketchEdit(sketched, 'base', JSON.stringify(edit))));
  const doc = JSON.parse(sketched);
  const document = JSON.stringify({ ...doc, features: [...doc.features, { id: 'top', type: 'sketch', on: { face: 'plate/end' }, points: {}, entities: [], constraints: [] }] });
  const project = { op: 'project', faces: ['plate/end', 'plate/right'], near: [60, 10, 5], linked: true };
  assert.deepEqual(await call('sketchEdit', { document, id: 'top', edit: project }), JSON.parse(wasm.SketchEdit(document, 'top', JSON.stringify(project))));
});

test('each connection is a session of its own: the previews\' slot is not the main one\'s', async () => {
  const [, document] = examples[0];
  const { result } = await previewClient.call('regenerate', { document, edits: [{ set: 'plate.size.0', value: 104 }], cell, chord, slot: 'preview', snap: true });
  assert.equal(result.info.slot, 'preview');
  assert.equal(result.info.refinement, 0, 'previews are not refined');
  assert.equal((await previewClient.call('handles', { slot: 'preview' })).result.hash, result.info.hash);
  assert.deepEqual((await call('handles', { slot: 'preview' })).handles, []);
});
