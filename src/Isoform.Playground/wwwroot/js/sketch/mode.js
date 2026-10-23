// Sketch mode (docs/sketch-plan.md §4, B1–B5): draw a constrained sketch on a flat face and finish it as an extrude,
// cut or revolve, or edit a sketch of the document. The view looks straight down the face with an orthographic
// camera, the part dimmed behind an SVG layer that holds the drawing and takes the pointer. Every change and every
// drag solves the sketch in the kernel (the `sketch` op, latest wins) and the drawing shows the solved points. The
// finish commits through applyEdits as one history entry. Fillet, trim and projecting a part edge are the kernel's (the
// `sketchEdit` op), each one undo step. The pure parts are model.js, infer.js, dimension.js, regions.js, frame.js,
// status.js, edit.js and project.js; the panel is panel.js.
//
//   const sketching = createSketchMode(store, { viewer, view, column, call, latest, applyEdits });
//     view: the element the 3D view is in; column: the right-hand panel; call/latest: the kernel client's;
//     applyEdits(edits, label, origin): lib/commit.js. sketching.edit(id) edits a sketch (the tree's double-click),
//     sketching.api is for the browser tests.
import * as THREE from 'three';
import * as M from './model.js';
import { infer, tangentArc, tangentLine } from './infer.js';
import { label, measure, options, parseValue, showValue } from './dimension.js';
import { polylineOf, regionAt, regionKey, shapes } from './regions.js';
import { bounds, fit, fromScreen, frameOfPlane, gridStep, toScreen, toSketch, toWorld, zoom } from './frame.js';
import { states, summary } from './status.js';
import { createPanel } from './panel.js';
import { cornerAt, filletArc, filletEdit, filletRadius, runEdit, trimEdit, trimPiece } from './edit.js';
import { edgeAt, edgesOf, projectEdit } from './project.js';

const TOOLS = [
  ['select', 'Select', 'Esc'], ['line', 'Line', 'L'], ['rectangle', 'Rectangle', 'R'], ['circle', 'Circle', 'C'],
  ['arc', 'Arc', 'A'], ['slot', 'Slot', 'S'], ['point', 'Point', 'P'], ['dimension', 'Dimension', 'D'],
  ['fillet', 'Fillet', 'F'], ['trim', 'Trim', 'T'], ['project', 'Project', 'E'],
];
// The tools that draw: they place points where inference says.
const DRAWING = new Set(['line', 'rectangle', 'circle', 'arc', 'slot', 'point']);
const HINT = 'Click to draw · Shift draws without snapping · Esc stops a tool, then leaves · drag a point to move it · right-drag to pan · scroll to zoom · click inside a loop to pick it';
const HINTS = {
  fillet: 'Click a corner and type its radius, or drag out from it · Esc stops the tool',
  trim: 'Click the piece of a line, arc or circle to delete, up to where others cross it · Esc stops the tool',
  project: 'Click an edge of the part to project it into the sketch · Linked keeps it following the edge · Esc stops the tool',
};
const KEYS = Object.fromEntries(TOOLS.filter(t => t[2].length === 1).map(([id, , key]) => [key.toLowerCase(), id]));
const GLYPHS = { coincident: '◉', origin: '⌖', midpoint: '½', on: '∈', horizontal: 'H', vertical: 'V', tangent: 'T' };
const SYMBOLS = {
  horizontal: 'H', vertical: 'V', parallel: '∥', perpendicular: '⊥', equal: '=', tangent: 'T', coincident: '◉',
  midpoint: '½', on: '∈', symmetric: '⇹', fix: '⚓',
};

const el = (tag, props = {}, ...children) => {
  const e = Object.assign(document.createElement(tag), props);
  e.append(...children);
  return e;
};
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);
const f1 = n => n.toFixed(1);

// The distance from p to a polyline, in whatever units both are in.
function toPolyline(p, poly) {
  let best = Infinity;
  for (let i = 1; i < poly.length; i++) {
    const [a, b] = [poly[i - 1], poly[i]], d = [b[0] - a[0], b[1] - a[1]];
    const t = Math.max(0, Math.min(1, ((p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1]) / (d[0] * d[0] + d[1] * d[1] || 1)));
    best = Math.min(best, dist(p, [a[0] + t * d[0], a[1] + t * d[1]]));
  }
  return best;
}

