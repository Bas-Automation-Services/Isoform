// The published wasm kernel in Node: every example regenerates cleanly. Run with node --test; the published
// wwwroot comes from ISOFORM_PLAYGROUND, else out/playground/wwwroot.
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
    const r = JSON.parse(kernel.Regenerate(document, cell, chord));
    assert.equal(r.error, undefined);
    assert.ok(r.faces.length > 0, 'no faces');
    assert.ok(r.triangles > 0, 'no triangles');
    assert.deepEqual(r.diagnostics.filter(d => d.code === 'invalid-parameter'), []);
    // The mesh buffers agree with the counts: three corners per triangle, one face index each.
    assert.equal(kernel.Positions().length, r.triangles * 9);
    assert.equal(kernel.Faces().length, r.triangles);
    t.diagnostic(`${r.ms.toFixed(0)} ms, ${r.triangles} triangles, ${r.faces.length} faces, ${r.diagnostics.length} diagnostics`);
  });
}

test('a broken document gives an error, not an exception', () => {
  const r = JSON.parse(kernel.Regenerate('{"format": "isoform-part/', cell, chord));
  assert.equal(typeof r.error, 'string');
  assert.equal(kernel.Faces().length, 0);
});
