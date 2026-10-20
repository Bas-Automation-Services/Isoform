// The reference pins panel's pure parts (wwwroot/js/panels/references.js): what a selection pins, replies folded in.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { label, pinTarget, withStatus } from '../../src/Isoform.Playground/wwwroot/js/panels/references.js';

const result = { faces: [['plate/z+'], ['hole/wall'], ['plate/z+']] };

test('pinTarget: two names an edge, one a face, by index when it is the face clicked', () => {
  assert.equal(pinTarget([], result), null);
  assert.deepEqual(pinTarget(['plate/z+', 'hole/wall'], result, 2), { edge: ['plate/z+', 'hole/wall'] });
  assert.deepEqual(pinTarget(['plate/z+'], result, 2), { face: 2 });
  assert.deepEqual(pinTarget(['plate/z+'], result, 1), { face: 'plate/z+' }, 'the click was on another face');
  assert.deepEqual(pinTarget(['plate/z+'], result), { face: 'plate/z+' });
});

test('withStatus: replies by id replace status and faces; unanswered pins stay', () => {
  const pins = [{ id: 'p1', ref: { face: 'plate/z+' }, status: 'resolved', faces: [0] }, { id: 'p2', ref: { face: 'hole/wall' }, status: 'resolved', faces: [1] }];
  const after = withStatus(pins, [{ id: 'p1', ref: { face: 'plate/z+' }, status: 'split', faces: [0, 4] }]);
  assert.deepEqual(after.map(p => [p.status, p.faces]), [['split', [0, 4]], ['resolved', [1]]]);
  assert.equal(after[1], pins[1]);
});

test('label: a face by name, an edge by its two', () => {
  assert.equal(label({ face: 'plate/z+', signature: ['a'] }), 'plate/z+');
  assert.equal(label({ edge: ['plate/z+', 'hole/wall'] }), 'plate/z+ | hole/wall');
});
