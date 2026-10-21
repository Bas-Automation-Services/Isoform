// Handle drag math (wwwroot/js/lib/dragmath.js): pointer ray → t → snapped, clamped edits. node --test
// The handles are a fixture in the C# contract (docs/playground-plan.md), so this runs without the kernel.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import {
  anchorAt, constrain, editsAt, frameOf, paramAt, shownHandles, startDrag, valueAt,
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
});

test('angle: degrees about the axis from u, counter-clockwise looking down it', () => {
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
  // A plane snaps each coordinate.
  near(constrain(handle('hole:center'), [2.4, -3.6]), [2, -4]);
});

test('edits: base + per·t for every path, with no float noise', () => {
  assert.deepEqual(editsAt(handle('plate:x+'), 3), [{ set: 'plate.size.0', value: 103 }]);
  // The -x face moves out by 3: the origin goes 3 down and the width 3 up.
  assert.deepEqual(editsAt(handle('plate:x-'), 3), [{ set: 'plate.origin.0', value: -3 }, { set: 'plate.size.0', value: 103 }]);
  assert.deepEqual(editsAt(handle('hole:center'), [2, -4]), [{ set: 'hole.center.0', value: 32 }, { set: 'hole.center.1', value: 21 }]);
  assert.deepEqual(editsAt(handle('hole:diameter'), 1.25), [{ set: 'hole.diameter', value: 12.5 }]);
  assert.deepEqual(editsAt({ edits: [{ path: 'a', base: 0.1, per: 2 }] }, 0.1), [{ set: 'a', value: 0.3 }], 'per may be a number');
});

test('valueAt follows the edit whose base is the value, not just the first', () => {
  assert.equal(valueAt(handle('plate:x-'), 3), 103);
  assert.equal(valueAt(handle('hole:diameter'), 1.25), 12.5);
});

test('anchorAt: moved along the axis or in the plane', () => {
  assert.deepEqual(anchorAt(handle('plate:x-'), 3), [-3, 25, 5]);
  assert.deepEqual(anchorAt(handle('hole:center'), [2, -4]), [32, 21, 10]);
  assert.deepEqual(anchorAt(handle('shaft:angle'), 40), [0, 0, 0]);
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

test('shownHandles: the first selected face\'s feature, never assembly frames', () => {
  assert.deepEqual(shownHandles(handles, ['plate/x+']).map(h => h.id), ['plate:x+', 'plate:x-', 'plate:z+']);
  assert.deepEqual(shownHandles(handles, ['hole/wall', 'plate/x+']).map(h => h.id), ['hole:center', 'hole:diameter']);
  assert.deepEqual(shownHandles(handles, []), []);
  assert.deepEqual(shownHandles(handles, ['upper/plate/z+']), []);
});
