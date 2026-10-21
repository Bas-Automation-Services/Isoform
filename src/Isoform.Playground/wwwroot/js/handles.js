// The selected feature's handles in the view: the store's feature, which the tree sets and a picked face sets to its
// own (panels/tree.js, which also tints the feature's faces). They show as arrows (linear) and discs (planar) a
// constant size on screen, and arcs (angle) round their axis through the part. Hovering one lights it and shows its
// label and value; dragging it computes the edits every move (preview) and hands them to commit on release; shift
// snaps to a tenth of the step. A pattern's handle has −/+ for its count. Handles set by an expression are grey and do
// not drag. The handles come from the kernel (the C# contract in src/Isoform.Interactive/README.md) after every
// regeneration; the math is in lib/dragmath.js.
import * as THREE from 'three';
import { anchorAt, countEdits, frameOf, shownHandles, startDrag, valueAt } from './lib/dragmath.js';

// Sizes in CSS pixels.
const arrow = { length: 64, head: 16, radius: 6, shaft: 1.6 }, disc = 11, knob = 6;
const reach = 9;  // how near the pointer must come to a handle's outline to grab it
const color = { idle: 0x2f6fdb, hot: 0xf59e0b, readOnly: 0x9aa0a6 };

const vector = a => new THREE.Vector3(...a);
const shown = x => String(Math.round(x * 1000) / 1000);

// One geometry per shape, in pixels, along local +z (the handle's axis) or in local xy (its plane).
const shaftGeometry = new THREE.CylinderGeometry(arrow.shaft, arrow.shaft, arrow.length - arrow.head, 8)
  .rotateX(Math.PI / 2).translate(0, 0, (arrow.length - arrow.head) / 2);
const headGeometry = new THREE.ConeGeometry(arrow.radius, arrow.head, 16)
  .rotateX(Math.PI / 2).translate(0, 0, arrow.length - arrow.head / 2);
const discGeometry = new THREE.CircleGeometry(disc, 32);
const rimGeometry = new THREE.RingGeometry(disc, disc + 2, 32);
const knobGeometry = new THREE.SphereGeometry(knob, 16, 8);

// An angle handle's arc in world units, in its frame (u towards the anchor): from the start of the sweep, value
// degrees behind the anchor, round to t.
function arcPoints(handle, t) {
  const r = Math.hypot(...handle.anchor.map((x, i) => x - (handle.center ?? handle.anchor)[i]));
  const from = -handle.value * Math.PI / 180, to = t * Math.PI / 180, points = [];
  for (let k = 0; k <= 96; k++) {
    const a = from + (to - from) * k / 96;
    points.push(new THREE.Vector3(Math.cos(a) * r, Math.sin(a) * r, 0));
  }
  return { r, points };
}

/**
 * fetch() resolves to the handles of the main slot's last result; a kernel without the op rejects, which shows
 * none. start(handle) is called when a drag starts, preview(edits, handle, {t, value, edits}) on every move that
 * changes the edits, end(handle, moved) on release, then commit(edits, handle) if it moved. A pattern's −/+ commit
 * the count's edit straight away.
 */