export function createSketchMode(store, { viewer, view, column, call, latest, applyEdits }) {
  const camera = new THREE.OrthographicCamera();
  let s = null; // the session while sketching (begin)

  const enterButton = el('button', { className: 'sketch-enter', textContent: 'Sketch', onclick: () => enterOnSelection() });
  view.append(enterButton);
  const face = () => store.state.selection.length === 1 ? store.state.selection[0] : null;
  function updateEnter() {
    enterButton.hidden = !!s;
    enterButton.disabled = !face();
    enterButton.title = face() ? `Sketch on ${face()} (S)` : 'Select a flat face to sketch on it';
  }
  store.subscribe((st, before) => { if (st.selection !== before.selection) updateEnter(); });
  updateEnter();

  const fail = message => store.set({ failure: message });
  const docOf = () => JSON.parse(store.state.result.text);

  async function frameOfFace(name) {
    const { result } = await call('faceFrame', { face: name });
    if (result.error) fail(result.error);
    return result.error ? null : result;
  }

  /** A new sketch on the selected face. */
  async function enterOnSelection() {
    const name = face();
    if (!name || s || !store.state.result) return;
    const frame = await frameOfFace(name);
    if (frame) begin({ sketch: M.newSketch(M.freshFeatureId(docOf(), 'sketch'), { face: name }), frame, editing: false, face: name });
  }

  /** An existing sketch of the document, to edit. */
  async function edit(id) {
    if (s || !store.state.result) return;
    const sketch = docOf().features.find(f => f.id === id && f.type === 'sketch');
    if (!sketch) return;
    const frame = sketch.on.face ? await frameOfFace(sketch.on.face) : frameOfPlane(sketch.on.plane ?? sketch.on);
    if (frame) begin({ sketch: structuredClone(sketch), frame, editing: true, face: sketch.on.face ?? null });
  }

  // The face's sharp edges in sketch coordinates: its outline.
  function outlineOf(result, name, frame) {
    const index = result.faces.findIndex(f => f[0] === name);
    const { sharp, sharpEdges } = result.mesh;
    const segments = [];
    if (index < 0) return segments;
    for (let k = 0; k < sharpEdges.length; k++) {
      if (!result.lineFaces[sharpEdges[k]].includes(index)) continue;
      segments.push([toSketch(frame, [...sharp.subarray(6 * k, 6 * k + 3)]), toSketch(frame, [...sharp.subarray(6 * k + 3, 6 * k + 6)])]);
    }
    return segments;
  }

  // --- The layer over the view -----------------------------------------------------------------------------------

  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.classList.add('sketch-svg');
  const bar = el('div', { className: 'sketch-bar' });
  const valueBox = el('div', { className: 'sketch-value', hidden: true });
  const hint = el('div', { className: 'hint', textContent: HINT });
  const layer = el('div', { className: 'sketch-layer' }, svg, bar, valueBox, hint);
  new ResizeObserver(() => {
    if (!s) return;
    s.view = { ...s.view, width: layer.clientWidth, height: layer.clientHeight };
    placeCamera();
    draw();
  }).observe(layer);

  function begin({ sketch, frame, editing, face: on }) {
    const result = store.state.result;
    const doc = JSON.parse(result.text);
    s = {
      sketch, frame, editing, face: on, base: result.text, reply: null, version: 0,
      tool: 'select', construction: false, selection: [], hover: null, highlight: [], picked: new Set(), undo: [],
      draft: null, dimPicks: [], cursor: null, pick: null, down: null, pan: null, dragging: null,
      // fillet: the corner being rounded and its radius ({corner, radius, dragged}); radius: the last fillet's; piece:
      // what a trim would delete; note: what the last fillet or trim did, or why it could not ({text, level, items}).
      fillet: null, radius: null, piece: null, note: null,
      // project: the part's edges in the plane, the one under the pointer, and whether a projection keeps its link.
      edges: result.mesh ? edgesOf(result, frame) : [], edge: null, linked: false,
      finish: { op: 'extrude', depth: 10, angle: 360, id: M.freshFeatureId(doc, 'extrude'), axis: null, auto: true },
      parameters: Object.keys(doc.parameters ?? {}),
      outline: on ? outlineOf(result, on, frame) : [],
    };
    store.set({ selection: [], feature: null });
    view.querySelector('#tooltip')?.setAttribute('hidden', '');
    view.append(layer);
    hint.textContent = HINT;
    document.body.classList.add('sketching');
    const around = [...s.outline.flat(), ...Object.values(sketch.points)];
    s.view = fit(around.length ? bounds(around) : [[-50, -50], [50, 50]], layer.clientWidth, layer.clientHeight);
    panel = createPanel(column, actions);
    viewer.useCamera(camera);
    placeCamera();
    updateEnter();
    refresh();
    solve();
  }

  function leave() {
    viewer.useCamera(null);
    layer.remove();
    panel.destroy();
    document.body.classList.remove('sketching');
    valueBox.hidden = true;
    s = null;
    updateEnter();
  }

  // The camera straight down the plane's normal, its x the frame's x, framing what the view shows.
  function placeCamera() {
    const { frame, view: v } = s;
    const centre = toWorld(frame, v.center), far = 5000;
    camera.position.set(...centre.map((c, i) => c + frame.normal[i] * far));
    camera.up.set(...frame.y);
    camera.lookAt(...centre);
    Object.assign(camera, { left: -v.width / 2 / v.scale, right: v.width / 2 / v.scale, top: v.height / 2 / v.scale, bottom: -v.height / 2 / v.scale, near: 1, far: 2 * far });
    camera.updateProjectionMatrix();
  }

  // --- Solving -----------------------------------------------------------------------------------------------------

  /** The geometry to draw: the sketch's points, or a conflicted solve's attempt. */
  function geo() {
    const r = s.reply && !s.reply.error ? s.reply : { points: {}, radii: {} };
    return { sketch: s.sketch, points: Object.fromEntries(Object.entries(s.sketch.points).map(([k, p]) => [k, r.points[k] ?? p])), radii: r.radii };
  }

  /** The last solve's regions as shapes, those whose entities are all still there (a change may be unsolved yet). */
  function shownRegions(g) {
    const ids = new Set(s.sketch.entities.map(e => e.id));
    const regions = (s.reply?.regions ?? []).filter(r => [...r.outer, ...r.holes.flat()].every(id => ids.has(id)));
    return shapes(regions, s.sketch, g.points, g.radii);
  }

  async function solve(drag) {
    const mine = s, version = ++s.version;
    const reply = await latest('sketch', 'sketch', { document: M.workingDocument(s.base, s.sketch), id: s.sketch.id, ...(drag ? { drag } : {}) });
    if (!reply || s !== mine || version !== s.version) return;
    take(reply.result, version);
  }

  // A solve's reply (the sketch op's, or a fillet's or trim's) for the sketch of that version.
  function take(r, version) {
    // A sketch in conflict keeps its points: the attempt is drawn, not stored.
    if (!r.error && !r.problems.some(p => p.code === 'sketch-conflict')) s.sketch = M.adopt(s.sketch, r);
    s.reply = r.error ? r : { ...r, points: r.problems.some(p => p.code === 'sketch-conflict') ? r.points : {}, radii: {} };
    s.solvedVersion = version;
    refresh();
  }

  /** A changed sketch: one step back for undo, then solved. */
  function change(sketch) {
    s.undo.push(s.sketch);
    s.sketch = sketch;
    s.note = null;
    keepSelection();
    refresh();
    solve();
  }

  function keepSelection() {
    const names = new Set([...Object.keys(s.sketch.points), ...s.sketch.entities.map(e => e.id)]);
    s.selection = s.selection.filter(n => names.has(n));
  }

  /** A fillet or trim (edit.js): the kernel makes it and solves the result, one step back for undo; or says why not. */
  async function sketchEdit(edit) {
    const mine = s, version = s.version;
    const r = await runEdit(call, s, edit);
    if (s !== mine || version !== s.version) return; // the sketch changed meanwhile: the edit was of an older one
    Object.assign(s, { sketch: r.sketch, undo: r.undo, note: r.note, fillet: null, piece: null, hover: null, edge: null });
    if (!r.reply) return refresh();
    if (edit.op === 'fillet') s.radius = edit.radius;
    keepSelection();
    take(r.reply, ++s.version);
  }

  function undo() {
    if (!s.undo.length) return;
    s.sketch = s.undo.pop();
    s.draft = null;
    s.note = null;
    refresh();
    solve();
  }

  // --- Drawing ---------------------------------------------------------------------------------------------------

  function draw() {
    if (!s) return;
    const v = s.view, S = p => toScreen(v, p);
    const d = pts => pts.length ? 'M' + pts.map(p => S(p).map(f1).join(' ')).join('L') : '';
    const g = geo();
    const parts = [`<rect class="dim" width="${v.width}" height="${v.height}"/>`];

    // The grid, the sketch's axes and the face's outline.
    const [lo, hi] = [fromScreen(v, [0, v.height]), fromScreen(v, [v.width, 0])];
    const lines = step => {
      let out = '';
      for (let u = Math.ceil(lo[0] / step) * step; u <= hi[0]; u += step) out += `M${f1(S([u, 0])[0])} 0V${v.height}`;
      for (let w = Math.ceil(lo[1] / step) * step; w <= hi[1]; w += step) out += `M0 ${f1(S([0, w])[1])}H${v.width}`;
      return out;
    };
    parts.push(`<path class="grid" d="${lines(gridStep(v.scale, 12))}"/>`, `<path class="grid major" d="${lines(gridStep(v.scale, 80))}"/>`);
    const o = S([0, 0]);
    parts.push(`<path class="axes" d="M${f1(o[0])} 0V${v.height}M0 ${f1(o[1])}H${v.width}"/>`);
    parts.push(`<path class="outline" d="${s.outline.map(d).join('')}"/>`);
    // The project tool shows every edge of the part it can take, the one under the pointer lit.
    if (s.tool === 'project') {
      parts.push(`<path class="edge" d="${s.edges.flatMap(e => e.segments).map(d).join('')}"/>`);
      if (s.edge) parts.push(`<path class="edge lit" d="${s.edge.edge.segments.map(d).join('')}"/>`);
    }

    // Regions: picked ones shaded, the one under the pointer lighter.
    const regions = shownRegions(g);
    const under = s.tool === 'select' && s.cursor && !s.hover ? regionAt(regions, fromScreen(v, s.cursor)) : -1;
    regions.forEach((r, i) => {
      const cls = s.picked.has(r.key) ? 'picked' : i === under ? 'hover' : '';
      if (cls) parts.push(`<path class="region ${cls}" fill-rule="evenodd" d="${[r.outer, ...r.holes].map(p => d(p) + 'Z').join('')}"/>`);
    });

    // Entities and points, coloured by what the solve says about them.
    const { state, conflicts } = s.reply && !s.reply.error ? states(s.sketch, s.reply) : { state: new Map(), conflicts: new Set() };
    const lit = new Set([...s.highlight, ...s.dimPicks, ...(s.hover ? [s.hover] : [])]);
    const mark = name => `${state.get(name) ?? 'free'}${s.selection.includes(name) ? ' selected' : ''}${lit.has(name) ? ' lit' : ''}`;
    for (const e of s.sketch.entities)
      parts.push(`<path class="entity ${mark(e.id)}${e.construction ? ' construction' : ''}${e.project ? ' linked' : ''}" d="${d(polylineOf(e, g.points, g.radii))}"/>`);
    for (const [name, p] of Object.entries(g.points)) {
      const [x, y] = S(p);
      parts.push(`<circle class="point ${mark(name)}" cx="${f1(x)}" cy="${f1(y)}" r="3"/>`);
    }

    // Dimensions, their values clickable, off the outside of the sketch.
    const off = 18 / v.scale, all = Object.values(g.points);
    const middle = all.length ? [0, 1].map(k => all.reduce((sum, p) => sum + p[k], 0) / all.length) : null;
    s.sketch.constraints.forEach((c, i) => {
      if (!M.isDimension(c)) return;
      const l = label(c, g, off, middle);
      const [x, y] = S(l.at);
      parts.push(`<g class="dimension${conflicts.has(i) ? ' conflict' : ''}"><path d="${l.lines.map(d).join('')}"/>` +
        `<text data-dim="${i}" x="${f1(x)}" y="${f1(y)}">${l.text.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</text></g>`);
    });

    // What the tool would add, and the glyph of what inference adds.
    parts.push(...draftShapes(g).map(p => `<path class="draft" d="${d(p)}"/>`));
    // A fillet's arc and radius as it would be; the piece a trim would delete.
    if (s.fillet) {
      const arc = filletArc(s.sketch, g.points, s.fillet.corner, s.fillet.radius), [x, y] = S(g.points[s.fillet.corner]);
      if (arc) parts.push(`<path class="draft" d="${d(arc)}"/>`);
      parts.push(`<text class="glyph" x="${f1(x + 10)}" y="${f1(y - 10)}">R${+s.fillet.radius.toFixed(2)}</text>`);
    }
    if (s.piece) parts.push(`<path class="trim" d="${d(s.piece.polyline)}"/>`);
    if (s.pick && DRAWING.has(s.tool)) {
      const [x, y] = S(s.pick.at);
      parts.push(`<circle class="snap" cx="${f1(x)}" cy="${f1(y)}" r="4"/>`);
      const glyph = s.pick.tangent ? 'tangent' : s.pick.glyph;
      if (glyph) parts.push(`<text class="glyph" x="${f1(x + 10)}" y="${f1(y - 10)}">${GLYPHS[glyph]}</text>`);
    }
    svg.innerHTML = parts.join('');
  }

  // The tool's shapes so far, through the pointer.
  function draftShapes(g) {
    const d = s.draft, p = s.pick?.at;
    if (!d || !p) return [];
    const at = d.picks.map(k => k.point ? g.points[k.point] : k.at);
    switch (s.tool) {
      case 'line': return [[at[0], p]];
      case 'rectangle': return [[at[0], [p[0], at[0][1]], p, [at[0][0], p[1]], at[0]]];
      case 'circle': return [polylineOf({ id: '', circle: { center: 'c', radius: dist(at[0], p) } }, { c: at[0] })];
      case 'arc': {
        if (at.length === 1) return [[at[0], p]];
        const r = dist(at[1], at[0]), a = Math.atan2(p[1] - at[0][1], p[0] - at[0][0]);
        const tip = [at[0][0] + r * Math.cos(a), at[0][1] + r * Math.sin(a)];
        const pts = { c: at[0], f: d.sweep >= 0 ? at[1] : tip, t: d.sweep >= 0 ? tip : at[1] };
        return [polylineOf({ id: '', arc: { center: 'c', from: 'f', to: 't' } }, pts)];
      }
      case 'slot': {
        if (at.length === 1) return [[at[0], p]];
        const r = slotRadius(at[0], at[1], p);
        const { sketch: t } = M.addSlot(M.newSketch('draft', {}), { at: at[0] }, { at: at[1] }, r);
        return t.entities.map(e => polylineOf(e, t.points));
      }
    }
    return [];
  }

  const slotRadius = (a, b, p) => Math.max(toPolyline(p, [a, b]), 1e-3);

  // The toolbar and the panel: after every change and solve, not on every pointer move.
  let panel = null;
  function refresh() {
    if (!s) return;
    draw();
    const { state, conflicts } = s.reply && !s.reply.error ? states(s.sketch, s.reply) : { state: new Map(), conflicts: new Set() };
    const regionCount = s.reply?.regions?.length ?? 0;
    const lines = s.sketch.entities.filter(e => e.line).map(e => e.id);
    if (s.finish.axis && !lines.includes(s.finish.axis)) s.finish.axis = null;
    panel.render({
      sketch: s.sketch, editing: s.editing, face: s.face, reply: s.reply, state, conflicts, parameters: s.parameters, note: s.note,
      finish: s.finish, lines, picked: [...s.picked].filter(k => s.reply?.regions?.some(r => regionKey(r.outer) === k)).length, regionCount,
    });
    const status = s.reply ? summary(s.reply) : { text: 'solving…', level: 'free' };
    const constrain = M.applicable(s.sketch, s.selection);
    bar.replaceChildren(
      ...TOOLS.map(([id, name, key]) => el('button', { className: `tool${s.tool === id ? ' on' : ''}`, textContent: name, title: `${name} (${key})`, onclick: () => setTool(id) })),
      ...(s.tool === 'project' ? [el('button', {
        className: `tool${s.linked ? ' on' : ''}`, textContent: 'Linked', title: 'Linked: the projection follows the edge when the part changes (K)',
        onclick: () => { s.linked = !s.linked; refresh(); },
      })] : []),
      el('button', { className: `tool${s.construction ? ' on' : ''}`, textContent: 'Construction', title: 'Construction: new entities, or the selected ones (X)', onclick: () => toggleConstruction() }),
      ...constrain.map(a => el('button', {
        className: 'constrain', textContent: SYMBOLS[a.kind], title: `${a.kind} ${a.refs.join(' ')}`,
        onclick: () => { change(M.constraintsOf(a).reduce(M.addConstraint, s.sketch)); },
      })),
      el('span', { className: `sketch-status ${status.level}`, textContent: status.text }),
      el('button', { textContent: 'Undo', title: 'Ctrl+Z', disabled: !s.undo.length, onclick: () => undo() }),
      el('button', { textContent: 'Cancel', title: 'Leave without changing the document (Esc)', onclick: () => leave() }),
      el('button', { className: 'primary', textContent: s.editing ? 'Save' : 'Finish', onclick: () => finish() }));
  }

  // --- Pointer -----------------------------------------------------------------------------------------------------

  const local = e => {
    const r = svg.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  };

  /** The point or entity under a screen point (points first), or null. */
  function hitAt(px) {
    const g = geo(), S = p => toScreen(s.view, p);
    let best = null, near = 8;
    for (const [name, p] of Object.entries(g.points)) if (dist(S(p), px) < near) [best, near] = [name, dist(S(p), px)];
    return best ?? entityUnder(px);
  }

  /** The entity under a screen point, or null. */
  function entityUnder(px) {
    const g = geo(), S = p => toScreen(s.view, p);
    let best = null, near = 6;
    for (const e of s.sketch.entities) {
      const d = toPolyline(px, polylineOf(e, g.points, g.radii).map(S));
      if (d < near) [best, near] = [e.id, d];
    }
    return best;
  }

  /** The corner a fillet could round under a screen point, or null. */
  const cornerUnder = px => cornerAt(s.sketch, geo().points, fromScreen(s.view, px), 10 / s.view.scale);

  /** The part edge a projection would take under a screen point ({edge, near}), or null. */
  const edgeUnder = px => edgeAt(s.edges, fromScreen(s.view, px), 8 / s.view.scale);

  /** What a press picks in the current tool: a corner to fillet, an entity to trim, a part edge, else a point or entity. */
  const pickUnder = px => s.tool === 'fillet' ? cornerUnder(px) : s.tool === 'trim' ? entityUnder(px) : s.tool === 'project' ? edgeUnder(px) : hitAt(px);

  function setTool(tool) {
    s.tool = tool;
    s.draft = null;
    s.dimPicks = [];
    s.pick = null;
    s.fillet = null;
    s.piece = null;
    s.edge = null;
    hint.textContent = HINTS[tool] ?? HINT;
    valueBox.hidden = true;
    refresh();
  }

  function toggleConstruction() {
    const ids = s.selection.filter(n => s.sketch.entities.some(e => e.id === n));
    if (ids.length) change(M.toggleConstruction(s.sketch, ids));
    else { s.construction = !s.construction; refresh(); }
  }

  // Where a click would land now, inference applied (infer.js), with the tangents a line or arc may carry on with.
  function updatePick(shift) {
    const c = fromScreen(s.view, s.cursor), g = geo(), tol = 8 / s.view.scale, d = s.draft, tool = s.tool;
    const first = d?.picks[0], start = first ? (first.point ? g.points[first.point] : first.at) : null;
    const exclude = new Set(first?.point && (tool === 'line' || tool === 'rectangle') ? [first.point] : []);
    let pick = infer(c, { sketch: s.sketch, points: g.points, radii: g.radii, tol, from: tool === 'line' ? start : null, exclude, shift });
    if (!shift && tool === 'line' && first?.point && !pick.point && !pick.constraints.length) {
      const t = tangentLine(first.point, c, { sketch: s.sketch, points: g.points });
      if (t) pick = { at: t.at, constraints: [], tangent: t.tangent, glyph: 'tangent' };
    }
    if (!shift && tool === 'arc' && d?.picks.length === 1 && pick.point) {
      const t = tangentArc(pick.point, start, { sketch: s.sketch, points: g.points });
      if (t) pick = { ...pick, tangent: t.tangent };
    }
    // A circle's edge and a slot's width are no points of their own; neither is an arc's end off its radius.
    if ((tool === 'circle' && d) || (tool === 'slot' && d?.picks.length === 2)) pick = pick.point ? pick : { at: c, constraints: [], glyph: null };
    if (tool === 'arc' && d?.picks.length === 2) {
      const centre = start, a = Math.atan2(c[1] - centre[1], c[0] - centre[0]);
      d.sweep += ((a - d.angle + 3 * Math.PI) % (2 * Math.PI)) - Math.PI;
      d.angle = a;
    }
    s.pick = pick;
  }

  function click(px, shift, down) {
    const at = fromScreen(s.view, px);
    if (down.dim !== undefined && (s.tool === 'select' || s.tool === 'dimension')) return editDimension(+down.dim, px);
    if (s.tool === 'select') {
      const hit = down.hit;
      if (hit) s.selection = shift ? (s.selection.includes(hit) ? s.selection.filter(n => n !== hit) : [...s.selection, hit]) : [hit];
      else {
        const regions = shownRegions(geo());
        const i = regionAt(regions, at);
        if (i >= 0) s.picked.has(regions[i].key) ? s.picked.delete(regions[i].key) : s.picked.add(regions[i].key);
        else s.selection = [];
      }
      return refresh();
    }
    if (s.tool === 'dimension') {
      if (down.hit) s.dimPicks = s.dimPicks.length === 2 ? [down.hit] : [...s.dimPicks, down.hit];
      else if (s.dimPicks.length) newDimension(px);
      return draw();
    }
    if (s.tool === 'fillet') return down.hit ? radiusAt(px, down.hit) : undefined;
    if (s.tool === 'trim') return down.hit ? sketchEdit(trimEdit(down.hit, at)) : undefined;
    if (s.tool === 'project') return down.hit ? sketchEdit(projectEdit(down.hit.edge.faces, down.hit.near, s.linked)) : undefined;
    drawClick();
  }

  // One click of a drawing tool: the pick is added, and once the tool has all it needs its entities go in.
  function drawClick() {
    const pick = s.pick, d = s.draft ??= { picks: [] }, opts = { construction: s.construction };
    if (!pick) return;
    d.picks.push(pick);
    const [a, b, c] = d.picks;
    const done = () => { s.draft = null; };
    switch (s.tool) {
      case 'point': change(M.addPoint(s.sketch, a).sketch); done(); break;
      case 'line': {
        if (!b) break;
        if (b.point && b.point === a.point) { d.picks.pop(); break; } // no line from a point to itself
        const r = M.addLine(s.sketch, a, b, { ...opts, hv: b.hv, tangent: b.tangent });
        change(r.sketch);
        // A polyline carries on from the end, unless it closed onto a point that was there.
        s.draft = b.point ? null : { picks: [{ at: r.sketch.points[r.end], point: r.end, constraints: [] }] };
        break;
      }
      case 'rectangle': if (b) { change(M.addRectangle(s.sketch, a, b, opts).sketch); done(); } break;
      case 'circle': if (b) { change(M.addCircle(s.sketch, a, b, opts).sketch); done(); } break;
      case 'arc': {
        if (b && !c) {
          const centre = a.point ? geo().points[a.point] : a.at, p = b.point ? geo().points[b.point] : b.at;
          Object.assign(d, { sweep: 0, angle: Math.atan2(p[1] - centre[1], p[0] - centre[0]) });
        }
        if (c) { change(M.addArc(s.sketch, a, b, c, { ...opts, ccw: d.sweep >= 0, tangent: b.tangent }).sketch); done(); }
        break;
      }
      case 'slot': {
        if (!c) break;
        const g = geo(), [pa, pb] = [a, b].map(k => k.point ? g.points[k.point] : k.at);
        change(M.addSlot(s.sketch, a, b, slotRadius(pa, pb, c.at), opts).sketch);
        done();
        break;
      }
    }
    draw();
  }

  function onDown(e) {
    if (!s) return;
    svg.setPointerCapture(e.pointerId);
    const px = local(e);
    if (e.button === 1 || e.button === 2) { s.pan = { from: px, center: s.view.center, moved: false }; return; }
    if (e.button !== 0) return;
    const dim = e.target.closest?.('[data-dim]')?.dataset.dim;
    s.down = { px, hit: dim === undefined ? pickUnder(px) : null, dim };
  }

  function onMove(e) {
    if (!s) return;
    const px = local(e);
    if (s.pan) {
      const dx = (px[0] - s.pan.from[0]) / s.view.scale, dy = (px[1] - s.pan.from[1]) / s.view.scale;
      s.pan.moved ||= Math.hypot(px[0] - s.pan.from[0], px[1] - s.pan.from[1]) > 3;
      s.view = { ...s.view, center: [s.pan.center[0] - dx, s.pan.center[1] + dy] };
      placeCamera();
      return draw();
    }
    if (s.down?.hit && s.tool === 'select' && (s.dragging || dist(px, s.down.px) > 3)) return dragTo(fromScreen(s.view, px));
    // Dragging out from a corner sizes its fillet: the radius is the pointer's distance from the corner.
    if (s.down?.hit && s.tool === 'fillet' && (s.fillet?.dragged || dist(px, s.down.px) > 3)) {
      valueBox.hidden = true;
      const r = dist(geo().points[s.down.hit], fromScreen(s.view, px));
      s.fillet = { corner: s.down.hit, radius: Math.max(+r.toPrecision(2), 1e-3), dragged: true };
      return draw();
    }
    s.cursor = px;
    s.hover = s.tool === 'select' || s.tool === 'dimension' ? hitAt(px) : s.tool === 'fillet' ? cornerUnder(px) : null;
    if (s.tool === 'trim') {
      const id = entityUnder(px), g = geo();
      s.piece = id ? trimPiece(s.sketch, g.points, g.radii, id, fromScreen(s.view, px)) : null;
    }
    if (s.tool === 'project') s.edge = edgeUnder(px);
    if (DRAWING.has(s.tool)) updatePick(e.shiftKey);
    draw();
  }

  function onUp(e) {
    if (!s) return;
    const px = local(e);
    if (s.pan) {
      const { moved } = s.pan;
      s.pan = null;
      if (!moved && e.button === 2) { s.draft = null; draw(); } // a right click ends a polyline
      return;
    }
    const down = s.down;
    s.down = null;
    if (!down) return;
    if (s.dragging) {
      s.undo.push(s.dragging.before);
      s.dragging = null;
      return refresh();
    }
    if (s.fillet?.dragged) return sketchEdit(filletEdit(s.fillet.corner, s.fillet.radius));
    if (dist(px, down.px) <= 4) click(px, e.shiftKey, down);
  }

  // A drag in the select tool: a point is pulled by the kernel (the sketch op's drag); a line or arc moves its free
  // points with the pointer; a circle takes the pointer's distance as its radius. The solve does the rest.
  function dragTo(to) {
    const name = s.down.hit;
    s.dragging ??= { before: s.sketch, from: fromScreen(s.view, s.down.px) };
    if (name in s.sketch.points) return solve({ point: name, to });
    const e = s.sketch.entities.find(x => x.id === name), before = s.dragging.before;
    if (e.circle) {
      const c = s.sketch.points[e.circle.center];
      s.sketch = { ...s.sketch, entities: s.sketch.entities.map(x => x === e ? { ...x, circle: { ...x.circle, radius: Math.max(dist(c, to), 1e-3) } } : x) };
    } else {
      const fixed = new Set(s.sketch.constraints.filter(c => M.kindOf(c) === 'fix').flatMap(M.refsOf));
      const [du, dv] = [to[0] - s.dragging.from[0], to[1] - s.dragging.from[1]];
      const points = { ...s.sketch.points };
      for (const p of M.pointsOf(e)) if (!fixed.has(p)) points[p] = [before.points[p][0] + du, before.points[p][1] + dv];
      s.sketch = { ...s.sketch, points };
    }
    solve();
  }

  svg.addEventListener('pointerdown', onDown);
  svg.addEventListener('pointermove', onMove);
  svg.addEventListener('pointerup', onUp);
  svg.addEventListener('pointerleave', () => { if (s) { s.cursor = null; s.pick = null; s.hover = null; s.piece = null; s.edge = null; draw(); } });
  svg.addEventListener('contextmenu', e => e.preventDefault());
  svg.addEventListener('dblclick', () => { if (s?.tool === 'line') { s.draft = null; draw(); } });
  svg.addEventListener('wheel', e => {
    e.preventDefault();
    s.view = zoom(s.view, Math.exp(-e.deltaY * 0.0015), local(e));
    placeCamera();
    draw();
  }, { passive: false });

  // --- Dimension values ----------------------------------------------------------------------------------------

  // The value box at a screen point: for a new dimension, a choice of what the picks fit; for an existing one, its
  // value. Enter takes a number or =expression.
  function valueAt(px, { choices = null, index = null }) {
    const select = el('select', {}, ...(choices ?? []).map((o, i) => el('option', { value: i, textContent: `${o.kind} ${o.refs.join(' ')}` })));
    select.hidden = !choices;
    const now = () => choices ? choices[+select.value] : (() => {
      const c = s.sketch.constraints[index];
      return { kind: M.kindOf(c), refs: M.refsOf(c), value: measure(M.kindOf(c), M.refsOf(c), geo()), stored: c.value };
    })();
    const input = el('input', { spellcheck: false, value: showValue(now().stored ?? now().value, now().kind) });
    select.onchange = () => { input.value = showValue(now().value, now().kind); input.focus(); input.select(); };
    const hint = el('div', { className: 'muted', textContent: s.parameters.length ? `= ${s.parameters.join(', ')}` : 'a number, or =expression' });
    input.onkeydown = e => {
      e.stopPropagation();
      if (e.key === 'Escape') { valueBox.hidden = true; s.dimPicks = []; draw(); return; }
      if (e.key !== 'Enter') return;
      const o = now(), v = parseValue(input.value, o.kind, o.value);
      if (v === null) { input.classList.add('bad'); return; }
      valueBox.hidden = true;
      s.dimPicks = [];
      change(choices ? M.addConstraint(s.sketch, M.constraint(o.kind, o.refs, v)) : M.setValue(s.sketch, index, v));
    };
    showBox(px, input, select, input, hint);
  }

  function showBox(px, input, ...children) {
    valueBox.replaceChildren(...children);
    Object.assign(valueBox.style, { left: `${px[0] + 10}px`, top: `${px[1] + 10}px` });
    valueBox.hidden = false;
    input.focus();
    input.select();
  }

  // The fillet tool's value box at a corner: the radius (the last fillet's, else one to suit the corner), previewed as
  // it is typed; Enter fillets. A fillet takes a number: the kernel's edit has no expressions.
  function radiusAt(px, corner) {
    const start = s.radius ?? filletRadius(s.sketch, geo().points, corner);
    s.fillet = { corner, radius: start, dragged: false };
    const input = el('input', { spellcheck: false, value: showValue(start, 'radius') });
    const number = () => (v => typeof v === 'number' ? v : null)(parseValue(input.value, 'radius'));
    input.oninput = () => {
      input.classList.toggle('bad', number() === null);
      if (number() !== null) { s.fillet.radius = number(); draw(); }
    };
    input.onkeydown = e => {
      e.stopPropagation();
      if (e.key === 'Escape') { valueBox.hidden = true; s.fillet = null; draw(); return; }
      if (e.key !== 'Enter') return;
      if (number() === null) { input.classList.add('bad'); return; }
      valueBox.hidden = true;
      sketchEdit(filletEdit(corner, number()));
    };
    showBox(px, input, input, el('div', { className: 'muted', textContent: `Fillet radius at ${corner}, mm` }));
    draw();
  }

  function newDimension(px) {
    const choices = options(s.dimPicks, geo());
    if (!choices.length) { s.dimPicks = []; return; }
    valueAt(px, { choices });
  }

  const editDimension = (index, px) => valueAt(px, { index });

  // --- Panel actions, keys and the finish ------------------------------------------------------------------------

  const actions = {
    rename(from, to) {
      const problem = M.nameProblem(s.sketch, to, from);
      if (problem) return problem;
      const swap = id => id === from ? to : id;
      s.picked = new Set([...s.picked].map(k => regionKey(k.split(',').map(swap))));
      if (s.finish.axis === from) s.finish.axis = to;
      s.selection = s.selection.map(swap);
      change(M.rename(s.sketch, from, to));
      return null;
    },
    removeConstraint: i => change(M.removeConstraint(s.sketch, i)),
    setValue(i, text) {
      const c = s.sketch.constraints[i], kind = M.kindOf(c);
      const v = parseValue(text, kind, measure(kind, M.refsOf(c), geo()));
      if (v === null) return `${text} is not a value for ${kind}: a number, or =expression`;
      change(M.setValue(s.sketch, i, v));
      return null;
    },
    highlight(names) { s.highlight = names; draw(); },
    finishChanged(patch) {
      if (patch.id) s.finish.auto = false;
      Object.assign(s.finish, patch);
      if (patch.op && s.finish.auto && patch.op !== 'sketch') s.finish.id = M.freshFeatureId(JSON.parse(s.base), patch.op);
      if (patch.op === 'revolve' && !s.finish.axis) s.finish.axis = s.sketch.entities.find(e => e.line && e.construction)?.id ?? null;
      refresh();
    },
    finish: () => finish(),
    cancel: () => leave(),
  };

  /** Commits the sketch, settled at its solution, and the feature it makes: one history entry. */
  async function finish() {
    if (!s) return null;
    const { sketch, editing, finish: f, base } = s;
    const { result: r } = await call('sketch', { document: M.workingDocument(base, sketch), id: sketch.id });
    const settled = r.error ? sketch : M.settledSketch(r.settled.text, sketch.id);
    const regions = r.regions ?? [];
    const picked = regions.filter(x => s.picked.has(regionKey(x.outer))).map(x => x.outer);
    const feature = editing || f.op === 'sketch' ? null
      : { op: f.op, id: f.id, depth: f.depth, angle: f.angle, axis: f.axis, regions: picked.length === regions.length ? [] : picked };
    const hasBody = JSON.parse(base).features.some(x => x.type !== 'sketch');
    leave();
    return applyEdits(M.finishEdits(settled, { editing, feature, hasBody }), M.finishLabel(settled, { editing, feature }), 'sketch');
  }

  // Keys while sketching go to the sketch, before the page's own (history's Ctrl+Z, references' P); S starts one.
  addEventListener('keydown', e => {
    if (e.target.closest?.('textarea, input, select')) return;
    const key = e.key.toLowerCase(), mod = e.ctrlKey || e.metaKey;
    if (!s) {
      if (key === 's' && !mod && !e.altKey && face()) { e.preventDefault(); enterOnSelection(); }
      return;
    }
    e.stopImmediatePropagation();
    if (mod && key === 'z') { e.preventDefault(); return undo(); }
    if (mod) return;
    if (key === 'escape') {
      if (!valueBox.hidden) { valueBox.hidden = true; s.fillet = null; }
      else if (s.draft || s.dimPicks.length) { s.draft = null; s.dimPicks = []; }
      else if (s.tool !== 'select') return setTool('select');
      else return leave();
      return draw();
    }
    if (key === 'enter') { s.draft = null; return draw(); }
    if ((key === 'delete' || key === 'backspace') && s.selection.length) {
      const gone = s.selection;
      s.selection = [];
      return change(M.remove(s.sketch, gone));
    }
    if (key === 'x') return toggleConstruction();
    if (key === 'k' && s.tool === 'project') { s.linked = !s.linked; return refresh(); }
    if (KEYS[key]) setTool(KEYS[key]);
  }, { capture: true });

  const api = {
    active: () => !!s,
    enter: () => enterOnSelection(),
    edit,
    tool: name => setTool(name),
    /** A sketch point in the page's CSS pixels, to click at. */
    toScreen(p) {
      const r = svg.getBoundingClientRect(), [x, y] = toScreen(s.view, p);
      return [r.left + x, r.top + y];
    },
    state: () => s && {
      tool: s.tool, sketch: structuredClone(s.sketch), editing: s.editing, selection: [...s.selection], picked: [...s.picked],
      solved: s.reply?.solved ?? null, freeDegrees: s.reply?.freeDegrees ?? null, problems: s.reply?.problems ?? [],
      regions: s.reply?.regions ?? [], finish: { ...s.finish }, note: s.note && structuredClone(s.note), undo: s.undo.length,
      /** The last change is solved: the reply is for what is drawn. */
      current: s.solvedVersion === s.version,
    },
    finishChanged: patch => actions.finishChanged(patch),
    /** The part edges the project tool offers: their faces and a sketch point on each, to click at. */
    edges: () => s.edges.map(e => ({ faces: e.faces, at: e.segments[0].reduce((m, p) => [m[0] + p[0] / 2, m[1] + p[1] / 2], [0, 0]) })),
    linked: on => { s.linked = on; refresh(); },
    finish: () => finish(),
    cancel: () => leave(),
  };

  return { edit, api };
}
