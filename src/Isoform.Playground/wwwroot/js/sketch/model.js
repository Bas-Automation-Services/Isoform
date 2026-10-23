// The sketch being drawn, as the document stores it (src/Isoform.Document CanonicalFormat: a `sketch` feature), and
// what the tools add to it. Pure, so it is tested in Node. Every function takes a sketch and returns a new one.
//
//   {id, type: 'sketch', on: {face} | {plane}, points: {name: [u, v]}, entities: [{id, line: [p, q]} |
//    {id, arc: {center, from, to}} | {id, circle: {center, radius}}, construction?], constraints: [{kind: ref | refs, value?}]}
//
// A pick is where a click lands once inference has had its say (sketch/infer.js): {at: [u, v]}, plus point: an
// existing point to reuse (that is how the format says coincident), or constraints the new point gets: {kind: 'on' |
// 'midpoint', entity} or {kind: 'fix'}.

/** A constraint's kind: its one key besides value. */
export const kindOf = c => Object.keys(c).find(k => k !== 'value');
/** A constraint's points and entities, always as a list. */
export const refsOf = c => [c[kindOf(c)]].flat();
/** A constraint as the format writes it: one ref as a string, several as a list. */
export function constraint(kind, refs, value) {
  const c = { [kind]: refs.length === 1 ? refs[0] : refs };
  if (value !== undefined) c.value = value;
  return c;
}

export const DIMENSIONS = ['length', 'distance', 'horizontal-distance', 'vertical-distance', 'radius', 'diameter', 'angle'];
export const isDimension = c => DIMENSIONS.includes(kindOf(c));

/** The points an entity is drawn between. */
export function pointsOf(e) {
  if (e.line) return e.line;
  if (e.arc) return [e.arc.center, e.arc.from, e.arc.to];
  return [e.circle.center];
}
export const shapeOf = e => e.line ? 'line' : e.arc ? 'arc' : 'circle';

export const newSketch = (id, on) => ({ id, type: 'sketch', on, points: {}, entities: [], constraints: [] });

const copy = s => structuredClone(s);
const taken = s => new Set([...Object.keys(s.points), ...s.entities.map(e => e.id)]);

/** The first free name prefix1, prefix2, … among the sketch's points and entities. */
export function freshId(sketch, prefix, also = new Set()) {
  const used = taken(sketch);
  for (let n = 1; ; n++) if (!used.has(prefix + n) && !also.has(prefix + n)) return prefix + n;
}

/** The first free feature id base1, base2, … in a document. */
export function freshFeatureId(doc, base) {
  const used = new Set((doc?.features ?? []).map(f => f.id));
  for (let n = 1; ; n++) if (!used.has(base + n)) return base + n;
}

// Adds a pick's point to s (mutating it) and returns its name.
function place(s, pick) {
  if (pick.point) return pick.point;
  const name = freshId(s, 'p');
  s.points[name] = [...pick.at];
  for (const c of pick.constraints ?? [])
    s.constraints.push(c.kind === 'fix' ? constraint('fix', [name]) : constraint(c.kind, [name, c.entity]));
  return name;
}

function entity(s, shape, opts) {
  const e = { id: freshId(s, 'e'), ...shape };
  if (opts.construction) e.construction = true;
  s.entities.push(e);
  return e.id;
}

/** A point on its own. */
export function addPoint(sketch, pick) {
  const s = copy(sketch);
  const point = place(s, pick);
  return { sketch: s, point };
}

/**
 * A line between two picks; hv adds horizontal or vertical, tangent (an arc's id) a tangent. end is the end point's
 * name, for a polyline to carry on from.
 */
export function addLine(sketch, from, to, { construction = false, hv = null, tangent = null } = {}) {
  const s = copy(sketch);
  const a = place(s, from), b = place(s, to);
  const id = entity(s, { line: [a, b] }, { construction });
  if (hv) s.constraints.push(constraint(hv, [id]));
  if (tangent) s.constraints.push(constraint('tangent', [id, tangent]));
  return { sketch: s, entity: id, end: b };
}

