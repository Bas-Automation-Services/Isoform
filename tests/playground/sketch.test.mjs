// The sketch mode's pure parts (wwwroot/js/sketch/): ids, tools to entities and constraints, inference, dimensions,
// regions under a click, colours and the edits a finish commits: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  addArc, addCircle, addConstraint, addLine, addPoint, addRectangle, addSlot, adopt, applicable, constraint, constraintsOf,
  finishEdits, finishLabel, freshFeatureId, freshId, kindOf, nameProblem, newSketch, refsOf, remove, rename, settledSketch,
  toggleConstruction, workingDocument,
} from '../../src/Isoform.Playground/wwwroot/js/sketch/model.js';
import { infer, nearestOn, tangentArc, tangentLine } from '../../src/Isoform.Playground/wwwroot/js/sketch/infer.js';
import { label, measure, options, parseValue, showValue } from '../../src/Isoform.Playground/wwwroot/js/sketch/dimension.js';
import { inside, loopPolygon, regionAt, regionKey, shapes } from '../../src/Isoform.Playground/wwwroot/js/sketch/regions.js';
import { bounds, fit, fromScreen, frameOfPlane, gridStep, toScreen, toSketch, toWorld, zoom } from '../../src/Isoform.Playground/wwwroot/js/sketch/frame.js';
import { describeConstraint, states, summary } from '../../src/Isoform.Playground/wwwroot/js/sketch/status.js';
import { cornerAt, corners, filletArc, filletEdit, filletRadius, runEdit, trimEdit, trimPiece } from '../../src/Isoform.Playground/wwwroot/js/sketch/edit.js';

const blank = () => newSketch('s', { face: 'plate/z+' });
const at = (u, v) => ({ at: [u, v] });
const solvedOf = s => ({ sketch: s, points: s.points, radii: {} });
const close = (a, b, eps = 1e-9) => assert.ok(Math.abs(a - b) < eps, `${a} is not ${b}`);
const closeAll = (a, b) => a.forEach((x, i) => close(x, b[i]));

// A 60 x 40 rectangle with its corners p1 (0,0), p2 (60,0), p3 (60,40), p4 (0,40) and sides e1 bottom, e2 right, e3 top, e4 left.
const rect = () => addRectangle(blank(), at(0, 0), at(60, 40)).sketch;

test('ids are the first free p1, p2 … and e1, e2 … over points and entities alike', () => {
  const s = rect();
  assert.deepEqual(Object.keys(s.points), ['p1', 'p2', 'p3', 'p4']);
  assert.deepEqual(s.entities.map(e => e.id), ['e1', 'e2', 'e3', 'e4']);
  assert.equal(freshId(s, 'p'), 'p5');
  const renamed = rename(s, 'e2', 'p5');
  assert.equal(freshId(renamed, 'p'), 'p6');
  assert.equal(freshId(renamed, 'e'), 'e2');
  assert.equal(freshFeatureId({ features: [{ id: 'extrude1' }, { id: 'sketch1' }] }, 'extrude'), 'extrude2');
});

test('a rectangle is four lines round shared corners, two horizontal and two vertical', () => {
  const s = rect();
  assert.deepEqual(s.entities.map(e => e.line), [['p1', 'p2'], ['p2', 'p3'], ['p3', 'p4'], ['p4', 'p1']]);
  assert.deepEqual(s.points.p2, [60, 0]);
  assert.deepEqual(s.points.p4, [0, 40]);
  assert.deepEqual(s.constraints, [{ horizontal: 'e1' }, { vertical: 'e2' }, { horizontal: 'e3' }, { vertical: 'e4' }]);
});

