// The selected feature's handles in the view. Selecting a face selects its feature: its faces get a tint and its
// handles show, as arrows (linear), discs (planar) and arcs (angle), a constant size on screen. Hovering one lights it
// and shows its label and value; dragging it computes the edits every move (preview) and hands them to commit on
// release. Handles set by an expression are grey and do not drag. The handles come from the kernel (the C# contract
// in docs/playground-plan.md) after every regeneration; the math is in lib/dragmath.js.
import * as THREE from 'three';
import { anchorAt, featureOf, frameOf, shownHandles, startDrag, valueAt } from './lib/dragmath.js';
import { faceName } from './store.js';

// Sizes in CSS pixels.
const arrow = { length: 64, head: 16, radius: 6, shaft: 1.6 }, disc = 11, arc = { radius: 46, tube: 1.6, knob: 6 };
const reach = 9;  // how near the pointer must come to a handle's outline to grab it
const color = { idle: 0x2f6fdb, hot: 0xf59e0b, readOnly: 0x9aa0a6, tint: 0x2f6fdb };

const vector = a => new THREE.Vector3(...a);
const shown = x => String(Math.round(x * 1000) / 1000);

// One geometry per shape, in pixels, along local +z (the handle's axis) or in local xy (its plane).
const shaftGeometry = new THREE.CylinderGeometry(arrow.shaft, arrow.shaft, arrow.length - arrow.head, 8)
  .rotateX(Math.PI / 2).translate(0, 0, (arrow.length - arrow.head) / 2);
const headGeometry = new THREE.ConeGeometry(arrow.radius, arrow.head, 16)
  .rotateX(Math.PI / 2).translate(0, 0, arrow.length - arrow.head / 2);
const discGeometry = new THREE.CircleGeometry(disc, 32);
const rimGeometry = new THREE.RingGeometry(disc, disc + 2, 32);
const knobGeometry = new THREE.SphereGeometry(arc.knob, 16, 8);
const arcGeometry = degrees => new THREE.TorusGeometry(arc.radius, arc.tube, 6, 64, Math.min(Math.abs(degrees), 360) * Math.PI / 180);

/**
 * fetch() resolves to the handles of the main slot's last result; a kernel without the op rejects, which shows
 * none. preview(edits, handle) is called on every move of a drag, commit(edits, handle) once on release.
 */
