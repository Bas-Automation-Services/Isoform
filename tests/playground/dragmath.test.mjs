// Handle drag math (wwwroot/js/lib/dragmath.js): pointer ray → t → snapped, clamped edits. node --test
// The handles are a fixture in the kernel's shape (src/Isoform.Interactive/README.md; kernel.test.mjs checks the ones
// example 0 has against the kernel), so this runs without it.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import {
  anchorAt, constrain, countEdits, editsAt, featureOf, frameOf, paramAt, shownHandles, startDrag, valueAt,
} from '../../src/Isoform.Playground/wwwroot/js/lib/dragmath.js';

const handles = JSON.parse(readFileSync(new URL('./handles.fixture.json', import.meta.url), 'utf8'));
const handle = id => handles.find(h => h.id === id);
const near = (actual, expected, eps = 1e-9) =>
  [actual].flat().forEach((a, i) => assert.ok(Math.abs(a - [expected].flat()[i]) < eps, `${actual} is not ${expected}`));

// A ray looking straight down -z onto the point (x, y), and one from the front (-y) through (x, z).
const down = (x, y) => ({ origin: [x, y, 100], direction: [0, 0, -1] });
const front = (x, z) => ({ origin: [x, -100, z], direction: [0, 1, 0] });

test('frameOf: right-handed, u from world x unless the axis runs along x, or the handle\'s own u', () => {
  assert.deepEqual(frameOf({ axis: [0, 0, 2] }), { n: [0, 0, 1], u: [1, 0, 0], v: [0, 1, 0] });
  const { n, u, v } = frameOf({ axis: [1, 0, 0] });
  near(n, [1, 0, 0]); near(u, [0, 1, 0]); near(v, [0, 0, 1]);
  // A sketch plane on xz with normal -y: its x is world x and its y world z.
  near(frameOf({ axis: [0, -1, 0], u: [1, 0, 0] }).v, [0, 0, 1]);
  near(frameOf({ axis: [0, 0, 1], u: [1, 1, 0] }).u, [Math.SQRT1_2, Math.SQRT1_2, 0]);
  // The kernel's v as given, even when it is not n × u.
  near(frameOf(handle('sketch:corner:b')).v, [0, 0, -1]);
  // An angle's u points from its center to its anchor.
  near(frameOf({ kind: 'angle', axis: [0, 0, 1], anchor: [0, 5, 3], center: [0, 0, 3] }).u, [0, 1, 0]);
});

test('linear: the point on the axis closest to the ray, from any side', () => {
  const h = handle('plate:x+');
  near(paramAt(h, down(130, 0)), 30);
  near(paramAt(h, front(90, 40)), -10);
  // A skewed ray: from (120, -50, 5) towards +y and up, closest to the axis where it crosses x = 120.
  near(paramAt(h, { origin: [120, -50, 5], direction: [0, 1, 1] }), 20);
  // Looking along the axis gives nothing.
  assert.equal(paramAt(h, { origin: [200, 25, 5], direction: [-1, 0, 0] }), null);
});

test('planar: the ray\'s hit on the plane, in the plane\'s u and v', () => {
  const h = handle('hole:center');
  near(paramAt(h, down(33, 21)), [3, -4]);
  near(paramAt(h, { origin: [30, 25, 20], direction: [1, 1, -1] }), [10, 10]);
  assert.equal(paramAt(h, front(0, 10)), null, 'a ray in the plane');
  // Along the kernel's u and v: here v is -z.
  near(paramAt(handle('sketch:corner:b'), front(8, 3)), [3, 4]);
});

test('angle: degrees about the axis through center (not the anchor), counter-clockwise looking down it', () => {
  const h = handle('shaft:angle');
  near(paramAt(h, down(10, 0)), 0);
  near(paramAt(h, down(0, 10)), 90);
  near(paramAt(h, down(-10, -10)), -135);
});

test('constrain: the value snaps to its step and stays within min and max', () => {
  const width = handle('plate:x+');
  near(constrain(width, 2.4), 2);
  near(constrain(width, 2.6), 3);
  near(constrain(width, -150), 0.001 - 100, 1e-6);
  // Diameter = 10 + 2t snaps to 0.5 mm, so t to 0.25 mm.
  near(constrain(handle('hole:diameter'), 1.3), 1.25);
  const angle = handle('shaft:angle');
  near(constrain(angle, 52), 50); // 322° snaps to 320°
  near(constrain(angle, 100), 90);
  near(constrain(angle, -400), 1 - 270);
  // A plane snaps each of its values, not t: from 30.5 a hole lands on 31, not 30.5 + 1.
  near(constrain(handle('hole:center'), [2.4, -3.6]), [2, -4]);
  near(constrain({ ...handle('hole:center'), value: [30.5, 25] }, [0.2, 0.4]), [0.5, 0]);
  // Fine (shift): a tenth of the step.
  near(constrain(handle('plate:x+'), 2.44, true), 2.4);
  near(constrain(handle('hole:center'), [2.44, -3.66], true), [2.4, -3.7]);
});