test('a pick on an existing point reuses it; a new point gets the pick\'s constraints', () => {
  const s = rect();
  const { sketch: t, entity, end } = addLine(s, { at: [60, 40], point: 'p3' }, { at: [30, 0], constraints: [{ kind: 'midpoint', entity: 'e1' }] }, { construction: true });
  assert.deepEqual(t.entities.at(-1), { id: entity, line: ['p3', end], construction: true });
  assert.equal(end, 'p5');
  assert.deepEqual(t.constraints.at(-1), { midpoint: ['p5', 'e1'] });
  const origin = addPoint(s, { at: [0, 0], constraints: [{ kind: 'fix' }] });
  assert.deepEqual(origin.sketch.constraints.at(-1), { fix: origin.point });
  const plumb = addLine(s, at(100, 0), at(100, 30), { hv: 'vertical', tangent: 'e9' }).sketch;
  assert.deepEqual(plumb.constraints.slice(-2), [{ vertical: 'e5' }, { tangent: ['e5', 'e9'] }]);
});

test('a circle through a point keeps to it; an arc runs counter-clockwise with its end on the radius', () => {
  const { sketch: c } = addCircle(rect(), at(30, 20), { at: [60, 40], point: 'p3' });
  const circle = c.entities.at(-1);
  close(circle.circle.radius, Math.hypot(30, 20));
  assert.deepEqual(c.constraints.at(-1), { on: ['p3', circle.id] });

  const { sketch: a, entity } = addArc(blank(), at(0, 0), at(10, 0), at(0, 20));
  const arc = a.entities.find(e => e.id === entity).arc;
  closeAll(a.points[arc.to], [0, 10]);
  assert.equal(arc.from, 'p2');
  // Drawn clockwise: the ends swap so the stored arc is counter-clockwise.
  const cw = addArc(blank(), at(0, 0), at(10, 0), at(0, -10), { ccw: false }).sketch.entities[0].arc;
  assert.deepEqual([cw.from, cw.to], ['p3', 'p2']);
});

test('a slot is two lines and two arcs, the lines tangent to both arcs, the arcs equal', () => {
  const { sketch: s, entities } = addSlot(blank(), at(0, 0), at(40, 0), 5);
  const [bottom, capB, top, capA] = entities.map(id => s.entities.find(e => e.id === id));
  assert.ok(bottom.line && top.line && capA.arc && capB.arc);
  closeAll(s.points[bottom.line[0]], [0, -5]);
  closeAll(s.points[top.line[0]], [40, 5]);
  // Each cap turns counter-clockwise through the far end of the slot.
  assert.equal(capB.arc.from, bottom.line[1]);
  assert.equal(capB.arc.to, top.line[0]);
  assert.deepEqual(s.constraints.map(kindOf), ['tangent', 'tangent', 'tangent', 'tangent', 'equal']);
});

test('remove takes an entity\'s unshared points and every constraint naming what is gone', () => {
  const s = addConstraint(rect(), constraint('length', ['e1'], 60));
  const t = remove(s, ['e1']);
  assert.deepEqual(t.entities.map(e => e.id), ['e2', 'e3', 'e4']);
  assert.ok('p1' in t.points && 'p2' in t.points, 'the corners are still used by e2 and e4');
  assert.ok(!t.constraints.some(c => refsOf(c).includes('e1')));
  const u = remove(s, ['p1']);
  assert.deepEqual(u.entities.map(e => e.id), ['e2', 'e3'], 'the lines on p1 go with it');
  assert.ok(!('p1' in u.points));
  assert.equal(addConstraint(s, { horizontal: 'e1' }), s, 'the same constraint twice is one');
});

test('rename moves every reference; names follow the kernel\'s rules', () => {
  const s = addConstraint(rect(), constraint('distance', ['p1', 'e3'], 40));
  const t = rename(rename(s, 'e3', 'top'), 'p1', 'origin');
  assert.equal(t.entities[2].id, 'top');
  assert.deepEqual(t.entities[0].line, ['origin', 'p2']);
  assert.deepEqual(t.constraints.at(-1), { distance: ['origin', 'top'], value: 40 });
  assert.deepEqual(t.points.origin, [0, 0]);
  assert.equal(nameProblem(s, 'top'), null);
  assert.match(nameProblem(s, 'e2'), /taken/);
  assert.equal(nameProblem(s, 'e2', 'e2'), null);
  for (const bad of ['', 'a.b', 'a/b', 'start', 'end', '=x']) assert.ok(nameProblem(s, bad), bad);
});

