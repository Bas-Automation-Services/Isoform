// Dimensions: which ones fit what was clicked, what they measure now, the values typed for them, and where their
// labels go. Pure, so it is tested in Node. Kinds and refs are the format's (src/Isoform.Document Features.cs:
// SketchConstraint): length line; distance [p, q], [p, line] or [line, line]; horizontal-/vertical-distance [p, q];
// radius and diameter an arc or circle; angle [line, line], degrees, the first line turned by it runs along the second.
import { kindOf, refsOf } from './model.js';

const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
const len = a => Math.hypot(a[0], a[1]);
const cross = (a, b) => a[0] * b[1] - a[1] * b[0];

function lineOf(sketch, points, id) {
  const e = sketch.entities.find(x => x.id === id);
  return e?.line ? e.line.map(n => points[n]) : null;
}

function radiusOf(sketch, points, radii, id) {
  const e = sketch.entities.find(x => x.id === id);
  if (e.circle) return radii[id] ?? e.circle.radius;
  return len(sub(points[e.arc.from], points[e.arc.center]));
}

// The distance from p to the line through a and b.
const toLine = (p, [a, b]) => Math.abs(cross(sub(b, a), sub(p, a))) / len(sub(b, a));

/** The signed angle in degrees (−180, 180] that turns line r0 onto the direction of line r1. */
function angleBetween(l0, l1) {
  const u = sub(l0[1], l0[0]), w = sub(l1[1], l1[0]);
  return Math.atan2(cross(u, w), u[0] * w[0] + u[1] * w[1]) * 180 / Math.PI;
}

/** What a dimension of this kind on these refs measures in the solved sketch. */
export function measure(kind, refs, { sketch, points, radii = {} }) {
  const isPoint = r => r in points && !sketch.entities.some(e => e.id === r);
  switch (kind) {
    case 'length': { const [a, b] = lineOf(sketch, points, refs[0]); return len(sub(b, a)); }
    case 'horizontal-distance': return Math.abs(points[refs[1]][0] - points[refs[0]][0]);
    case 'vertical-distance': return Math.abs(points[refs[1]][1] - points[refs[0]][1]);
    case 'radius': return radiusOf(sketch, points, radii, refs[0]);
    case 'diameter': return 2 * radiusOf(sketch, points, radii, refs[0]);
    case 'angle': return angleBetween(lineOf(sketch, points, refs[0]), lineOf(sketch, points, refs[1]));
    case 'distance':
      if (isPoint(refs[0]) && isPoint(refs[1])) return len(sub(points[refs[1]], points[refs[0]]));
      if (isPoint(refs[0])) return toLine(points[refs[0]], lineOf(sketch, points, refs[1]));
      if (isPoint(refs[1])) return toLine(points[refs[1]], lineOf(sketch, points, refs[0]));
      return toLine(lineOf(sketch, points, refs[1])[0], lineOf(sketch, points, refs[0]));
  }
  return NaN;
}

/**
 * The dimensions that fit the picks (point names and entity ids, in click order), the most likely first:
 * [{kind, refs, value}] with value what it measures now. A line's ends give its length or their horizontal and
 * vertical spans; two lines a distance when parallel, else an angle.
 */
export function options(picks, solved) {
  const { sketch, points } = solved;
  const entity = id => sketch.entities.find(e => e.id === id);
  const [a, b] = picks;
  const with_ = (kind, refs) => ({ kind, refs, value: measure(kind, refs, solved) });
  if (picks.length === 1) {
    const e = entity(a);
    if (!e) return [];
    if (e.line) return [with_('length', [a]), with_('horizontal-distance', e.line), with_('vertical-distance', e.line)];
    return e.circle ? [with_('diameter', [a]), with_('radius', [a])] : [with_('radius', [a]), with_('diameter', [a])];
  }
  if (picks.length !== 2) return [];
  const [ea, eb] = [entity(a), entity(b)];
  if (!ea && !eb) return [with_('distance', [a, b]), with_('horizontal-distance', [a, b]), with_('vertical-distance', [a, b])];
  if (!ea && eb?.line) return [with_('distance', [a, b])];
  if (ea?.line && !eb) return [with_('distance', [b, a])];
  if (ea?.line && eb?.line) {
    const parallel = Math.abs(cross(...[ea, eb].map(e => { const [p, q] = e.line.map(n => points[n]); const d = sub(q, p); return [d[0] / len(d), d[1] / len(d)]; }))) < 1e-6;
    return parallel ? [with_('distance', [a, b])] : [with_('angle', [a, b])];
  }
  return [];
}

/**
 * A typed value: a number (above 0 except an angle), or "=expression" kept as text for the document's
 * expressions. An angle takes the sign the lines have now, since the format measures it one way round. null when it
 * does not read.
 */