/** A rectangle from corner to opposite corner: four lines round shared corners, two horizontal and two vertical. */
export function addRectangle(sketch, a, b, { construction = false } = {}) {
  const s = copy(sketch);
  // Named round the rectangle: p1 at the first corner, p3 at the opposite one.
  const p1 = place(s, a);
  const [u1, v1] = s.points[p1], [u2, v2] = b.point ? s.points[b.point] : b.at;
  const p2 = place(s, { at: [u2, v1] }), p3 = place(s, b), p4 = place(s, { at: [u1, v2] });
  const sides = [[p1, p2, 'horizontal'], [p2, p3, 'vertical'], [p3, p4, 'horizontal'], [p4, p1, 'vertical']]
    .map(([p, q, hv]) => {
      const id = entity(s, { line: [p, q] }, { construction });
      s.constraints.push(constraint(hv, [id]));
      return id;
    });
  return { sketch: s, entities: sides };
}

/** A circle round a centre pick through a radius pick; a radius pick on a point puts the circle through that point. */
export function addCircle(sketch, centre, edge, { construction = false } = {}) {
  const s = copy(sketch);
  const c = place(s, centre);
  const at = edge.point ? s.points[edge.point] : edge.at;
  const radius = Math.hypot(at[0] - s.points[c][0], at[1] - s.points[c][1]);
  const id = entity(s, { circle: { center: c, radius } }, { construction });
  if (edge.point) s.constraints.push(constraint('on', [edge.point, id]));
  return { sketch: s, entity: id };
}

/**
 * An arc round a centre from a start pick to an end pick, counter-clockwise unless ccw is false (then the ends swap,
 * the format's arcs run counter-clockwise). A new end point lands on the start's radius. tangent: a line's id the
 * arc starts tangent to.
 */
export function addArc(sketch, centre, start, end, { construction = false, ccw = true, tangent = null } = {}) {
  const s = copy(sketch);
  const c = place(s, centre);
  const from = place(s, start);
  const [cu, cv] = s.points[c], [fu, fv] = s.points[from];
  const r = Math.hypot(fu - cu, fv - cv);
  const along = end.point ? null : Math.atan2(end.at[1] - cv, end.at[0] - cu);
  const to = place(s, along === null ? end : { ...end, at: [cu + r * Math.cos(along), cv + r * Math.sin(along)] });
  const id = entity(s, { arc: ccw ? { center: c, from, to } : { center: c, from: to, to: from } }, { construction });
  if (tangent) s.constraints.push(constraint('tangent', [tangent, id]));
  return { sketch: s, entity: id, end: to };
}

/**
 * A slot round the segment between two centre picks, radius wide: two lines and two half-circle arcs, each line
 * tangent to both arcs, the arcs of equal radius.
 */
export function addSlot(sketch, a, b, radius, { construction = false } = {}) {
  const s = copy(sketch);
  const ca = place(s, a), cb = place(s, b);
  const [au, av] = s.points[ca], [bu, bv] = s.points[cb];
  const len = Math.hypot(bu - au, bv - av) || 1;
  const [nu, nv] = [-(bv - av) / len * radius, (bu - au) / len * radius]; // left of a → b
  const a1 = place(s, { at: [au + nu, av + nv] }), a2 = place(s, { at: [au - nu, av - nv] });
  const b1 = place(s, { at: [bu + nu, bv + nv] }), b2 = place(s, { at: [bu - nu, bv - nv] });
  const bottom = entity(s, { line: [a2, b2] }, { construction });
  const capB = entity(s, { arc: { center: cb, from: b2, to: b1 } }, { construction });
  const top = entity(s, { line: [b1, a1] }, { construction });
  const capA = entity(s, { arc: { center: ca, from: a1, to: a2 } }, { construction });
  s.constraints.push(constraint('tangent', [bottom, capB]), constraint('tangent', [top, capB]),
    constraint('tangent', [top, capA]), constraint('tangent', [bottom, capA]), constraint('equal', [capA, capB]));
  return { sketch: s, entities: [bottom, capB, top, capA] };
}

/** Adds a constraint, unless the same one is there already. */
export function addConstraint(sketch, c) {
  const key = JSON.stringify(c);
  if (sketch.constraints.some(x => JSON.stringify(x) === key)) return sketch;
  return { ...sketch, constraints: [...sketch.constraints, c] };
}

