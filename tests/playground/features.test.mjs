// The feature tree and inspector model (wwwroot/js/lib/features.js): items, faces, fields and edit paths: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  bodiesOf, describe, diagnosticsOf, documentOf, facesOf, fieldsOf, itemsOf, keyOfFace, parseScalar, scrubbed, setEdit, treeOf,
} from '../../src/Isoform.Playground/wwwroot/js/lib/features.js';

const part = {
  format: 'isoform/0', units: 'mm', parameters: { width: 100, bore: '=width / 10' },
  features: [
    { id: 'plate', type: 'block', op: 'new', origin: [0, 0, 0], size: ['=width', 50, 10] },
    { id: 'hole', type: 'hole', center: [30, 25], diameter: '=bore' },
    { id: 'rib', type: 'extrude', op: 'join', plane: { origin: [0, 0, 10], x: [1, 0, 0], normal: [0, 0, 1] },
      profile: { start: [10, 10], edges: [{ id: 'a', to: [40, 10] }, { id: 'b', to: [10, 10], bulge: 1 }] }, start: 0, end: 6 },
    { id: 'fillet', type: 'fillet', edge: ['plate/z+', 'plate/x+'], radius: 2 },
    { id: 'round', type: 'fillet', loop: 'rib/end', radius: 1 },
    { id: 'row', type: 'pattern', features: ['hole'], linear: { direction: [1, 0, 0], spacing: 20, count: 3 } },
  ],
};

const bundle = {
  format: 'isoform-bundle/0', units: 'mm',
  assembly: {
    format: 'isoform-assembly/0', units: 'mm',
    components: [
      { id: 'lower', part: 'bracket.json', placement: { origin: [0, 0, 0], x: [1, 0, 0], normal: [0, 0, 1] } },
      { id: 'pin', part: 'pin.json', parameters: { length: 25 }, placement: { origin: [10, 10, -9], x: [1, 0, 0], normal: [0, 0, 1] } },
    ],
    mates: [{ id: 'seat', kind: 'axis', a: 'pin/pin/shank', b: 'lower/bore/wall' }],
  },
  documents: { 'bracket.json': {}, 'pin.json': {} },
};

const result = (doc, faces, diagnostics = []) => ({ text: JSON.stringify(doc), faces, diagnostics });
const field = (fields, path) => {
  for (const f of fields) {
    if (f.path === path) return f;
    const inner = f.fields && field(f.fields, path);
    if (inner) return inner;
  }
  return null;
};

test('tree: a single-body part is the parameter table and the features in order', () => {
  const tree = treeOf(part);
  assert.equal(tree.length, 1);
  assert.equal(tree[0].title, null);
  assert.deepEqual(itemsOf(tree).map(i => i.key), ['parameters', 'plate', 'hole', 'rib', 'fillet', 'round', 'row']);
  assert.deepEqual(itemsOf(tree).map(describe), ['2 defined', 'block', 'hole', 'extrude · join', 'fillet', 'fillet', 'pattern']);
  assert.deepEqual(itemsOf(treeOf(bundle)).map(describe), ['component', 'component', 'mate · axis']);
});

test('tree: several bodies group the features by the body they work on', () => {
  const features = [
    { id: 'base', type: 'block', op: 'new', origin: [0, 0, 0], size: [10, 10, 10] },
    { id: 'tab', type: 'block', op: 'new', origin: [20, 0, 0], size: [5, 5, 5], body: 'lid' },
    { id: 'edge', type: 'fillet', edge: ['base/z+', 'base/x+'], radius: 1 },   // the body of its faces, not the current one
    { id: 'drill', type: 'hole', center: [22, 2], diameter: 1 },             // the current body
    { id: 'back', type: 'hole', center: [5, 5], diameter: 1, body: 'base' },
  ];
  assert.deepEqual([...bodiesOf(features)], [['base', 'base'], ['tab', 'lid'], ['edge', 'base'], ['drill', 'lid'], ['back', 'base']]);
  const tree = treeOf({ format: 'isoform/0', features });
  assert.deepEqual(tree.map(g => g.title), [null, 'base', 'lid']);
  assert.deepEqual(tree.map(g => g.items.map(i => i.id)), [['parameters'], ['base', 'edge', 'back'], ['tab', 'drill']]);
});

