// Fillet and trim in sketch mode (docs/kernel-gaps-plan.md §4): the corners a fillet can round, the arc it would put
// there, the piece of an entity a trim would delete, and the edit itself, which the kernel makes (the sketchEdit op,
// src/Isoform.Interactive SketchJson.Edit). What is drawn here is a preview; the kernel's answer is what the sketch
// becomes. Pure but for the call it is given, so it is tested in Node.
//
//   edit: {op: 'fillet', corner, radius} | {op: 'trim', entity, x, y} (x, y in sketch coordinates)
//   reply: the sketch op's solve of the edited document plus added (ids) and dropped (constraints, as the kernel words
//   them), or {error} when the edit cannot be made.
import { settledSketch, workingDocument } from './model.js';

const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
const add = (a, b) => [a[0] + b[0], a[1] + b[1]];
const scale = (a, k) => [a[0] * k, a[1] * k];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1];
const cross = (a, b) => a[0] * b[1] - a[1] * b[0];
const len = a => Math.hypot(a[0], a[1]);
const dist = (a, b) => len(sub(a, b));
const TAU = 2 * Math.PI;
const angleOf = (p, c) => (Math.atan2(p[1] - c[1], p[0] - c[0]) + TAU) % TAU;
const round = (c, r, a) => [c[0] + r * Math.cos(a), c[1] + r * Math.sin(a)];

/** Points round c from angle a0 through sweep (either way), steps apart at most. */
function sample(c, r, a0, sweep, step = Math.PI / 32) {
  const n = Math.max(2, Math.ceil(Math.abs(sweep) / step));
  return Array.from({ length: n + 1 }, (_, i) => round(c, r, a0 + sweep * i / n));
}

// --- Fillet ----------------------------------------------------------------------------------------------------------

/** The points where exactly two lines or arcs end: the corners a fillet can round. */
export function corners(sketch) {
  const ends = new Map();
  for (const e of sketch.entities)
    for (const p of e.line ?? (e.arc ? [e.arc.from, e.arc.to] : [])) ends.set(p, (ends.get(p) ?? 0) + 1);
  return [...ends].filter(([, n]) => n === 2).map(([p]) => p);
}

/** The corner nearest at within tol, or null. */
export function cornerAt(sketch, points, at, tol) {
  let best = null, near = tol;
  for (const p of corners(sketch)) if (dist(points[p], at) <= near) [best, near] = [p, dist(points[p], at)];
  return best;
}

// The entities ending at a corner, each with the direction away from it along the entity and its size (a line's
// length, an arc's radius).
function legs(sketch, points, corner) {
  const c = points[corner];
  return sketch.entities.filter(e => e.line?.includes(corner) || e.arc?.from === corner || e.arc?.to === corner).map(e => {
    if (e.line) {
      const d = sub(points[e.line[0] === corner ? e.line[1] : e.line[0]], c);
      return { e, u: scale(d, 1 / len(d)), size: len(d) };
    }
    return { e, size: dist(points[e.arc.center], c) };
  });
}

/** A first radius for a corner: a fifth of its shortest line or arc radius, to two figures. */
export function filletRadius(sketch, points, corner) {
  return +(Math.min(...legs(sketch, points, corner).map(l => l.size)) / 5).toPrecision(2);
}

/**
 * The arc a fillet of radius r would put at a corner of two lines, as a polyline from one tangent point to the other,
 * or null: a corner with an arc (the kernel's answer shows it), a straight or folded corner, or a radius whose tangent
 * points would run off a line.
 */
export function filletArc(sketch, points, corner, r) {
  const [a, b, more] = legs(sketch, points, corner);
  if (!a?.u || !b?.u || more || !(r > 0)) return null;
  const half = Math.acos(Math.max(-1, Math.min(1, dot(a.u, b.u)))) / 2;
  if (half < 1e-6 || Math.PI / 2 - half < 1e-6) return null;
  const back = r / Math.tan(half);
  if (back > a.size || back > b.size) return null;
  const c = points[corner], bisector = add(a.u, b.u);
  const centre = add(c, scale(bisector, r / Math.sin(half) / len(bisector)));
  const [from, to] = [a, b].map(l => add(c, scale(l.u, back)));
  const a0 = Math.atan2(from[1] - centre[1], from[0] - centre[0]), a1 = Math.atan2(to[1] - centre[1], to[0] - centre[0]);
  return sample(centre, r, a0, ((a1 - a0 + 3 * Math.PI) % TAU) - Math.PI);
}

// --- Trim ------------------------------------------------------------------------------------------------------------

// An entity as a curve: a segment a → b, or a circle round c of radius r, from angle a0 counter-clockwise through span
// (a whole circle is closed).
function curveOf(e, points, radii) {
  if (e.line) return { a: points[e.line[0]], b: points[e.line[1]] };
  const c = points[e.arc?.center ?? e.circle.center];
  if (e.circle) return { c, r: radii[e.id] ?? e.circle.radius, a0: 0, span: TAU, closed: true };
  const a0 = angleOf(points[e.arc.from], c);
  return { c, r: dist(points[e.arc.from], c), a0, span: (angleOf(points[e.arc.to], c) - a0 + TAU) % TAU || TAU };
}