export const removeConstraint = (sketch, index) => ({ ...sketch, constraints: sketch.constraints.filter((_, i) => i !== index) });

export function setValue(sketch, index, value) {
  const s = copy(sketch);
  s.constraints[index].value = value;
  return s;
}

/**
 * Removes points and entities by name: an entity takes its points with it where nothing else uses them, a point the
 * entities on it; constraints that name anything gone go too.
 */
export function remove(sketch, names) {
  const s = copy(sketch);
  const gone = new Set(names);
  const dropped = s.entities.filter(e => gone.has(e.id) || pointsOf(e).some(p => gone.has(p)));
  s.entities = s.entities.filter(e => !dropped.includes(e));
  const used = new Set(s.entities.flatMap(pointsOf));
  for (const e of dropped) for (const p of pointsOf(e)) if (!used.has(p)) gone.add(p);
  for (const p of gone) delete s.points[p];
  for (const e of dropped) gone.add(e.id);
  s.constraints = s.constraints.filter(c => !refsOf(c).some(r => gone.has(r)));
  return s;
}

/** Turns construction on for these entities, or off when they all have it. */
export function toggleConstruction(sketch, ids) {
  const chosen = sketch.entities.filter(e => ids.includes(e.id));
  const on = !chosen.every(e => e.construction);
  return {
    ...sketch, entities: sketch.entities.map(e => {
      // A linked projection stays construction (the format's rule).
      if (!ids.includes(e.id) || e.project) return e;
      const { construction, ...rest } = e;
      return on ? { ...rest, construction: true } : rest;
    }),
  };
}

/** Why a point or entity cannot be called name (the kernel's rules, Model.Problem), or null. */
export function nameProblem(sketch, name, from) {
  if (!name) return 'a name cannot be empty';
  if (/[./]/.test(name)) return "a name cannot contain '.' or '/'";
  if (name === 'start' || name === 'end') return `'${name}' is the name of a cap`;
  if (name.startsWith('=')) return "a name cannot start with '='";
  if (name !== from && taken(sketch).has(name)) return `'${name}' is taken`;
  return null;
}

/** Renames a point or entity, and every reference to it. */
export function rename(sketch, from, to) {
  const s = copy(sketch);
  const swap = r => r === from ? to : r;
  if (from in s.points) {
    s.points = Object.fromEntries(Object.entries(s.points).map(([k, v]) => [swap(k), v]));
    for (const e of s.entities) {
      if (e.line) e.line = e.line.map(swap);
      if (e.arc) for (const k of ['center', 'from', 'to']) e.arc[k] = swap(e.arc[k]);
      if (e.circle) e.circle.center = swap(e.circle.center);
    }
  }
  for (const e of s.entities) if (e.id === from) e.id = to;
  s.constraints = s.constraints.map(c => {
    const kind = kindOf(c), refs = refsOf(c).map(swap);
    return constraint(kind, refs, c.value);
  });
  return s;
}

/** Moves the points and circle radii to a solve's answer (the sketch op's points and radii). */
export function adopt(sketch, { points, radii }) {
  return {
    ...sketch,
    points: Object.fromEntries(Object.entries(sketch.points).map(([k, v]) => [k, points[k] ?? v])),
    entities: sketch.entities.map(e => e.circle && radii[e.id] !== undefined ? { ...e, circle: { ...e.circle, radius: radii[e.id] } } : e),
  };
}