test('construction toggles on for the chosen entities, off when they all have it', () => {
  const s = toggleConstruction(rect(), ['e1', 'e2']);
  assert.deepEqual(s.entities.map(e => !!e.construction), [true, true, false, false]);
  assert.deepEqual(toggleConstruction(s, ['e1', 'e2']).entities.map(e => !!e.construction), [false, false, false, false]);
  assert.deepEqual(toggleConstruction(s, ['e1', 'e3']).entities.map(e => !!e.construction), [true, true, true, false]);
});

test('a solve\'s points and radii are adopted', () => {
  const { sketch: s } = addCircle(rect(), at(30, 20), at(35, 20));
  const t = adopt(s, { points: { ...s.points, p1: [1, 2] }, radii: { e5: 7 } });
  assert.deepEqual(t.points.p1, [1, 2]);
  assert.equal(t.entities[4].circle.radius, 7);
});

test('the constrain buttons offer what fits the selection', () => {
  const s = addCircle(rect(), at(30, 20), at(35, 20)).sketch;
  const kinds = sel => applicable(s, sel).map(a => a.kind);
  assert.deepEqual(kinds(['e1']), ['horizontal', 'vertical']);
  assert.deepEqual(kinds(['e1', 'e2']), ['parallel', 'perpendicular', 'equal']);
  assert.deepEqual(kinds(['e1', 'e5']), ['tangent']);
  assert.deepEqual(kinds(['p1', 'p3']), ['coincident', 'horizontal', 'vertical', 'fix']);
  assert.deepEqual(kinds(['p5', 'e1']), ['on', 'midpoint']);
  assert.deepEqual(kinds(['p1', 'p3', 'e2']), ['symmetric']);
  assert.deepEqual(constraintsOf({ kind: 'fix', refs: ['p1', 'p2'] }), [{ fix: 'p1' }, { fix: 'p2' }]);
  assert.deepEqual(constraintsOf({ kind: 'on', refs: ['p5', 'e1'] }), [{ on: ['p5', 'e1'] }]);
});

test('inference snaps to points, the origin, midpoints, curves, then level and plumb; shift turns it off', () => {
  const s = addCircle(rect(), at(30, 20), at(40, 20)).sketch; // e5: radius 10 round p5 (30, 20)
  const ctx = { sketch: s, points: s.points, tol: 2 };
  assert.deepEqual(infer([59, 41], ctx), { at: [60, 40], point: 'p3', constraints: [], glyph: 'coincident' });
  assert.equal(infer([59, 41], { ...ctx, exclude: new Set(['p3']) }).point, undefined);
  assert.deepEqual(infer([31, 1], ctx), { at: [30, 0], constraints: [{ kind: 'midpoint', entity: 'e1' }], glyph: 'midpoint' });
  const on = infer([45, 0.5], ctx);
  assert.deepEqual([on.at, on.constraints, on.glyph], [[45, 0], [{ kind: 'on', entity: 'e1' }], 'on']);
  const round = infer([30, 31], ctx);
  closeAll(round.at, [30, 30]);
  assert.deepEqual(round.constraints, [{ kind: 'on', entity: 'e5' }]);
  assert.deepEqual(infer([80, 101], { ...ctx, from: [100, 100] }), { at: [80, 100], constraints: [], hv: 'horizontal', glyph: 'horizontal' });
  assert.deepEqual(infer([99, 80], { ...ctx, from: [100, 100] }).hv, 'vertical');
  assert.deepEqual(infer([59, 41], { ...ctx, shift: true }), { at: [59, 41], constraints: [], glyph: null });
  // The origin, when no point is there yet: a new point fixed at it.
  const empty = blank();
  assert.deepEqual(infer([1, -1], { sketch: empty, points: {}, tol: 2 }), { at: [0, 0], constraints: [{ kind: 'fix' }], glyph: 'origin' });
});