test('tree: a bundle is its components and mates, keyed by their edit paths', () => {
  const tree = treeOf(bundle);
  assert.deepEqual(tree.map(g => g.title), ['Components', 'Mates']);
  assert.deepEqual(itemsOf(tree).map(i => i.key), ['components.lower', 'components.pin', 'mates.seat']);
  assert.deepEqual(treeOf(null), [{ title: null, items: [{ key: 'parameters', kind: 'parameters', id: 'parameters', type: 'parameters', json: {} }] }]);
});

test('face to item: the feature (part) or component (assembly) its name starts with', () => {
  assert.equal(keyOfFace(part, 'plate/z+'), 'plate');
  assert.equal(keyOfFace(part, 'row/2:hole/wall'), 'row');
  assert.equal(keyOfFace(part, 'round/rib/a'), 'round');
  assert.equal(keyOfFace(bundle, 'pin/pin/shank'), 'components.pin');
  assert.equal(keyOfFace(part, undefined), null);
});

test('item to faces: every face with a source under the id, a mate its two faces', () => {
  const faces = [['plate/z+'], ['hole/wall'], ['row/1:hole/wall'], ['plate/x+', 'fillet/face'], ['fillet/face'], ['holes/wall']];
  const r = result(part, faces);
  const items = itemsOf(treeOf(part));
  const of = key => facesOf(r, items.find(i => i.key === key));
  assert.deepEqual(of('plate'), [0, 3]);
  assert.deepEqual(of('hole'), [1]);           // not "holes/…", not the pattern's copy
  assert.deepEqual(of('row'), [2]);
  assert.deepEqual(of('fillet'), [3, 4]);
  assert.deepEqual(of('parameters'), []);
  const b = result(bundle, [['lower/plate/z+'], ['lower/bore/wall'], ['pin/pin/shank'], ['pin/pin/tip']]);
  const parts = itemsOf(treeOf(bundle));
  assert.deepEqual(facesOf(b, parts[1]), [2, 3]);
  assert.deepEqual(facesOf(b, parts[2]), [1, 2]);
  assert.deepEqual(facesOf(null, parts[0]), []);
});

test('diagnostics: those naming the item, a component also those of its features', () => {
  const r = result(part, [], [{ code: 'below-resolution', feature: 'hole', message: 'm' }, { code: 'x', feature: null, message: 'n' },
    { code: 'y', feature: 'holes', message: 'o' }]);
  const items = itemsOf(treeOf(part));
  assert.deepEqual(diagnosticsOf(r, items.find(i => i.key === 'hole')).map(d => d.code), ['below-resolution']);
  assert.deepEqual(diagnosticsOf(result(bundle, [], [{ code: 'z', feature: 'pin/lead' }]), itemsOf(treeOf(bundle))[1]).map(d => d.code), ['z']);
});

