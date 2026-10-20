// Pointer ray to handle parameter to document edits. Pure (plain [x, y, z] arrays, no three.js), so Node tests it.
// A handle is the C# contract (docs/playground-plan.md, "Handles come from C#"). Its parameter t is
//   linear  world units along the (unit) axis,
//   planar  [s, r]: world units along u and v of the plane whose normal is the axis,
//   angle   degrees about the axis, counter-clockwise looking down it,
// measured from where the drag started, so grabbing a handle off its anchor does not make it jump.
// Each edit sets base + per·t.

const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const scale = (a, k) => [a[0] * k, a[1] * k, a[2] * k];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const unit = a => scale(a, 1 / Math.hypot(...a));

/**
 * The handle's frame {n, u, v}: n along the axis; u the handle's own u when it has one, else world x (world y when x
 * runs nearly along n) made perpendicular to n; v = n × u, so a sketch plane's (x, y) read as (u, v).
 */
export function frameOf({ axis, u }) {
  const n = unit(axis);
  const along = u ?? (Math.abs(n[0]) < 0.9 ? [1, 0, 0] : [0, 1, 0]);
  const uu = unit(sub(along, scale(n, dot(along, n))));
  return { n, u: uu, v: cross(n, uu) };
}

/** Where the ray meets the plane through the anchor with normal n; null when it runs (nearly) along the plane. */
function meet({ origin, direction }, anchor, n) {
  const d = unit(direction), along = dot(d, n);
  if (Math.abs(along) < 1e-6) return null;
  return add(origin, scale(d, dot(sub(anchor, origin), n) / along));
}

/**
 * The pointer's absolute parameter: linear, s along the axis of the point on it closest to the ray; planar, [s, r] of
 * the ray's hit on the plane; angle, the degrees of that hit about the axis from u. null when the ray runs (nearly)
 * along the axis line (linear) or the plane (planar, angle), so the drag keeps its last value.
 */
export function paramAt(handle, ray) {
  const { n, u, v } = frameOf(handle);
  if (handle.kind === 'linear') {
    // The closest points of two lines: anchor + s·n and origin + q·d, both unit.
    const d = unit(ray.direction), w = sub(ray.origin, handle.anchor);
    const b = dot(n, d), denominator = 1 - b * b;
    if (denominator < 1e-6) return null;
    return (dot(w, n) - b * dot(w, d)) / denominator;
  }
  const p = meet(ray, handle.anchor, n);
  if (!p) return null;
  const r = sub(p, handle.anchor);
  if (handle.kind === 'planar') return [dot(r, u), dot(r, v)];
  return Math.atan2(dot(r, v), dot(r, u)) * 180 / Math.PI;
}

const perOf = edit => [edit.per].flat();
const times = (per, t) => typeof t === 'number' ? per[0] * t : per.reduce((sum, p, i) => sum + p * (t[i] ?? 0), 0);
const round = x => Math.round(x * 1e9) / 1e9; // so a snapped 100 + 1 does not come out as 100.99999999999999

/** The edit the handle's value, readout and min/max follow: the one whose base is the value, else the first. */
export const valueEdit = handle => handle.edits.find(e => e.base === handle.value) ?? handle.edits[0];

/** The handle's value at t (linear and angle; planar returns the value as given). */
export function valueAt(handle, t) {
  if (handle.kind === 'planar') return handle.value;
  const e = valueEdit(handle);
  return round(e.base + times(perOf(e), t));
}

/**
 * Snaps and clamps t: linear and angle snap their value to a multiple of step and clamp it to min/max; planar snaps
 * each coordinate to step (no bounds, a plane has no single value).
 */
export function constrain(handle, t) {
  const { step, min, max } = handle;
  if (handle.kind === 'planar') return step > 0 ? t.map(x => Math.round(x / step) * step) : t;
  const e = valueEdit(handle), p = perOf(e)[0];
  if (!p) return t;
  let value = e.base + p * t;
  if (step > 0) value = Math.round(value / step) * step;
  if (min != null) value = Math.max(value, min);
  if (max != null) value = Math.min(value, max);
  return (value - e.base) / p;
}

/** The document edits at t: [{set: path, value: base + per·t}]. */
export const editsAt = (handle, t) => handle.edits.map(e => ({ set: e.path, value: round(e.base + times(perOf(e), t)) }));

/** The anchor moved by t: along the axis or in the plane (an angle turns about it, so it stays). */
export function anchorAt(handle, t) {
  const { n, u, v } = frameOf(handle);
  if (handle.kind === 'linear') return add(handle.anchor, scale(n, t));
  if (handle.kind === 'planar') return add(handle.anchor, add(scale(u, t[0]), scale(v, t[1])));
  return handle.anchor;
}

/**
 * A drag that started with the pointer on this ray, or null when the ray gives no parameter. move(ray) gives
 * {t, value, edits} with t snapped and clamped; a ray with no parameter gives the last state again. An angle
 * accumulates past ±180°, so dragging round more than half a turn keeps going.
 */
export function startDrag(handle, ray) {
  const start = paramAt(handle, ray);
  if (start == null) return null;
  const at = t => ({ t, value: valueAt(handle, t), edits: editsAt(handle, t) });
  let previous = start, turned = 0, state = at(handle.kind === 'planar' ? [0, 0] : 0);
  return {
    move(ray) {
      const now = paramAt(handle, ray);
      if (now == null) return state;
      let raw;
      if (handle.kind === 'planar') raw = [now[0] - start[0], now[1] - start[1]];
      else if (handle.kind === 'angle') {
        turned += ((now - previous + 540) % 360) - 180; // the shortest way round since the last move
        previous = now;
        raw = turned;
      } else raw = now - start;
      return state = at(constrain(handle, raw));
    },
  };
}

/** The feature a face belongs to: its name up to the last '/' ("plate/x+" → "plate"). */
export const featureOf = name => name.slice(0, name.lastIndexOf('/'));

/**
 * The handles that show for a selection: those of the first selected face's feature. Assembly frames are not
 * feature handles (TransformControls, later), so they never show here.
 */
export function shownHandles(handles, selection) {
  if (!selection.length) return [];
  const feature = featureOf(selection[0]);
  return handles.filter(h => h.kind !== 'frame' && h.feature === feature);
}