test('an arc is only near where it runs', () => {
  const s = addArc(blank(), at(0, 0), at(10, 0), at(0, 10)).sketch; // the quarter from (10, 0) to (0, 10)
  closeAll(nearestOn(s.entities[0], [8, 8], s.points), [10 * Math.SQRT1_2, 10 * Math.SQRT1_2]);
  assert.equal(nearestOn(s.entities[0], [-8, -8], s.points), null);
  const line = addLine(blank(), at(0, 0), at(10, 0)).sketch;
  assert.equal(nearestOn(line.entities[0], [12, 1], line.points), null);
});

test('a line on from an arc\'s end along its tangent snaps to it and is tangent; so is an arc from a line\'s end', () => {
  const s = addArc(blank(), at(0, 0), at(10, 0), at(0, 10)).sketch; // ends at p3 (0, 10), tangent there (-1, 0)
  const t = tangentLine('p3', [-20, 11], { sketch: s, points: s.points });
  assert.equal(t.tangent, 'e1');
  closeAll(t.at, [-20, 10]);
  assert.equal(tangentLine('p3', [-20, 20], { sketch: s, points: s.points }), null);
  const l = addLine(blank(), at(0, 0), at(10, 0)).sketch;
  const a = tangentArc('p2', [10.3, 5], { sketch: l, points: l.points });
  assert.equal(a.tangent, 'e1');
  closeAll(a.at, [10, 5]);
});

test('dimensions fit what was clicked and measure the solved sketch', () => {
  const s = addCircle(addArc(rect(), at(100, 0), at(110, 0), at(100, 10)).sketch, at(30, 20), at(35, 20)).sketch;
  const solved = solvedOf(s);
  const of = picks => options(picks, solved).map(o => [o.kind, o.refs, +o.value.toFixed(6)]);
  assert.deepEqual(of(['e1']), [['length', ['e1'], 60], ['horizontal-distance', ['p1', 'p2'], 60], ['vertical-distance', ['p1', 'p2'], 0]]);
  assert.deepEqual(of(['p1', 'p3']), [['distance', ['p1', 'p3'], +Math.hypot(60, 40).toFixed(6)], ['horizontal-distance', ['p1', 'p3'], 60], ['vertical-distance', ['p1', 'p3'], 40]]);
  assert.deepEqual(of(['e1', 'p3']), [['distance', ['p3', 'e1'], 40]]);
  assert.deepEqual(of(['e1', 'e3']), [['distance', ['e1', 'e3'], 40]]);
  assert.deepEqual(of(['e1', 'e2']), [['angle', ['e1', 'e2'], 90]]);
  assert.deepEqual(of(['e5']).map(o => o[0]), ['radius', 'diameter']);
  assert.deepEqual(of(['e6']), [['diameter', ['e6'], 10], ['radius', ['e6'], 5]]);
  close(measure('angle', ['e2', 'e1'], solved), -90);
});

test('typed values: numbers above 0, expressions kept, an angle keeps its sense', () => {
  assert.equal(parseValue(' 25 ', 'length'), 25);
  assert.equal(parseValue('=width / 2', 'length'), '=width / 2');
  assert.equal(parseValue('=', 'length'), null);
  assert.equal(parseValue('abc', 'length'), null);
  assert.equal(parseValue('0', 'length'), null);
  assert.equal(parseValue('0', 'distance'), 0);
  assert.equal(parseValue('30', 'angle', -60), -30);
  assert.equal(parseValue('30', 'angle', 60), 30);
  assert.equal(showValue(-30, 'angle'), '30');
  assert.equal(showValue(12.345678, 'length'), '12.346');
  assert.equal(showValue('=w', 'length'), '=w');
});