export function parseValue(text, kind, now = 0) {
  const t = text.trim();
  if (t.startsWith('=')) return t.length > 1 ? t : null;
  const v = Number(t);
  if (t === '' || !Number.isFinite(v)) return null;
  if (kind === 'angle') return now < 0 ? -Math.abs(v) : Math.abs(v);
  return v > 0 || (kind === 'distance' && v === 0) ? v : null;
}

/** A value as the input shows it: an expression as written, a number rounded to what can be typed. */
export const showValue = (value, kind) => typeof value === 'string' ? value : String(+(kind === 'angle' ? Math.abs(value) : value).toFixed(3));

/**
 * The dimension as drawn: its text, where the text sits ([u, v]) and the lines from the geometry to it ([[a, b]]),
 * `off` mm clear of the geometry, on the side away from `away` (the sketch's middle, say) when given.
 */
export function label(c, { sketch, points, radii = {} }, off, away = null) {
  const kind = kindOf(c), refs = refsOf(c);
  const text = `${{ radius: 'R', diameter: '⌀' }[kind] ?? ''}${typeof c.value === 'string' ? c.value : showValue(c.value, kind)}${kind === 'angle' ? '°' : ''}`;
  const mid = (p, q) => [(p[0] + q[0]) / 2, (p[1] + q[1]) / 2];
  const isPoint = r => r in points && !sketch.entities.some(e => e.id === r);
  // Between two points: both lifted off the geometry, a line across, the text in its middle.
  const across = (p, q, lift) => {
    const [p1, q1] = [lift(p), lift(q)];
    return { text, at: mid(p1, q1), lines: [[p, p1], [q, q1], [p1, q1]] };
  };
  switch (kind) {
    case 'length': case 'distance': {
      if (kind === 'distance' && !(isPoint(refs[0]) && isPoint(refs[1]))) {
        // To a line: from the point (or the second line's middle) square onto the first line.
        const [line, from] = isPoint(refs[0]) ? [refs[1], points[refs[0]]] : isPoint(refs[1]) ? [refs[0], points[refs[1]]] : [refs[0], mid(...lineOf(sketch, points, refs[1]))];
        const [a, b] = lineOf(sketch, points, line), d = sub(b, a);
        const t = ((from[0] - a[0]) * d[0] + (from[1] - a[1]) * d[1]) / (d[0] * d[0] + d[1] * d[1]);
        const foot = [a[0] + t * d[0], a[1] + t * d[1]];
        return { text, at: mid(from, foot), lines: [[from, foot]] };
      }
      const [p, q] = kind === 'length' ? lineOf(sketch, points, refs[0]) : refs.map(r => points[r]);
      const d = sub(q, p), l = len(d) || 1;
      let n = [-d[1] / l * off, d[0] / l * off];
      if (away && n[0] * (p[0] + q[0] - 2 * away[0]) + n[1] * (p[1] + q[1] - 2 * away[1]) < 0) n = [-n[0], -n[1]];
      return across(p, q, x => [x[0] + n[0], x[1] + n[1]]);
    }
    case 'horizontal-distance': case 'vertical-distance': {
      // Above (or right of) both points, or below (left of) both when the middle of the sketch is above.
      const [p, q] = refs.map(r => points[r]), k = kind === 'horizontal-distance' ? 1 : 0;
      const below = away && (p[k] + q[k]) / 2 < away[k];
      const level = below ? Math.min(p[k], q[k]) - off : Math.max(p[k], q[k]) + off;
      return across(p, q, x => k ? [x[0], level] : [level, x[1]]);
    }
    case 'radius': case 'diameter': {
      const e = sketch.entities.find(x => x.id === refs[0]);
      const centre = points[(e.arc ?? e.circle).center], r = radiusOf(sketch, points, radii, refs[0]);
      // At 45°, or for an arc halfway round it.
      let a = Math.PI / 4;
      if (e.arc) {
        const [f, t] = [points[e.arc.from], points[e.arc.to]].map(p => Math.atan2(p[1] - centre[1], p[0] - centre[0]));
        a = f + ((t - f) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) / 2;
      }
      const at = [centre[0] + (r + off) * Math.cos(a), centre[1] + (r + off) * Math.sin(a)];
      const from = kind === 'radius' ? centre : [centre[0] - r * Math.cos(a), centre[1] - r * Math.sin(a)];
      return { text, at, lines: [[from, at]] };
    }
    default: { // angle
      const [l0, l1] = refs.map(r => lineOf(sketch, points, r));
      return { text, at: mid(mid(...l0), mid(...l1)), lines: [] };
    }
  }
}

/** The indices of the sketch's dimensions. */
export const dimensionIndices = sketch => sketch.constraints.flatMap((c, i) => 'value' in c ? [i] : []);
