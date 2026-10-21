// Inference while drawing: where a click near something lands and what it adds. Pure, so it is tested in Node.
//
//   infer(cursor, {sketch, points, tol, from, exclude, shift}) → pick
//     cursor   [u, v] in sketch coordinates; points the solved {name: [u, v]}; tol the snap distance in mm
//     from     the point the segment being drawn starts at ([u, v]), for horizontal and vertical
//     exclude  names not to snap to (the entity being drawn)
//   pick: {at, point?, constraints: [{kind, entity?}], hv?, glyph}: an existing point to reuse (coincident, the way the
//   format says it), the sketch origin (a new point fixed there), a line's midpoint, a point on a line, arc or circle,
//   or a free point, kept level or plumb with from when it is nearly so. glyph names what will be added.
//
// Shift draws without inference: the pick is the cursor.

const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1];
const len = a => Math.hypot(a[0], a[1]);
const dist = (a, b) => len(sub(a, b));

/** The angle of p round c, in [0, 2π). */
const angleOf = (p, c) => (Math.atan2(p[1] - c[1], p[0] - c[0]) + 2 * Math.PI) % (2 * Math.PI);

/** Whether the angle a lies on the counter-clockwise sweep from a0 to a1. */
export function onSweep(a, a0, a1) {
  const span = (a1 - a0 + 2 * Math.PI) % (2 * Math.PI) || 2 * Math.PI;
  return (a - a0 + 2 * Math.PI) % (2 * Math.PI) <= span + 1e-12;
}

/** The nearest point of an entity to p, or null where p does not project onto it (past a line's ends, off an arc). */
export function nearestOn(e, p, points, radii = {}) {
  if (e.line) {
    const [a, b] = e.line.map(n => points[n]);
    const d = sub(b, a), t = dot(sub(p, a), d) / dot(d, d);
    return t < 0 || t > 1 ? null : [a[0] + t * d[0], a[1] + t * d[1]];
  }
  const c = points[e.arc?.center ?? e.circle.center];
  const r = e.arc ? dist(points[e.arc.from], c) : radii[e.id] ?? e.circle.radius;
  const a = Math.atan2(p[1] - c[1], p[0] - c[0]);
  if (e.arc && !onSweep(angleOf(p, c), angleOf(points[e.arc.from], c), angleOf(points[e.arc.to], c))) return null;
  return [c[0] + r * Math.cos(a), c[1] + r * Math.sin(a)];
}

export function infer(cursor, { sketch, points, radii = {}, tol, from = null, exclude = new Set(), shift = false }) {
  const free = { at: cursor, constraints: [], glyph: null };
  if (shift) return free;
  const near = (at, best) => dist(at, cursor) <= tol && (!best || dist(at, cursor) < dist(best.at, cursor));

  // Points first: a point to reuse beats anything else.
  let best = null;
  for (const [name, at] of Object.entries(points)) if (!exclude.has(name) && near(at, best)) best = { at, point: name, constraints: [], glyph: 'coincident' };
  if (best) return best;
  if (!Object.values(points).some(at => dist(at, [0, 0]) < 1e-9) && near([0, 0], null))
    return { at: [0, 0], constraints: [{ kind: 'fix' }], glyph: 'origin' };

  const shown = sketch.entities.filter(e => !exclude.has(e.id) && !(e.line ?? []).some(n => exclude.has(n)));
  for (const e of shown.filter(e => e.line)) {
    const [a, b] = e.line.map(n => points[n]);
    const mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
    if (near(mid, best)) best = { at: mid, constraints: [{ kind: 'midpoint', entity: e.id }], glyph: 'midpoint' };
  }
  if (best) return best;

  for (const e of shown) {
    const at = nearestOn(e, cursor, points, radii);
    if (at && near(at, best)) best = { at, constraints: [{ kind: 'on', entity: e.id }], glyph: 'on' };
  }
  if (best) return best;

  if (from) {
    const [du, dv] = sub(cursor, from);
    if (Math.abs(dv) <= tol && Math.abs(du) > tol) return { at: [cursor[0], from[1]], constraints: [], hv: 'horizontal', glyph: 'horizontal' };
    if (Math.abs(du) <= tol && Math.abs(dv) > tol) return { at: [from[0], cursor[1]], constraints: [], hv: 'vertical', glyph: 'vertical' };
  }
  return free;
}

/**
 * A line from point `start` (a name) aimed at the cursor, carrying on from an arc that ends there: when the cursor
 * is within `degrees` of the arc's tangent at that end, the line's end snaps onto the tangent and the line gets a
 * tangent constraint. Returns {at, tangent: arc id} or null.
 */
export function tangentLine(start, cursor, { sketch, points, degrees = 8 }) {
  const p = points[start];
  for (const e of sketch.entities) {
    if (!e.arc || (e.arc.from !== start && e.arc.to !== start)) continue;
    const r = sub(p, points[e.arc.center]);
    const t = [-r[1] / len(r), r[0] / len(r)]; // counter-clockwise along the arc
    const d = sub(cursor, p), along = dot(d, t);
    if (len(d) === 0 || Math.abs(along) / len(d) < Math.cos(degrees * Math.PI / 180)) continue;
    return { at: [p[0] + along * t[0], p[1] + along * t[1]], tangent: e.id };
  }
  return null;
}

/**
 * An arc starting at the end `start` of a line, centred near the line's normal there: the centre snaps onto the
 * normal and the arc gets a tangent constraint. Returns {at (the centre), tangent: line id} or null.
 */
export function tangentArc(start, cursor, { sketch, points, degrees = 8 }) {
  const p = points[start];
  for (const e of sketch.entities) {
    if (!e.line?.includes(start)) continue;
    const [a, b] = e.line.map(n => points[n]);
    const d = sub(b, a), n = [-d[1] / len(d), d[0] / len(d)];
    const c = sub(cursor, p), along = dot(c, n);
    if (len(c) === 0 || Math.abs(along) / len(c) < Math.cos(degrees * Math.PI / 180)) continue;
    return { at: [p[0] + along * n[0], p[1] + along * n[1]], tangent: e.id };
  }
  return null;
}