test('a dimension label says its value off the geometry, with lines to what it measures', () => {
  const s = rect();
  const solved = solvedOf(s);
  assert.deepEqual(label({ length: 'e1', value: 60 }, solved, 5), { text: '60', at: [30, 5], lines: [[[0, 0], [0, 5]], [[60, 0], [60, 5]], [[0, 5], [60, 5]]] });
  assert.deepEqual(label({ length: 'e1', value: '=width' }, solved, 5).text, '=width');
  assert.deepEqual(label({ 'vertical-distance': ['p1', 'p4'], value: 40 }, solved, 5).at, [5, 20]);
  assert.deepEqual(label({ distance: ['p3', 'e1'], value: 40 }, solved, 5), { text: '40', at: [60, 20], lines: [[[60, 40], [60, 0]]] });
  // Away from the middle of the sketch: below the bottom line, left of the left side.
  assert.deepEqual(label({ length: 'e1', value: 60 }, solved, 5, [30, 20]).at, [30, -5]);
  assert.deepEqual(label({ 'vertical-distance': ['p1', 'p4'], value: 40 }, solved, 5, [30, 20]).at, [-5, 20]);
  assert.deepEqual(label({ 'horizontal-distance': ['p4', 'p3'], value: 60 }, solved, 5, [30, 20]).at, [30, 45]);
  const c = addCircle(s, at(30, 20), at(40, 20)).sketch;
  const d = label({ diameter: 'e5', value: 20 }, solvedOf(c), 2);
  assert.equal(d.text, '⌀20');
  closeAll(d.lines[0][0], [30 - 10 * Math.SQRT1_2, 20 - 10 * Math.SQRT1_2]);
});

test('regions are found under a click: inside the outer loop, outside its holes, the smallest first', () => {
  const s = addCircle(rect(), at(30, 20), at(35, 20)).sketch; // e5: radius 5 round (30, 20)
  const regions = [{ outer: ['e1', 'e2', 'e3', 'e4'], holes: [['e5']] }, { outer: ['e5'], holes: [] }];
  const shaped = shapes(regions, s, s.points);
  assert.equal(regionAt(shaped, [5, 5]), 0);
  assert.equal(regionAt(shaped, [31, 21]), 1, 'inside the hole is the island');
  assert.equal(regionAt(shaped, [70, 5]), -1);
  assert.equal(shaped[0].key, regionKey(['e4', 'e3', 'e2', 'e1']));
  // A loop listed out of order, with an entity running backwards, still chains.
  const poly = loopPolygon(['e3', 'e1', 'e4', 'e2'], { ...s, entities: s.entities.map(e => e.id === 'e4' ? { ...e, line: ['p1', 'p4'] } : e) }, s.points);
  assert.ok(inside(poly, [59, 39]) && !inside(poly, [61, 20]));
  assert.equal(poly.length, 5);
  // An arc's loop bulges where the arc runs.
  const d = addArc(addLine(blank(), at(-10, 0), at(10, 0)).sketch, at(0, 0), at(10, 0), at(-10, 0)).sketch; // e2: upper half
  const half = loopPolygon(['e1', 'e2'], { ...d, entities: d.entities.map(e => e.id === 'e2' ? { ...e, arc: { ...e.arc, to: 'p1', from: 'p2' } } : e) }, d.points);
  assert.ok(inside(half, [0, 9]) && !inside(half, [0, -1]));
});