test('fields: generated from the JSON, every one with the DocumentEdits path it sets', () => {
  const items = itemsOf(treeOf(part));
  const plate = fieldsOf(items.find(i => i.key === 'plate'));
  assert.deepEqual(plate.map(f => [f.kind, f.path]), [['choice', 'plate.op'], ['vector', 'plate.origin'], ['vector', 'plate.size']]);
  assert.deepEqual(plate[0].options, ['new', 'join', 'cut']);
  assert.deepEqual(plate[2].entries.map(e => [e.path, e.label, e.value]), [['plate.size.0', 'x', '=width'], ['plate.size.1', 'y', 50], ['plate.size.2', 'z', 10]]);

  const rib = fieldsOf(items.find(i => i.key === 'rib'));
  assert.equal(field(rib, 'rib.blend').value, 0);                       // optional, shown at its default
  assert.equal(field(rib, 'rib.plane.normal').kind, 'vector');
  assert.deepEqual(field(rib, 'rib.profile.start').entries.map(e => e.label), ['u', 'v']);
  assert.deepEqual(field(rib, 'rib.profile.edges.0.to').entries.map(e => e.path), ['rib.profile.edges.0.to.0', 'rib.profile.edges.0.to.1']);
  assert.equal(field(rib, 'rib.profile.edges.0.bulge').value, 0);
  assert.equal(field(rib, 'rib.profile.edges.1.bulge').value, 1);
  assert.equal(field(rib, 'rib.profile.edges.0.id'), null);             // ids name faces: not edited here
  assert.equal(rib.find(f => f.kind === 'group' && f.label === 'profile').fields[1].label, 'edges a');

  const fillet = fieldsOf(items.find(i => i.key === 'fillet'));
  assert.deepEqual(fillet[0], { kind: 'faces', path: 'fillet.edge', label: 'edge', value: ['plate/z+', 'plate/x+'], count: 2 });
  assert.deepEqual(fieldsOf(items.find(i => i.key === 'round'))[0], { kind: 'faces', path: 'round.loop', label: 'loop', value: 'rib/end', count: 1 });

  const row = fieldsOf(items.find(i => i.key === 'row'));
  assert.deepEqual(row[0], { kind: 'text', path: 'row.features', label: 'features', value: ['hole'], list: true });
  assert.deepEqual(field(row, 'row.linear.count'), { kind: 'scalar', path: 'row.linear.count', label: 'count', value: 3, integer: true });

  assert.deepEqual(fieldsOf(items[0]), [{ kind: 'parameters', path: 'parameters', entries: [
    { kind: 'scalar', path: 'parameters.width', label: 'width', value: 100, integer: false },
    { kind: 'scalar', path: 'parameters.bore', label: 'bore', value: '=width / 10', integer: false },
  ] }]);
});

test('fields: components and mates take assembly paths', () => {
  const [, pin, seat] = itemsOf(treeOf(bundle));
  const c = fieldsOf(pin);
  assert.deepEqual(c.map(f => f.path ?? f.label), ['components.pin.part', 'parameters', 'placement', 'components.pin.grounded']);
  assert.equal(field(c, 'components.pin.parameters.length').value, 25);
  assert.equal(field(c, 'components.pin.placement.origin').entries[2].path, 'components.pin.placement.origin.2');
  const m = fieldsOf(seat);
  assert.deepEqual(m.map(f => [f.kind, f.path]), [['choice', 'mates.seat.kind'], ['faces', 'mates.seat.a'], ['faces', 'mates.seat.b'], ['scalar', 'mates.seat.offset']]);
  assert.deepEqual(m[0].options, ['mate', 'flush', 'axis']);
  const [, merge] = itemsOf(treeOf({ format: 'isoform/0', features: [{ id: 'merge', type: 'combine', tool: 'tab', op: 'cut' }] }));
  assert.deepEqual(fieldsOf(merge).map(f => [f.kind, f.path, f.options?.join()]),
    [['text', 'merge.tool', undefined], ['choice', 'merge.op', 'join,cut'], ['bool', 'merge.keep', undefined]]);
});

test('typed values: numbers, expressions with or without "=", nothing', () => {
  assert.equal(parseScalar(' 12.5 '), 12.5);
  assert.equal(parseScalar('-3e-1'), -0.3);
  assert.equal(parseScalar('=width / 2'), '=width / 2');
  assert.equal(parseScalar('width / 2'), '=width / 2');
  assert.equal(parseScalar(''), undefined);
  assert.deepEqual(setEdit('hole.diameter', 12), { set: 'hole.diameter', value: 12 });
});

test('scrubbing: a hundredth of the order of magnitude per 2 px, one per 8 px for counts', () => {
  assert.equal(scrubbed(100, 20), 110);
  assert.equal(scrubbed(10, 20), 11);
  assert.equal(scrubbed(10, -21), 9);
  assert.equal(scrubbed(2, 6), 2.03);
  assert.equal(scrubbed(0.5, 4), 0.52);
  assert.equal(scrubbed(0, 4), 0.02);
  assert.equal(scrubbed(10, 20, { fast: true }), 20);
  assert.equal(scrubbed(3, 16, { integer: true }), 5);
  assert.equal(scrubbed(2, -400, { integer: true }), 1);
});

test('documentOf: the result\'s canonical text, or null', () => {
  assert.deepEqual(documentOf(result(part, [])), part);
  assert.equal(documentOf(null), null);
  assert.equal(documentOf({ text: '{' }), null);
});