test('edits: base + per·t for every path, with no float noise', () => {
  assert.deepEqual(editsAt(handle('plate:x+'), 3), [{ set: 'plate.size.0', value: 103 }]);
  // The -x face moves out by 3: the origin goes 3 down and the width 3 up.
  assert.deepEqual(editsAt(handle('plate:x-'), 3), [{ set: 'plate.size.0', value: 103 }, { set: 'plate.origin.0', value: -3 }]);
  assert.deepEqual(editsAt(handle('hole:center'), [2, -4]), [{ set: 'hole.center.0', value: 32 }, { set: 'hole.center.1', value: 21 }]);
  assert.deepEqual(editsAt(handle('hole:diameter'), 1.25), [{ set: 'hole.diameter', value: 12.5 }]);
  assert.deepEqual(editsAt({ edits: [{ path: 'a', base: 0.1, per: 2 }] }, 0.1), [{ set: 'a', value: 0.3 }], 'per may be a number');
});

test('valueAt, min and max are edits[0]\'s; a plane has two values', () => {
  assert.equal(valueAt(handle('plate:x-'), 3), 103);
  assert.equal(valueAt(handle('hole:diameter'), 1.25), 12.5);
  assert.deepEqual(valueAt(handle('hole:center'), [2, -4]), [32, 21]);
  // The -x face: min bounds the width (edits[0]), not the origin.
  near(constrain(handle('plate:x-'), -150), 0.001 - 100, 1e-6);
});

test('anchorAt: moved along the axis or in the plane', () => {
  assert.deepEqual(anchorAt(handle('plate:x-'), 3), [-3, 25, 5]);
  assert.deepEqual(anchorAt(handle('hole:center'), [2, -4]), [32, 21, 10]);
  // An angle turns its anchor about the axis through center.
  near(anchorAt(handle('shaft:angle'), 90), [0, 12, 10]);
  near(anchorAt(handle('shaft:angle'), -30), [12 * Math.cos(Math.PI / 6), -6, 10]);
});

test('a linear drag is relative to where it was grabbed, snapped and clamped', () => {
  const h = handle('plate:x+');
  const drag = startDrag(h, down(110, 25)); // grabbed at the arrow, 10 mm off the face
  assert.deepEqual(drag.move(down(110, 30)), { t: 0, value: 100, edits: [{ set: 'plate.size.0', value: 100 }] });
  assert.deepEqual(drag.move(down(124.6, 0)), { t: 15, value: 115, edits: [{ set: 'plate.size.0', value: 115 }] });
  // Looking along the axis keeps the last state.
  assert.equal(drag.move({ origin: [0, 25, 5], direction: [1, 0, 0] }).value, 115);
  near(drag.move(down(-500, 0)).value, 0.001);
  assert.equal(startDrag(h, { origin: [200, 25, 5], direction: [-1, 0, 0] }), null);
});

test('a planar drag moves both coordinates', () => {
  const drag = startDrag(handle('hole:center'), down(31, 26));
  assert.deepEqual(drag.move(down(36.2, 18.9)).edits, [{ set: 'hole.center.0', value: 35 }, { set: 'hole.center.1', value: 18 }]);
});

test('an angle drag accumulates past half a turn and clamps', () => {
  const h = handle('shaft:angle');
  const drag = startDrag(h, down(10, 0));
  assert.equal(drag.move(down(0, 10)).value, 360, '270 + 90, at the max');
  const back = startDrag(h, down(10, 0));
  // Clockwise round through -90, -180 (= 180) to -225: 225 degrees back, not 135 forward.
  back.move(down(0, -10));
  back.move(down(-10, 0.001));
  back.move(down(-10, -0.001));
  assert.equal(back.move(down(-10, 10)).value, 45);
});

test('featureOf: the name up to the first slash', () => {
  assert.equal(featureOf('plate/x+'), 'plate');
  assert.equal(featureOf('round/plate/x+'), 'round');
  assert.equal(featureOf('row/2:bore/wall'), 'row');
  assert.equal(featureOf('shell/inner:plate/z+'), 'shell');
});

test('countEdits: the count stepped, within its minimum, none when an expression sets it', () => {
  const row = handle('row:spacing');
  assert.deepEqual(countEdits(row, 1), [{ set: 'row.linear.count', value: 4 }]);
  assert.deepEqual(countEdits(row, -1), [{ set: 'row.linear.count', value: 2 }]);
  assert.equal(countEdits({ count: { ...row.count, value: 2 } }, -1), null);
  assert.equal(countEdits({ count: { ...row.count, path: null } }, 1), null);
  assert.equal(countEdits(handle('plate:x+'), 1), null);
});

test('shownHandles: the first selected face\'s feature, never assembly frames', () => {
  assert.deepEqual(shownHandles(handles, ['plate/x+']).map(h => h.id), ['plate:x+', 'plate:x-', 'plate:z+']);
  assert.deepEqual(shownHandles(handles, ['hole/wall', 'plate/x+']).map(h => h.id), ['hole:center', 'hole:diameter']);
  assert.deepEqual(shownHandles(handles, ['row/2:bore/wall']).map(h => h.id), ['row:spacing'], 'a pattern copy\'s face');
  assert.deepEqual(shownHandles(handles, []), []);
  assert.deepEqual(shownHandles(handles, ['upper/plate/z+']), []);
});