test('free entities are blue, fixed ones ink, a conflict red; the summary counts free degrees', () => {
  const s = addConstraint(addConstraint(rect(), { fix: 'p1' }), { length: 'e1', value: 60 });
  const reply = { free: ['p3', 'p4'], freeDegrees: 1, problems: [] };
  const { state } = states(s, reply);
  assert.equal(state.get('e1'), 'fixed');
  assert.equal(state.get('e2'), 'free');
  assert.equal(state.get('p3'), 'free');
  assert.deepEqual(summary(reply), { text: '1 free degree', level: 'free' });
  const conflict = { free: [], freeDegrees: 0, problems: [{ code: 'sketch-conflict', message: '…', constraints: [5] }] };
  const c = states(s, conflict);
  assert.equal(c.state.get('e1'), 'conflict');
  assert.equal(c.state.get('e3'), 'fixed');
  assert.deepEqual([...c.conflicts], [5]);
  assert.equal(summary(conflict).level, 'error');
  assert.equal(summary({ free: [], freeDegrees: 0, problems: [] }).text, 'fully constrained');
  assert.equal(describeConstraint({ length: 'e1', value: 60 }, 5), '#5 length e1 = 60');
});

test('finishing inserts the settled sketch and its feature, or sets an edited sketch, as one edit list', () => {
  const s = rect();
  const extrude = finishEdits(s, { feature: { op: 'extrude', id: 'pad', depth: 5, regions: [['e1', 'e2', 'e3', 'e4']] } });
  assert.deepEqual(extrude, [{ insert: s }, { insert: { id: 'pad', type: 'extrude', op: 'join', sketch: 's', regions: [['e1', 'e2', 'e3', 'e4']], start: 0, end: 5 } }]);
  const cut = finishEdits(s, { feature: { op: 'cut', id: 'pocket', depth: 3, regions: [] } })[1].insert;
  assert.deepEqual([cut.op, cut.start, cut.end, 'regions' in cut], ['cut', -3, 1, false]);
  const revolve = finishEdits(s, { feature: { op: 'revolve', id: 'turn', angle: 360, axis: 'e4', regions: [] }, hasBody: false })[1].insert;
  assert.deepEqual(revolve, { id: 'turn', type: 'revolve', op: 'new', sketch: 's', axis: 'e4', angle: 360 });
  assert.deepEqual(finishEdits(s, { editing: true }), [{ set: 's', value: s }]);
  assert.deepEqual(finishEdits(s, {}), [{ insert: s }]);
  assert.equal(finishLabel(s, { feature: { op: 'extrude', id: 'pad' } }), 'sketch s, extrude pad');
});

test('the working document holds the sketch at its place, or at the end; the settled sketch is read back', () => {
  const base = JSON.stringify({ format: 'isoform/0', units: 'mm', features: [{ id: 'plate' }, { id: 's', old: true }, { id: 'pad' }] });
  const s = rect();
  assert.deepEqual(JSON.parse(workingDocument(base, s)).features.map(f => f.id), ['plate', 's', 'pad']);
  assert.deepEqual(JSON.parse(workingDocument(base, s)).features[1], s);
  assert.deepEqual(JSON.parse(workingDocument(base, { ...s, id: 'n' })).features.map(f => f.id), ['plate', 's', 'pad', 'n']);
  assert.deepEqual(settledSketch(workingDocument(base, s), 's'), s);
});

test('the view maps the plane to the screen with x right and y up, and zooms about the pointer', () => {
  const f = frameOfPlane({ origin: [0, 0, 10], x: [1, 0, 0], normal: [0, 0, -1] });
  assert.deepEqual(f.y, [0, -1, 0]);
  closeAll(toWorld(f, [3, 4]), [3, -4, 10]);
  closeAll(toSketch(f, [3, -4, 10]), [3, 4]);
  const view = fit(bounds([[0, 0], [100, 50]]), 1000, 500, 0);
  assert.deepEqual(view, { center: [50, 25], scale: 10, width: 1000, height: 500 });
  assert.deepEqual(toScreen(view, [0, 50]), [0, 0]);
  assert.deepEqual(fromScreen(view, [1000, 500]), [100, 0]);
  const z = zoom(view, 2, [250, 125]);
  closeAll(fromScreen(z, [250, 125]), fromScreen(view, [250, 125]));
  assert.equal(z.scale, 20);
  assert.equal(gridStep(10), 2);
  assert.equal(gridStep(1), 20);
});

