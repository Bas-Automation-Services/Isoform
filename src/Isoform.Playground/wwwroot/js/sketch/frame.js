// The sketch plane in the world and on screen. Pure, so it is tested in Node.
//
// A frame is {origin, x, y, normal} (world vectors; the faceFrame op answers one): sketch [u, v] is the world point
// origin + u·x + v·y. A view is {center: [u, v], scale (px per mm), width, height}: the screen (CSS px from the
// view's top left) shows the plane with x to the right and y up.

const add = (a, b) => a.map((v, i) => v + b[i]);
const scale = (a, s) => a.map(v => v * s);
const dot = (a, b) => a.reduce((s, v, i) => s + v * b[i], 0);
const crossV = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const unit = a => scale(a, 1 / Math.hypot(...a));

/** A sketch plane of the format ({origin, x, normal}) as a frame: x made square to the normal, y = normal × x. */
export function frameOfPlane({ origin, x, normal }) {
  const n = unit(normal);
  const ux = unit(add(x, scale(n, -dot(x, n))));
  return { origin, x: ux, y: crossV(n, ux), normal: n };
}

export const toWorld = (f, [u, v]) => add(f.origin, add(scale(f.x, u), scale(f.y, v)));

export function toSketch(f, p) {
  const d = p.map((v, i) => v - f.origin[i]);
  return [dot(d, f.x), dot(d, f.y)];
}

export const toScreen = (view, [u, v]) => [view.width / 2 + (u - view.center[0]) * view.scale, view.height / 2 - (v - view.center[1]) * view.scale];
export const fromScreen = (view, [x, y]) => [view.center[0] + (x - view.width / 2) / view.scale, view.center[1] - (y - view.height / 2) / view.scale];

/** The view that shows a box [[u0, v0], [u1, v1]] whole, with a margin round it. */
export function fit([[u0, v0], [u1, v1]], width, height, margin = 0.15) {
  const w = Math.max(u1 - u0, 1e-3), h = Math.max(v1 - v0, 1e-3);
  return { center: [(u0 + u1) / 2, (v0 + v1) / 2], scale: Math.min(width / w, height / h) * (1 - 2 * margin), width, height };
}

/** The view zoomed by factor about a screen point, which stays over the same sketch point. */
export function zoom(view, factor, at) {
  const p = fromScreen(view, at);
  const s = view.scale * factor;
  return { ...view, scale: s, center: [p[0] - (at[0] - view.width / 2) / s, p[1] + (at[1] - view.height / 2) / s] };
}

/** A box round points [u, v]. */
export function bounds(points) {
  const us = points.map(p => p[0]), vs = points.map(p => p[1]);
  return [[Math.min(...us), Math.min(...vs)], [Math.max(...us), Math.max(...vs)]];
}

/** The grid step for a scale: a 1, 2 or 5 times a power of ten that puts grid lines at least `px` apart. */
export function gridStep(scale, px = 12) {
  const raw = px / scale, ten = 10 ** Math.floor(Math.log10(raw));
  return [1, 2, 5, 10].map(k => k * ten).find(s => s >= raw);
}