// Where along a curve a point is: t from a to b on a segment, the angle past a0 round a circle.
const along = (k, p) => k.a ? dot(sub(p, k.a), sub(k.b, k.a)) / dot(sub(k.b, k.a), sub(k.b, k.a)) : (angleOf(p, k.c) - k.a0 + TAU) % TAU;
const extent = k => k.a ? 1 : k.span;
const EPS = 1e-9;
const onCurve = (k, p) => k.closed || (t => t >= -EPS && t <= extent(k) + EPS)(along(k, p));

// Where two curves' lines and circles cross, before limiting them to the curves.
function crossings(k, m) {
  if (k.a && m.a) {
    const d = sub(k.b, k.a), e = sub(m.b, m.a), den = cross(d, e);
    return Math.abs(den) < 1e-12 ? [] : [add(k.a, scale(d, cross(sub(m.a, k.a), e) / den))];
  }
  if (k.a || m.a) {
    const [l, o] = k.a ? [k, m] : [m, k], d = sub(l.b, l.a), f = sub(l.a, o.c);
    const A = dot(d, d), B = 2 * dot(f, d), disc = B * B - 4 * A * (dot(f, f) - o.r * o.r);
    if (disc < 0) return [];
    return [-1, 1].map(s => add(l.a, scale(d, (-B + s * Math.sqrt(disc)) / (2 * A))));
  }
  const d = dist(k.c, m.c);
  if (d < 1e-12 || d > k.r + m.r || d < Math.abs(k.r - m.r)) return [];
  const x = (k.r * k.r - m.r * m.r + d * d) / (2 * d), h = Math.sqrt(Math.max(0, k.r * k.r - x * x));
  const u = scale(sub(m.c, k.c), 1 / d), p = add(k.c, scale(u, x));
  return [add(p, scale([-u[1], u[0]], h)), add(p, scale([u[1], -u[0]], h))];
}

/**
 * The piece of entity id a trim at `at` would delete: the stretch between its crossings with the other entities
 * (or its ends) that `at` is nearest, as {from, to} along it and the polyline to draw. Construction geometry neither
 * cuts nor is cut (SketchIntersections); null where the kernel would refuse: construction, nothing crossing, a circle
 * crossed once.
 */
export function trimPiece(sketch, points, radii, id, at) {
  const entity = sketch.entities.find(e => e.id === id);
  if (entity.construction) return null;
  const k = curveOf(entity, points, radii), end = extent(k);
  const cuts = sketch.entities.filter(e => e.id !== id && !e.construction).flatMap(e => {
    const m = curveOf(e, points, radii);
    return crossings(k, m).filter(p => onCurve(k, p) && onCurve(m, p)).map(p => along(k, p));
  }).filter(t => t > EPS && t < end - EPS).sort((x, y) => x - y).filter((t, i, all) => i === 0 || t - all[i - 1] > EPS);
  if (cuts.length < (k.closed ? 2 : 1)) return null;
  let s = along(k, at);
  // Off a segment's ends, or off an arc's sweep: the nearer end.
  if (!k.closed) s = k.a ? Math.max(0, Math.min(1, s)) : s <= end ? s : s - end < TAU - s ? end : 0;
  let from, to;
  if (k.closed) {
    const i = cuts.findIndex(t => t > s);
    [from, to] = i <= 0 ? [cuts.at(-1), cuts[0] + TAU] : [cuts[i - 1], cuts[i]];
  } else {
    const bounds = [0, ...cuts, end], i = bounds.findIndex(t => t >= s);
    [from, to] = [bounds[Math.max(0, i - 1)], bounds[Math.max(1, i)]];
  }
  const polyline = k.a ? [from, to].map(t => add(k.a, scale(sub(k.b, k.a), t))) : sample(k.c, k.r, k.a0 + from, to - from);
  return { from, to, polyline };
}

// --- The edit --------------------------------------------------------------------------------------------------------

export const filletEdit = (corner, radius) => ({ op: 'fillet', corner, radius });
export const trimEdit = (entity, at) => ({ op: 'trim', entity, x: at[0], y: at[1] });
const named = edit => edit.op === 'fillet' ? `fillet ${edit.corner} R${+edit.radius.toFixed(3)}`
  : edit.op === 'project' ? `project ${edit.faces.join(' | ')}${edit.linked ? ' (linked)' : ''}` : `trim ${edit.entity}`;

/**
 * What an edit's reply does to the sketch mode's state {sketch, undo}: the sketch the kernel settled, with the one it
 * replaces as one undo step, and a note for the panel naming the edit and every constraint it dropped. A refusal keeps
 * both and the note says why. reply is null when refused.
 */
export function afterEdit({ sketch, undo }, edit, reply) {
  if (reply.error) return { sketch, undo, reply: null, note: { text: `${named(edit)}: ${reply.error}`, level: 'error', items: [] } };
  const dropped = reply.dropped ?? [];
  return {
    sketch: settledSketch(reply.settled.text, sketch.id), undo: [...undo, sketch], reply,
    note: dropped.length
      ? { text: `${named(edit)} dropped ${dropped.length} constraint${dropped.length > 1 ? 's' : ''}:`, level: 'warn', items: dropped }
      : { text: named(edit), level: 'ok', items: [] },
  };
}

/** Runs an edit through the kernel (call is the kernel client's) on the sketch being drawn: afterEdit of its reply. */
export async function runEdit(call, { base, sketch, undo }, edit) {
  const reply = await call('sketchEdit', { document: workingDocument(base, sketch), id: sketch.id, edit })
    .then(r => r.result, e => ({ error: e.message }));
  return afterEdit({ sketch, undo }, edit, reply);
}