// --- Fillet and trim (sketch/edit.js): what the tools pick and preview, and the edit through a fake kernel ---------

test('the fillet tool picks corners where two lines or arcs end, the nearest within reach', () => {
  const s = addArc(rect(), at(60, 20), { at: [60, 0], point: 'p2' }, { at: [60, 40], point: 'p3' }).sketch; // e5 round (60, 20) from p2 to p3, two more ends there
  assert.deepEqual(corners(rect()), ['p1', 'p2', 'p3', 'p4']);
  assert.deepEqual(corners(s), ['p1', 'p4'], 'three entities end at p2 and p3; a centre is no corner');
  const r = rect();
  assert.equal(cornerAt(r, r.points, [59, 39], 2), 'p3');
  assert.equal(cornerAt(r, r.points, [30, 20], 2), null);
  assert.equal(filletRadius(r, r.points, 'p3'), 8, 'a fifth of the shorter side');
});

test('a fillet previews its arc tangent to both lines, and none it could not make', () => {
  const r = rect();
  const arc = filletArc(r, r.points, 'p3', 5); // the corner (60, 40) between e2 (down) and e3 (left)
  closeAll(arc[0], [60, 35]);
  closeAll(arc.at(-1), [55, 40]);
  closeAll(arc[arc.length >> 1], [55 + 5 * Math.SQRT1_2, 35 + 5 * Math.SQRT1_2]);
  assert.equal(filletArc(r, r.points, 'p3', 45), null, 'the tangent point would run off the 40 long side');
  const a = addArc(addLine(blank(), at(-10, 0), at(10, 0)).sketch, at(20, 0), at(10, 0), at(20, 10)).sketch;
  assert.equal(filletArc(a, a.points, 'p2', 2), null, 'a corner with an arc is the kernel\'s to show');
});

test('the trim tool lights the piece between crossings nearest the pointer', () => {
  const s = addCircle(rect(), at(60, 20), at(70, 20)).sketch; // e5: radius 10 round (60, 20), across the right side e2
  const piece = p => trimPiece(s, s.points, {}, 'e2', p).polyline;
  assert.deepEqual(piece([60, 5]), [[60, 0], [60, 10]]);
  assert.deepEqual(piece([61, 25]), [[60, 10], [60, 30]]);
  assert.deepEqual(piece([60, 39]), [[60, 30], [60, 40]]);
  assert.deepEqual(trimPiece(s, s.points, {}, 'e1', [30, 0]).polyline, [[0, 0], [60, 0]], 'a side nothing crosses is one piece');
  const round = trimPiece(s, s.points, {}, 'e5', [70, 20]);
  close(round.to - round.from, Math.PI);
  closeAll(round.polyline[0], [60, 10]);
  closeAll(round.polyline.at(-1), [60, 30]);
  const left = trimPiece(s, s.points, {}, 'e5', [50, 20]);
  closeAll(left.polyline[0], [60, 30]);
  closeAll(left.polyline.at(-1), [60, 10]);
  const lone = addCircle(blank(), at(0, 0), at(5, 0)).sketch;
  assert.equal(trimPiece(lone, lone.points, {}, 'e1', [5, 0]).to, 2 * Math.PI, 'a circle crossed by nothing is all one piece');
  // An arc crossed once: past its sweep, the pointer picks the nearer end's piece.
  const a = addLine(addArc(blank(), at(0, 0), at(10, 0), at(-10, 0)).sketch, at(0, -5), at(0, 15)).sketch; // upper half, a line up the middle
  closeAll(trimPiece(a, a.points, {}, 'e1', [9, -3]).polyline[0], [10, 0]);
  closeAll(trimPiece(a, a.points, {}, 'e1', [9, -3]).polyline.at(-1), [0, 10]);
  closeAll(trimPiece(a, a.points, {}, 'e1', [-9, -3]).polyline[0], [0, 10]);
});

