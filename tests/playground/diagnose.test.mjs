// Diagnostics as the page shows them (wwwroot/js/lib/diagnose.js): grouping, the faces a message names, thin spots,
// the cell size that resolves a below-resolution report, and the verdict: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  diagnose, featureFaces, groups, mm, namesIn, niceBelow, referencedNames, refind, resolveCell, sizeOf, title, verdict,
} from '../../src/Isoform.Playground/wwwroot/js/lib/diagnose.js';

// What the kernel reported for example 0 with the hole moved to y = 44.4 (a 0.6 mm wall), at 0.5 mm cells; the
// message as the kernel formats it, culture-invariantly.
const thin = 'plate/y+ and hole/wall are 0.601 mm apart near (30.09, 49.70, 0.09), below the resolved size of 1 mm';
const result = (diagnostics, extra = {}) => ({
  faces: [['plate/x+'], ['plate/y+'], ['hole/wall'], ['plate/z+'], ['fillet/face'], ['plate/x-']],
  thinSpots: [
    { at: [28.6, 49.6, 0.09], faces: ['plate/y+', 'hole/wall'], thickness: 0.78 },
    { at: [30.1, 49.7, 0.09], faces: ['plate/y+', 'hole/wall'], thickness: 0.601 },
  ],
  diagnostics, cell: 0.5, ...extra,
});
const d = (code, feature, message) => ({ code, feature, message });

test('namesIn: the names a message mentions, in its order, whole names only', () => {
  const names = ['plate/x', 'plate/x+', 'hole/wall', 'plate/y+', 'bore-edge/face'];
  assert.deepEqual(namesIn(thin, names), ['plate/y+', 'hole/wall']);
  assert.deepEqual(namesIn('edge [plate/z+, plate/x+] does not exist on the part', [...names, 'plate/z+']), ['plate/z+', 'plate/x+']);
  assert.deepEqual(namesIn('plate/x touches it', names), ['plate/x']);
  assert.deepEqual(namesIn('lower/plate/x+ is thin', names), [], 'part of a longer (bundle) name');
  assert.deepEqual(namesIn('nothing here', names), []);
});

test('sizeOf: a thin spot\'s thickness, else the size in the message', () => {
  assert.equal(sizeOf(thin, { thickness: 0.601 }), 0.601);
  assert.equal(sizeOf(thin, null), 0.601);
  assert.equal(sizeOf('hole (0.6 mm) is below the resolved size of 1 mm; use a smaller CellSize'), 0.6);
  assert.equal(sizeOf('shell leaves a cavity about 0.25 mm thick, below the resolved size of 1 mm'), 0.25);
  assert.equal(sizeOf('shell leaves a cavity too thin to measure, if any'), null);
});

test('resolveCell: size / 2 and at least half the current cell, on a round value', () => {
  assert.equal(niceBelow(0.3), 0.25);
  assert.equal(niceBelow(0.2005), 0.2);
  assert.equal(niceBelow(1), 1);
  assert.equal(niceBelow(0.09), 0.05);
  assert.equal(resolveCell(0.601, 0.5), 0.25, 'the demo: a 0.6 mm wall at 0.5 mm cells');
  assert.equal(resolveCell(0.401, 0.5), 0.2);
  assert.equal(resolveCell(0.9, 0.5), 0.25, 'never coarser than half the current cell');
  assert.equal(resolveCell(null, 0.5), 0.25, 'size unknown: half the current cell');
  assert.equal(resolveCell(0.004, 0.5), null, 'a sliver no browser cell resolves');
});

test('diagnose: a thin spot names both faces, its thinnest point, and the cell that resolves it', () => {
  const [item] = diagnose(result([d('below-resolution', 'hole', thin)]));
  assert.equal(item.title, 'Below resolution');
  assert.deepEqual(item.faces, ['plate/y+', 'hole/wall']);
  assert.deepEqual(item.spot.at, [30.1, 49.7, 0.09]);
  assert.equal(item.size, 0.601);
  assert.equal(item.resolve, 0.25);
  assert.equal(item.stale, false);
  assert.equal(item.error, false);
});

test('diagnose: a preview greys out its below-resolution reports and offers no resolve', () => {
  const [below, fillet] = diagnose(result([d('below-resolution', 'hole', thin), d('fillet-invalid', 'fillet', 'radius 9 does not fit between the faces next to the edge')], { preview: true }));
  assert.equal(below.stale, true);
  assert.equal(below.resolve, null);
  assert.equal(fillet.stale, false, 'only below-resolution reports come from coarse cells');
});

