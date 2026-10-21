// The sketch op's regions as shapes on screen, and which one a click is in. Pure, so it is tested in Node.
// A region is {outer: [entity ids], holes: [[entity ids]]} (src/Isoform.Interactive SketchJson); a loop's entities
// are chained where their ends meet, arcs and circles sampled, so a point-in-polygon test answers a click.

const close = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]) < 1e-6;

/** An arc's or circle's points from start to end angle (counter-clockwise), steps apart at most. */
function sample(c, r, a0, a1, step = Math.PI / 32) {
  const n = Math.max(2, Math.ceil((a1 - a0) / step));
  return Array.from({ length: n + 1 }, (_, i) => [c[0] + r * Math.cos(a0 + (a1 - a0) * i / n), c[1] + r * Math.sin(a0 + (a1 - a0) * i / n)]);
}

/** An entity as a polyline from one end to the other. */
export function polylineOf(e, points, radii = {}) {
  if (e.line) return e.line.map(n => points[n]);
  if (e.circle) return sample(points[e.circle.center], radii[e.id] ?? e.circle.radius, 0, 2 * Math.PI);
  const c = points[e.arc.center], f = points[e.arc.from], t = points[e.arc.to];
  const a0 = Math.atan2(f[1] - c[1], f[0] - c[0]);
  let a1 = Math.atan2(t[1] - c[1], t[0] - c[0]);
  while (a1 <= a0 + 1e-12) a1 += 2 * Math.PI;
  return sample(c, Math.hypot(f[0] - c[0], f[1] - c[1]), a0, a1);
}

/** A loop of entities as one closed polygon: each entity's polyline, turned round where it runs the other way. */
export function loopPolygon(ids, sketch, points, radii = {}) {
  const left = ids.map(id => polylineOf(sketch.entities.find(e => e.id === id), points, radii));
  const polygon = [...left.shift()];
  while (left.length) {
    const end = polygon.at(-1);
    let i = left.findIndex(p => close(p[0], end));
    if (i < 0) {
      i = left.findIndex(p => close(p.at(-1), end));
      if (i < 0) break; // the kernel made the loop; a gap here can only be rounding
      left[i].reverse();
    }
    polygon.push(...left.splice(i, 1)[0].slice(1));
  }
  return polygon;
}

/** Whether p lies inside the polygon (even-odd). */
export function inside(polygon, p) {
  let odd = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const [a, b] = [polygon[i], polygon[j]];
    if ((a[1] > p[1]) !== (b[1] > p[1]) && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]) odd = !odd;
  }
  return odd;
}

const area = polygon => Math.abs(polygon.reduce((s, a, i) => {
  const b = polygon[(i + 1) % polygon.length];
  return s + a[0] * b[1] - b[0] * a[1];
}, 0)) / 2;

/** Each region's polygons: {outer, holes, key}. key names the region by its outer entities, as the document does. */
export function shapes(regions, sketch, points, radii = {}) {
  return regions.map(r => ({
    outer: loopPolygon(r.outer, sketch, points, radii),
    holes: r.holes.map(h => loopPolygon(h, sketch, points, radii)),
    key: regionKey(r.outer),
  }));
}

export const regionKey = outer => [...outer].sort().join(',');

/** The index of the region a point is in (inside its outer loop, outside its holes; the smallest if several), or -1. */
export function regionAt(shaped, p) {
  let best = -1;
  shaped.forEach((s, i) => {
    if (inside(s.outer, p) && !s.holes.some(h => inside(h, p)) && (best < 0 || area(s.outer) < area(shaped[best].outer))) best = i;
  });
  return best;
}