// A fake kernel: answers every call with answer (a reply, a function of the args, or an Error to fail with).
function kernel(answer) {
  const calls = [];
  const call = async (op, args) => {
    calls.push({ op, args });
    if (answer instanceof Error) throw answer;
    return { result: typeof answer === 'function' ? answer(args) : answer };
  };
  return { call, calls };
}
const base = JSON.stringify({ format: 'isoform/0', units: 'mm', features: [{ id: 'plate', type: 'block' }] });
// The kernel's answer to an edit (SketchJson.Edit): the edited sketch solved and settled in the document.
const solved = (sketch, more = {}) => ({ solved: true, freeDegrees: 0, free: [], points: sketch.points, radii: {}, problems: [], regions: [],
  settled: { text: workingDocument(base, sketch), hash: 'h' }, added: [], dropped: [], ...more });
const filleted = s => ({ ...s, points: { ...s.points, 'p3-fillet-e2': [60, 35] }, entities: [...s.entities, { id: 'p3-fillet', arc: { center: 'c', from: 'p3-fillet-e2', to: 'x' } }] });

test('a fillet asks the kernel for sketchEdit on the working document and is one undo step', async () => {
  const s = rect(), { call, calls } = kernel(args => solved(filleted(JSON.parse(args.document).features.at(-1)), { added: ['p3-fillet', 'p3-fillet-e2'] }));
  const r = await runEdit(call, { base, sketch: s, undo: [] }, filletEdit('p3', 5));
  assert.deepEqual(calls.map(c => c.op), ['sketchEdit']);
  assert.deepEqual(calls[0].args.edit, { op: 'fillet', corner: 'p3', radius: 5 });
  assert.equal(calls[0].args.id, 's');
  assert.deepEqual(JSON.parse(calls[0].args.document).features.map(f => f.id), ['plate', 's']);
  assert.deepEqual(r.undo, [s], 'the sketch before is the one step back');
  assert.ok(r.sketch.entities.some(e => e.id === 'p3-fillet'), 'the kernel\'s sketch is the new one');
  assert.equal(r.reply.solved, true);
  assert.deepEqual(r.note, { text: 'fillet p3 R5', level: 'ok', items: [] });
  assert.deepEqual(trimEdit('e2', [61, 25]), { op: 'trim', entity: 'e2', x: 61, y: 25 });
});

test('a trim names every constraint it dropped; a refusal keeps the sketch and says why', async () => {
  const s = addConstraint(rect(), constraint('length', ['e2'], 40));
  const dropped = ['#4 (length e2 = 40)'];
  const trim = await runEdit(kernel(solved(remove(s, ['e2']), { dropped })).call, { base, sketch: s, undo: [] }, trimEdit('e2', [60, 20]));
  assert.equal(trim.undo.length, 1);
  assert.deepEqual(trim.note, { text: 'trim e2 dropped 1 constraint:', level: 'warn', items: dropped });

  const before = { base, sketch: s, undo: [rect()] };
  const refused = await runEdit(kernel({ error: 'the radius is larger than e3 allows' }).call, before, filletEdit('p3', 50));
  assert.equal(refused.sketch, s);
  assert.equal(refused.undo, before.undo, 'no undo step');
  assert.equal(refused.reply, null);
  assert.deepEqual(refused.note, { text: 'fillet p3 R50: the radius is larger than e3 allows', level: 'error', items: [] });
  const failed = await runEdit(kernel(new Error('There is no op sketchEdit.')).call, before, trimEdit('e1', [1, 0]));
  assert.equal(failed.note.level, 'error');
  assert.match(failed.note.text, /^trim e1: There is no op sketchEdit/);
});