export function createHandles(viewer, store, { fetch, commit, preview = () => { }, start = () => { }, end = () => { } }) {
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
    if (s.result !== before.result || s.selection !== before.selection || s.feature !== before.feature) rebuild();
  });

  function rebuild() {
    for (const v of visuals) { v.arc?.geometry.dispose(); v.counter?.remove(); v.materials.forEach(m => m.dispose()); }
    group.clear();
    if (drag) { drag = null; controls.enabled = true; }
    hot = null;
    label.hidden = true;
    const { result, feature } = store.state;
    // shownHandles takes face names: any name under the feature picks its handles.
    visuals = result && feature ? shownHandles(all, [`${feature}/`]).map(visualOf) : [];
    for (const v of visuals) group.add(v.object);
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
      visual.fill = material();
      visual.materials.push(visual.fill);
      mesh(discGeometry, visual.fill);
      mesh(rimGeometry);
    }
    if (handle.kind === 'angle') {
      // Round the axis through center (the object sits there, unscaled): a line in world units, the knob on screen size.
      const line = new THREE.LineBasicMaterial({ depthTest: false, transparent: true });
      visual.materials.push(line);
      visual.arc = new THREE.Line(new THREE.BufferGeometry(), line);
      visual.arc.renderOrder = 10;
      object.add(visual.arc);
      visual.knob = mesh(knobGeometry);
      turn(visual, 0);
    }
    if (handle.count) visual.counter = counterOf(visual);
    paint(visual);
    place(visual);
    return visual;
  }

  // An angle handle at t: the arc from the start of its sweep to the knob.
  function turn(visual, t) {
    const { r, points } = arcPoints(visual.handle, t);
    visual.arc.geometry.dispose();
    visual.arc.geometry = new THREE.BufferGeometry().setFromPoints(points);
    visual.knob.position.set(Math.cos(t * Math.PI / 180) * r, Math.sin(t * Math.PI / 180) * r, 0);
  }

  // A pattern's copy count: − and + beside its handle; disabled at the minimum or when an expression sets it.
  function counterOf(visual) {
    const { handle } = visual, box = document.createElement('div');
    box.className = 'handle-count';
    box.dataset.handle = handle.id;
    const button = (text, delta) => {
      const b = document.createElement('button');
      b.className = delta > 0 ? 'plus' : 'minus';
      b.textContent = text;
      const edits = countEdits(handle, delta);
      b.disabled = !edits;
      b.title = handle.count.path ? `${handle.label.replace(/ \w+$/, '')} count ${handle.count.value + delta}` : 'set by an expression';
      b.onclick = e => { e.stopPropagation(); commit(edits, handle); };
      return b;
    };
    const value = document.createElement('span');
    value.textContent = `×${handle.count.value}`;
    box.append(button('−', -1), value, button('+', 1));
    host.append(box);
    return box;
  }

  function paint(visual) {
    const { readOnly } = visual.handle;
    const c = readOnly ? color.readOnly : visual === hot || visual === drag?.visual ? color.hot : color.idle;
    for (const m of visual.materials) {
      m.color.set(c);
      m.opacity = m === visual.fill ? (readOnly ? 0.25 : 0.4) : readOnly ? 0.55 : 1;
    }
  }

  // World units per CSS pixel at a point, so handles keep their size on screen.
  function pixel(p) {
    const depth = p.clone().sub(camera.position).dot(camera.getWorldDirection(new THREE.Vector3()));
    return 2 * Math.max(depth, camera.near) * Math.tan(camera.fov * Math.PI / 360) / canvas.clientHeight;
  }

  function place(v) {
    if (v.handle.kind === 'angle') {
      v.object.position.copy(vector(v.handle.center ?? v.handle.anchor));
      v.knob.scale.setScalar(pixel(vector(anchorAt(v.handle, v.t))));
    } else {
      v.object.position.copy(vector(anchorAt(v.handle, v.t)));
      v.object.scale.setScalar(pixel(v.object.position));
    }
    if (v.counter) {
      // Beside the arrow's foot, in view coordinates.
      const [x, y] = screenOf(v.object.position), r = host.getBoundingClientRect();
      v.counter.style.transform = `translate(${x - r.left + 14}px, ${y - r.top + 10}px)`;
    }
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
    // The arc in world units round center; grabbed by the knob, t growing right-handed round the axis.
    const { r, points } = arcPoints(v.handle, v.t), a = v.t * Math.PI / 180, step = Math.min(0.2, 30 / Math.max(r, 1e-9) * pixel(v.object.position));
    const line = points.filter((_, k) => k % 2 === 0).map(p => at(p.x, p.y, p.z));
    return { line, grip: at(Math.cos(a) * r, Math.sin(a) * r, 0), toward: at(Math.cos(a + step) * r, Math.sin(a + step) * r, 0) };
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
      ? `(${[valueAt(handle, v.t)].flat().map(shown).join(', ')})`
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
    if (e.button !== 0 || e.target.closest?.('.handle-count')) return;
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
    start(v.handle);
  }, { capture: true });

  host.addEventListener('pointermove', e => {
    const point = [e.clientX, e.clientY];
    if (drag) {
      e.stopPropagation();
      const before = drag.state;
      drag.state = drag.move.move(rayAt(...point), e.shiftKey);
      drag.visual.t = drag.state.t;
      if (drag.visual.handle.kind === 'angle') turn(drag.visual, drag.state.t);
      readout(drag.visual, point);
      if (JSON.stringify(drag.state.edits) !== JSON.stringify(before.edits)) preview(drag.state.edits, drag.visual.handle, drag.state);
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
    const moved = [state.t].flat().some(t => t !== 0);
    end(visual.handle, moved);
    if (moved) commit(state.edits, visual.handle);
  }, { capture: true });

  return {
    /** The shown handles as plain data, with where to grab each on screen and the screen direction t grows in. */
    list: () => visuals.map(v => {
      const { grip, toward } = outline(v), length = Math.hypot(toward[0] - grip[0], toward[1] - grip[1]) || 1;
      const { id, feature, kind, label: name, value, faces, readOnly = false, count = null } = v.handle;
      const handle = { id, feature, kind, label: name, value, faces, readOnly, count, screen: grip, direction: [(toward[0] - grip[0]) / length, (toward[1] - grip[1]) / length] };
      if (kind === 'planar') {
        // Planar: the screen direction of v too (direction is u's).
        v.object.updateMatrixWorld();
        const along = screenOf(new THREE.Vector3(0, disc, 0).applyMatrix4(v.object.matrixWorld)), l = Math.hypot(along[0] - grip[0], along[1] - grip[1]) || 1;
        handle.directionV = [(along[0] - grip[0]) / l, (along[1] - grip[1]) / l];
      }
      return handle;
    }),
    /** The drag under way: {id, t, value, edits}, or null. */
    dragging: () => drag && { id: drag.visual.handle.id, t: drag.state.t, value: drag.state.value, edits: drag.state.edits },
  };
}
