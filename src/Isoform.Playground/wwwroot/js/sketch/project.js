// Projecting part edges into a sketch (docs/kernel-gaps-plan.md §4 item 2): the part's sharp edges seen in the sketch
// plane, each named by its two faces, the one under the pointer, and the edit, which the kernel makes exact (the
// sketchEdit op's project, src/Isoform.Modeling SketchReferences). Pure, so it is tested in Node.
//
//   edges: [{faces: [a, b], segments: [[[u, v], [u, v]], …], world: [[[x, y, z], [x, y, z]], …], heights: [h, …]}],
//   one per face pair, h a segment's middle above the plane (towards the viewer)
//   edit: {op: 'project', faces: [a, b], near: [x, y, z], linked}
import { toSketch } from './frame.js';

/** The part's sharp edges in sketch coordinates, grouped by the faces they separate (by each face's first name). */
export function edgesOf(result, frame) {
  const { sharp, sharpEdges } = result.mesh, byPair = new Map();
  const height = p => p.reduce((s, v, i) => s + (v - frame.origin[i]) * frame.normal[i], 0);
  for (let k = 0; k < sharpEdges.length; k++) {
    const pair = sharpEdges[k];
    if (!byPair.has(pair)) byPair.set(pair, { faces: result.lineFaces[pair].map(f => result.faces[f][0]), segments: [], world: [], heights: [] });
    const e = byPair.get(pair), a = [...sharp.subarray(6 * k, 6 * k + 3)], b = [...sharp.subarray(6 * k + 3, 6 * k + 6)];
    e.world.push([a, b]);
    e.segments.push([toSketch(frame, a), toSketch(frame, b)]);
    e.heights.push(height(a.map((v, i) => (v + b[i]) / 2)));
  }
  return [...byPair.values()];
}

/**
 * The edge nearest the sketch point `at`, within tol (sketch units), and the world point on it under `at`: {edge,
 * near}; null when none is that close. Of edges that look the same from above (a plate's top and bottom edge), the
 * one nearest the viewer.
 */
export function edgeAt(edges, at, tol) {
  let best = null, near = tol, top = -Infinity;
  const same = tol / 100;
  for (const edge of edges)
    edge.segments.forEach(([a, b], i) => {
      const d = [b[0] - a[0], b[1] - a[1]], l2 = d[0] * d[0] + d[1] * d[1];
      const t = l2 ? Math.max(0, Math.min(1, ((at[0] - a[0]) * d[0] + (at[1] - a[1]) * d[1]) / l2)) : 0;
      const dist = Math.hypot(at[0] - a[0] - t * d[0], at[1] - a[1] - t * d[1]);
      if (dist >= near + same || dist > near - same && edge.heights[i] <= top) return;
      const [p, q] = edge.world[i];
      [best, near, top] = [{ edge, near: p.map((v, k) => v + t * (q[k] - v)) }, Math.min(dist, near), edge.heights[i]];
    });
  return best;
}

export const projectEdit = (faces, near, linked) => ({ op: 'project', faces, near, linked });