export function createHandles(viewer, store, { fetch, commit, preview = () => { } }) {
  const { scene, camera, controls, canvas } = viewer;
  const host = canvas.parentElement;
  const group = new THREE.Group();
  group.renderOrder = 10;
  scene.add(group);
  const label = document.createElement('div');
  label.className = 'handle-label';
  label.hidden = true;
  host.append(label);

  let all = [];        // every handle of the result on screen
  let visuals = [];    // per shown handle: { handle, object, materials, arc?, knob?, t }
  let tint = null;     // the overlay that tints the selected feature's faces
  let hot = null, drag = null; // the visual under the pointer, and the one being dragged: { visual, move, state }

  // A new result drops the old handles (their anchors moved) and fetches its own; a reply for a result no longer on
  // screen is stale.
  store.subscribe((s, before) => {
    if (s.result !== before.result) {
      all = [];
      const asked = s.result;
      const arrived = list => { if (asked === store.state.result) { all = list ?? []; rebuild(); } };
      if (asked) fetch().then(arrived, () => arrived([]));
    }
    if (s.result !== before.result || s.selection !== before.selection) rebuild();
  });

  function rebuild() {
    for (const v of visuals) { v.arc?.dispose(); v.materials.forEach(m => m.dispose()); }
    group.clear();
    if (tint) { scene.remove(tint); tint.geometry.dispose(); tint = null; }
    if (drag) { drag = null; controls.enabled = true; }
    hot = null;
    label.hidden = true;
    const { result, selection } = store.state;
    visuals = result ? shownHandles(all, selection).map(visualOf) : [];
    for (const v of visuals) group.add(v.object);
    if (result && selection.length) tintFeature(result, featureOf(selection[0]));
  }

  function tintFeature(result, feature) {
    const { positions, faceOfTriangle } = result.mesh;
    const mine = result.faces.map(face => faceName(face).startsWith(feature + '/'));
    const picked = [];
    faceOfTriangle.forEach((f, t) => { if (mine[f]) picked.push(...positions.subarray(t * 9, t * 9 + 9)); });
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(picked), 3));
    tint = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({
      color: color.tint, transparent: true, opacity: 0.16, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1,
    }));
    scene.add(tint);
  }

  function visualOf(handle) {
    const { n, u, v } = frameOf(handle);
    const object = new THREE.Group();
    object.quaternion.setFromRotationMatrix(new THREE.Matrix4().makeBasis(vector(u), vector(v), vector(n)));
    const material = () => new THREE.MeshBasicMaterial({ depthTest: false, depthWrite: false, transparent: true, side: THREE.DoubleSide });
    const visual = { handle, object, materials: [material()], t: handle.kind === 'planar' ? [0, 0] : 0 };
    const mesh = (geometry, m = visual.materials[0]) => { const o = new THREE.Mesh(geometry, m); o.renderOrder = 10; object.add(o); return o; };
    if (handle.kind === 'linear') { mesh(shaftGeometry); mesh(headGeometry); }
    if (handle.kind === 'planar') {
      visual.materials.push(material());
      mesh(discGeometry, visual.materials[1]);
      mesh(rimGeometry);
    }
    if (handle.kind === 'angle') {
      visual.arc = arcGeometry(handle.value);
      visual.arcMesh = mesh(visual.arc);
      visual.knob = mesh(knobGeometry);
      turn(visual, handle.value);
    }
    paint(visual);
    place(visual);
    return visual;
  }

  // An angle handle's arc runs from u through its value; the knob sits at the end.
  function turn(visual, degrees) {
    const a = degrees * Math.PI / 180;
    visual.knob.position.set(Math.cos(a) * arc.radius, Math.sin(a) * arc.radius, 0);
    visual.arc.dispose();
    visual.arc = visual.arcMesh.geometry = arcGeometry(degrees);
    visual.arcMesh.scale.set(1, Math.sign(degrees) || 1, 1); // a negative angle runs the other way round
  }

  function paint(visual) {
    const { readOnly } = visual.handle;
    const c = readOnly ? color.readOnly : visual === hot || visual === drag?.visual ? color.hot : color.idle;
    visual.materials[0].color.set(c);
    visual.materials[0].opacity = readOnly ? 0.55 : 1;
    if (visual.materials[1]) { visual.materials[1].color.set(c); visual.materials[1].opacity = readOnly ? 0.25 : 0.4; }
  }

  // World units per CSS pixel at a point, so handles keep their size on screen.
  function pixel(p) {
    const depth = p.clone().sub(camera.position).dot(camera.getWorldDirection(new THREE.Vector3()));
    return 2 * Math.max(depth, camera.near) * Math.tan(camera.fov * Math.PI / 360) / canvas.clientHeight;
  }

  function place(v) {
    v.object.position.copy(vector(anchorAt(v.handle, v.t)));
    v.object.scale.setScalar(pixel(v.object.position));
  }
  viewer.onFrame(() => visuals.forEach(place));

  function screenOf(p) {
    const r = canvas.getBoundingClientRect(), q = p.clone().project(camera);
    return [r.left + (q.x + 1) / 2 * r.width, r.top + (1 - q.y) / 2 * r.height];
  }

  // A handle's outline on screen, as a polyline in client pixels, with the point to grab it by and the screen
  // direction t grows in.
  function outline(v) {
    v.object.updateMatrixWorld();
    const at = (x, y, z) => screenOf(new THREE.Vector3(x, y, z).applyMatrix4(v.object.matrixWorld));
    if (v.handle.kind === 'linear') {
      const line = [at(0, 0, 0), at(0, 0, arrow.length)];
      return { line, grip: at(0, 0, arrow.length - arrow.head / 2), toward: line[1] };
    }
    if (v.handle.kind === 'planar') {
      const centre = at(0, 0, 0);
      return { line: [centre], grip: centre, toward: at(disc, 0, 0), wide: true };
    }
    const end = valueAt(v.handle, v.t) * Math.PI / 180, line = [];
    for (let k = 0; k <= 48; k++) line.push(at(Math.cos(end * k / 48) * arc.radius, Math.sin(end * k / 48) * arc.radius, 0));
    const grip = line.at(-1);
    return { line, grip, toward: at(Math.cos(end + 0.2) * arc.radius, Math.sin(end + 0.2) * arc.radius, 0) };
  }

  function distance([x, y], line) {
    if (line.length === 1) return Math.hypot(x - line[0][0], y - line[0][1]);
    let best = Infinity;
    for (let k = 1; k < line.length; k++) {
      const [ax, ay] = line[k - 1], [bx, by] = line[k], dx = bx - ax, dy = by - ay;
      const s = Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy || 1)));
      best = Math.min(best, Math.hypot(x - ax - s * dx, y - ay - s * dy));
    }
    return best;
  }

  function under(point) {
    let best = null, nearest = Infinity;
    for (const v of visuals) {
      const o = outline(v), d = distance(point, o.line) - (o.wide ? disc : 0);
      if (d < reach && d < nearest) { best = v; nearest = d; }
    }
    return best;
  }

  function rayAt(clientX, clientY) {
    const r = canvas.getBoundingClientRect(), raycaster = new THREE.Raycaster();
    raycaster.setFromCamera(new THREE.Vector2((clientX - r.left) / r.width * 2 - 1, -(clientY - r.top) / r.height * 2 + 1), camera);
    return { origin: raycaster.ray.origin.toArray(), direction: raycaster.ray.direction.toArray() };
  }

  function readout(v, [x, y]) {
    const { handle } = v;
    const value = handle.kind === 'planar'
      ? `(${(drag?.visual === v ? drag.state.edits : handle.edits.map(e => ({ value: e.base }))).map(e => shown(e.value)).join(', ')})`
      : shown(valueAt(handle, v.t)) + (handle.kind === 'angle' ? '°' : '');
    label.textContent = `${handle.label}: ${value}` + (handle.readOnly ? ' · set by an expression' : '');
    label.hidden = false;
    // Kept inside the view, so a drag to its edge does not make the page scroll.
    const r = host.getBoundingClientRect();
    label.style.left = `${Math.max(4, Math.min(x - r.left + 16, r.width - label.offsetWidth - 4))}px`;
    label.style.top = `${Math.max(4, Math.min(y - r.top - 30, r.height - label.offsetHeight - 4))}px`;
  }

  function hover(v) {
    if (v === hot) return;
    const was = hot;
    hot = v;
    if (was) paint(was);
    if (v) paint(v);
    canvas.style.cursor = v && !v.handle.readOnly ? 'grab' : '';
    if (!v) label.hidden = true;
  }

  // Captured on the view, before picking and the orbit controls see the event: a drag on a handle is not theirs.
  host.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    const v = under([e.clientX, e.clientY]);
    if (!v || v.handle.readOnly) return;
    const move = startDrag(v.handle, rayAt(e.clientX, e.clientY));
    if (!move) return;
    e.stopPropagation();
    canvas.setPointerCapture(e.pointerId);
    controls.enabled = false;
    drag = { visual: v, move, state: move.move(rayAt(e.clientX, e.clientY)) };
    canvas.style.cursor = 'grabbing';
    paint(v);
  }, { capture: true });

  host.addEventListener('pointermove', e => {
    const point = [e.clientX, e.clientY];
    if (drag) {
      e.stopPropagation();
      const before = drag.state;
      drag.state = drag.move.move(rayAt(...point));
      drag.visual.t = drag.state.t;
      if (drag.visual.handle.kind === 'angle') turn(drag.visual, drag.state.value);
      readout(drag.visual, point);
      if (JSON.stringify(drag.state.edits) !== JSON.stringify(before.edits)) preview(drag.state.edits, drag.visual.handle);
      return;
    }
    const v = under(point);
    hover(v);
    if (v) readout(v, point);
  }, { capture: true });

  host.addEventListener('pointerup', e => {
    if (!drag) return;
    e.stopPropagation();
    canvas.releasePointerCapture(e.pointerId);
    controls.enabled = true;
    const { visual, state } = drag;
    drag = null;
    paint(visual);
    hover(null);
    // The handle stays where it was let go until the new result brings the handles back.
    if ([state.t].flat().some(t => t !== 0)) commit(state.edits, visual.handle);
  }, { capture: true });

  return {
    /** The shown handles as plain data, with where to grab each on screen and the screen direction t grows in. */
    list: () => visuals.map(v => {
      const { grip, toward } = outline(v), length = Math.hypot(toward[0] - grip[0], toward[1] - grip[1]) || 1;
      const { id, feature, kind, label: name, value, faces, readOnly = false } = v.handle;
      return { id, feature, kind, label: name, value, faces, readOnly, screen: grip, direction: [(toward[0] - grip[0]) / length, (toward[1] - grip[1]) / length] };
    }),
  };
}