test('diagnose: a feature-level diagnostic names no faces; its feature\'s faces are found by source', () => {
  const r = result([d('fillet-invalid', 'fillet', 'radius 9 does not fit between the faces next to the edge')]);
  const [item] = diagnose(r);
  assert.deepEqual(item.faces, []);
  assert.equal(item.spot, null);
  assert.equal(item.error, true);
  assert.deepEqual(featureFaces(r, 'fillet'), [4]);
  assert.deepEqual(featureFaces(r, 'plate'), [0, 1, 3, 5]);
  assert.deepEqual(featureFaces({ faces: [['lower/plate/x+'], ['upper/hole/wall']] }, 'hole'), [1], 'bundle names');
  assert.deepEqual(featureFaces(r, null), []);
});

test('a fillet that does not fit leaves no faces: the edge its definition names is what it is about', () => {
  const text = JSON.stringify({ format: 'isoform/0', features: [
    { id: 'plate', type: 'block', origin: [0, 0, 0], size: [100, 50, 10] },
    { id: 'fillet', type: 'fillet', edge: ['plate/z+', 'plate/x+'], radius: 12 },
    { id: 'round', type: 'fillet', edge: [], loop: 'plate/z+', radius: 1 },
  ] });
  const r = { ...result([d('fillet-invalid', 'fillet', 'radius 12 does not fit between the faces next to the edge')]), text };
  r.faces = r.faces.filter(([n]) => n !== 'fillet/face');
  const [item] = diagnose(r);
  assert.deepEqual(item.faces, ['plate/z+', 'plate/x+']);
  const names = new Set(r.faces.flat());
  assert.deepEqual(referencedNames(text, 'round', names), ['plate/z+']);
  assert.deepEqual(referencedNames(text, 'nothing', names), []);
  assert.deepEqual(referencedNames('{ not json', 'fillet', names), []);
});

test('groups: by code, errors first, then in the kernel\'s order', () => {
  const items = diagnose(result([
    d('below-resolution', 'hole', thin), d('tangent-contact', 'hole', 'hole/wall touches plate/y+ tangentially: zero-thickness contact'),
    d('fillet-invalid', 'fillet', 'radius 9 does not fit'), d('below-resolution', 'fillet', 'fillet (0.4 mm) is below the resolved size of 1 mm'),
    d('some-new-code', null, 'x'),
  ]));
  const g = groups(items);
  assert.deepEqual(g.map(x => x.code), ['fillet-invalid', 'tangent-contact', 'below-resolution', 'some-new-code']);
  assert.deepEqual(g[2].items.map(i => i.feature), ['hole', 'fillet']);
  assert.equal(g[3].title, 'Some new code');
  assert.equal(title('fillet-invalid'), 'Fillet does not fit');
});

test('refind: the focused diagnostic survives a regeneration that still reports it', () => {
  const before = diagnose(result([d('below-resolution', 'hole', thin)]))[0];
  const moved = 'plate/y+ and hole/wall are 0.55 mm apart near (30.09, 49.72, 0.09), below the resolved size of 1 mm';
  const after = diagnose(result([d('fillet-invalid', 'fillet', 'x'), d('below-resolution', 'hole', moved)]));
  assert.equal(refind(after, before).index, 1);
  assert.equal(refind(diagnose(result([])), before), null);
  assert.equal(refind(after, null), null);
});

test('verdict: valid, warnings, errors, a document that does not read; stale reports do not count', () => {
  assert.equal(verdict({ result: null, problem: null }), null);
  assert.deepEqual(verdict({ result: result([]) }), { kind: 'valid', text: 'Valid', detail: '0.5 mm cells' });
  assert.equal(verdict({ result: result([d('below-resolution', 'hole', thin)]) }).kind, 'warn');
  const two = verdict({ result: result([d('below-resolution', 'hole', thin), d('fillet-invalid', 'fillet', 'x')]) });
  assert.deepEqual([two.kind, two.text], ['error', '2 problems']);
  assert.equal(verdict({ result: result([d('below-resolution', 'hole', thin)], { preview: true }) }).text, 'Valid');
  assert.equal(verdict({ result: result([]), problem: 'unexpected end' }).kind, 'broken');
  assert.equal(mm(0.601), '0.601 mm');
  assert.equal(mm(0.25), '0.25 mm');
});