/** The constraints that fit a selection of points and entities, for the constrain buttons: [{kind, refs}]. */
export function applicable(sketch, selection) {
  const byId = new Map(sketch.entities.map(e => [e.id, e]));
  const points = selection.filter(n => n in sketch.points && !byId.has(n));
  const lines = selection.filter(n => byId.get(n)?.line);
  const rounds = selection.filter(n => byId.get(n) && !byId.get(n).line);
  const only = (p, l, r) => points.length === p && lines.length === l && rounds.length === r;
  const out = [];
  if (only(0, 1, 0)) out.push({ kind: 'horizontal', refs: lines }, { kind: 'vertical', refs: lines });
  if (only(0, 2, 0)) out.push(...['parallel', 'perpendicular', 'equal'].map(kind => ({ kind, refs: lines })));
  if (only(0, 1, 1)) out.push({ kind: 'tangent', refs: [lines[0], rounds[0]] });
  if (only(0, 0, 2)) out.push({ kind: 'tangent', refs: rounds }, { kind: 'equal', refs: rounds });
  if (only(2, 0, 0)) out.push(...['coincident', 'horizontal', 'vertical'].map(kind => ({ kind, refs: points })));
  if (only(1, 1, 0)) out.push({ kind: 'on', refs: [points[0], lines[0]] }, { kind: 'midpoint', refs: [points[0], lines[0]] });
  if (only(1, 0, 1)) out.push({ kind: 'on', refs: [points[0], rounds[0]] });
  if (only(2, 1, 0)) out.push({ kind: 'symmetric', refs: [...points, lines[0]] });
  if (points.length && !lines.length && !rounds.length) out.push({ kind: 'fix', refs: points });
  return out;
}

/** The constraints of an applicable entry: fix makes one per point. */
export const constraintsOf = ({ kind, refs }) => kind === 'fix' ? refs.map(p => constraint('fix', [p])) : [constraint(kind, refs)];

/** The document text to solve: the base document with the sketch in place of the one it edits, or at the end. */
export function workingDocument(base, sketch) {
  const doc = JSON.parse(base);
  const at = doc.features.findIndex(f => f.id === sketch.id);
  if (at >= 0) doc.features[at] = sketch; else doc.features.push(sketch);
  return JSON.stringify(doc);
}

/** The sketch as the settled document holds it (the sketch op's settled.text). */
export const settledSketch = (text, id) => JSON.parse(text).features.find(f => f.id === id);

/**
 * The edits that finish a sketch, one history entry: an edited sketch is set in place; a new one is inserted at the
 * end with the feature that uses it after it. feature: null (the sketch alone) or {op: 'extrude' | 'cut' | 'revolve' |
 * 'sweep' | 'sweep-cut', id, depth | angle, regions (outer entity ids each; empty for all), axis (a line, revolve),
 * path and from (a sweep: the path sketch and an entity of the chain it follows)}; hasBody says whether the document has
 * a body to join or cut.
 */
export function finishEdits(sketch, { editing = false, feature = null, hasBody = true } = {}) {
  if (editing) return [{ set: sketch.id, value: sketch }];
  const edits = [{ insert: sketch }];
  if (!feature) return edits;
  const regions = feature.regions?.length ? { regions: feature.regions } : {};
  const op = !hasBody ? 'new' : feature.op === 'cut' || feature.op === 'sweep-cut' ? 'cut' : 'join';
  if (feature.op.startsWith('sweep')) {
    edits.push({ insert: { id: feature.id, type: 'sweep', op, sketch: sketch.id, ...regions, path: { sketch: feature.path, from: feature.from } } });
  } else if (feature.op === 'revolve') {
    edits.push({ insert: { id: feature.id, type: 'revolve', op, sketch: sketch.id, ...regions, ...(feature.axis ? { axis: feature.axis } : {}), angle: feature.angle } });
  } else {
    // A cut goes into the part, from a little outside the face so no skin of material is left on it.
    const [start, end] = feature.op === 'cut' ? [-feature.depth, 1] : [0, feature.depth];
    edits.push({ insert: { id: feature.id, type: 'extrude', op, sketch: sketch.id, ...regions, start, end } });
  }
  return edits;
}

/**
 * The paths a sweep of sketch id can follow: every other sketch of the document with its entities, lines and arcs
 * first (a chain runs through one), then circles (a closed path of their own).
 */
export const sweepPaths = (doc, id) => (doc?.features ?? []).filter(f => f.type === 'sketch' && f.id !== id)
  .map(f => ({ id: f.id, entities: [...f.entities.filter(e => !e.circle), ...f.entities.filter(e => e.circle)].map(e => e.id) }))
  .filter(p => p.entities.length > 0);

/** What the history calls the finish. */
export function finishLabel(sketch, { editing = false, feature = null } = {}) {
  if (editing) return `edit sketch ${sketch.id}`;
  return feature ? `sketch ${sketch.id}, ${feature.op} ${feature.id}` : `sketch ${sketch.id}`;
}
