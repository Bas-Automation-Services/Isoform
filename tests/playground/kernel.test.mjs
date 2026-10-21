// The published wasm kernel in Node: every example regenerates cleanly to result v2, and edits round-trip. Run with
// node --test; the published wwwroot comes from ISOFORM_PLAYGROUND, else out/playground/wwwroot.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { loadKernel } from './published.mjs';

// The page's default settings.
const cell = 0.5, chord = 0.05;

const kernel = await loadKernel();
const examples = Object.entries(JSON.parse(kernel.Examples()));

test('there are examples', () => assert.ok(examples.length > 0));

for (const [i, [name, document]] of examples.entries()) {
  test(`example ${i}: ${name}`, t => {
    const r = JSON.parse(kernel.Regenerate(document, '[]', cell, chord, 'main'));
    assert.equal(r.error, undefined);
    assert.equal(r.version, 2);
    assert.equal(r.slot, 'main');
    assert.equal(r.text, document, 'the examples are canonical text');
    assert.match(r.hash, /^[0-9a-f]{64}$/);
    assert.ok(r.faces.length > 0, 'no faces');
    assert.ok(r.triangles > 0, 'no triangles');
    assert.deepEqual(r.diagnostics.filter(d => d.code === 'invalid-parameter'), []);
    // The mesh buffers agree with the counts: three corners per triangle, one face index each; two end points and
    // one face pair per line segment.
    assert.equal(kernel.Positions().length, r.triangles * 9);
    assert.equal(kernel.Faces().length, r.triangles);
    assert.equal(kernel.Lines().length, r.lines * 6);
    assert.equal(kernel.SmoothLines().length, r.smoothLines * 6);
    const edges = [...kernel.LineEdges().slice(), ...kernel.SmoothLineEdges().slice()];
    assert.ok(edges.every(k => k >= 0 && k < r.lineFaces.length), 'a line without its face pair');
    assert.ok(r.lineFaces.every(([a, b]) => a < b && b < r.faces.length));
    if (r.format === 'isoform-bundle/0') assert.equal(r.instances.length, r.bodies.length);
    t.diagnostic(`${r.timing.regenerate.toFixed(0)} ms, ${r.triangles} triangles, ${r.faces.length} faces, ${r.diagnostics.length} diagnostics`);
  });
}

test('a broken document gives an error, not an exception', () => {
  const r = JSON.parse(kernel.Regenerate('{"format": "isoform-part/', '[]', cell, chord, 'main'));
  assert.equal(typeof r.error, 'string');
  assert.equal(kernel.Faces().length, 0);
});

test('edits are applied before regenerating; a bad one is named', () => {
  const [, document] = examples[0];
  const r = JSON.parse(kernel.Regenerate(document, '[{"set": "plate.size.0", "value": 120}]', cell, chord, 'main'));
  assert.equal(r.error, undefined);
  assert.equal(r.hash, JSON.parse(kernel.Apply(document, '{"set": "plate.size.0", "value": 120}')).hash);
  const bad = JSON.parse(kernel.Regenerate(document, '[{"remove": "slot"}]', cell, chord, 'main'));
  assert.equal(bad.failedEdit, 0);
  assert.match(bad.error, /slot/);
});

test('Apply(old, Diff(old, new)) gives new', () => {
  const parts = examples.map(([, d]) => d).filter(d => JSON.parse(d).format === 'isoform/0');
  for (const [old, now] of parts.map((d, i) => [d, parts[(i + 1) % parts.length]])) {
    const { edits } = JSON.parse(kernel.Diff(old, now));
    const applied = JSON.parse(kernel.Apply(old, JSON.stringify(edits)));
    assert.equal(applied.problem, undefined);
    assert.equal(applied.text, now);
  }
  assert.deepEqual(JSON.parse(kernel.Diff(parts[0], JSON.parse(kernel.Apply(parts[0], '{"set": "plate.size.0", "value": 120}')).text)).edits,
    [{ set: 'plate.size.0', value: 120 }]);
});

test('Handles(slot) gives the handles of the slot\'s last regeneration', () => {
  const [, document] = examples[0];
  const r = JSON.parse(kernel.Regenerate(document, '[]', cell, chord, 'main'));
  const { slot, hash, handles } = JSON.parse(kernel.Handles('main'));
  assert.equal(slot, 'main');
  assert.equal(hash, r.hash);
  const x = handles.find(h => h.id === 'plate:x+');
  assert.equal(x.kind, 'linear');
  assert.deepEqual(x.axis, [1, 0, 0]);
  assert.deepEqual(x.edits, [{ path: 'plate.size.0', base: 100, per: [1] }]);
  assert.deepEqual(JSON.parse(kernel.Handles('nothing')).handles, []);
});

test('Resolve: a pin by index comes back by name, and a cut across plate/z+ splits it', () => {
  const [, document] = examples[0];
  const r = JSON.parse(kernel.Regenerate(document, '[]', cell, chord, 'main'));
  const top = r.faces.findIndex(f => f[0] === 'plate/z+');
  const [pin] = JSON.parse(kernel.Resolve(JSON.stringify([{ id: 'top', face: top }]), 'main')).pins;
  assert.deepEqual(pin, { id: 'top', ref: { face: 'plate/z+' }, status: 'resolved', faces: [top] });
  const slot = { insert: { id: 'slot', type: 'block', op: 'cut', origin: [60, -1, 5], size: [5, 52, 10] }, before: 'fillet' };
  kernel.Regenerate(document, JSON.stringify([slot]), cell, chord, 'main');
  const after = JSON.parse(kernel.Resolve(JSON.stringify([{ id: 'top', ...pin.ref }]), 'main'));
  assert.equal(after.pins[0].status, 'split');
  assert.equal(after.pins[0].faces.length, 2);
  assert.match(JSON.parse(kernel.Resolve('[]', 'nothing')).error, /no result/);
});
