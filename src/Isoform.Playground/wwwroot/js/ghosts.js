// The ghost of a drag: the faces the dragged handle moves, copied from the mesh on screen and moved by the drag at
// once, before any regeneration comes back. Translucent amber with its outline, over the part. Cheap and
// approximate: a linear handle's faces slide along its axis, a diameter's wall grows about the hole's axis, a planar
// handle's faces slide in its plane, an angle's turn about its axis. The ghost moves from where the mesh on screen
// was made (the committed result at t = 0, or a preview at its t), so it closes up as previews catch up.
import * as THREE from 'three';
import { frameOf, valueAt } from './lib/dragmath.js';
import { shown } from './store.js';

const amber = 0xf59e0b;
const fill = new THREE.MeshBasicMaterial({ color: amber, transparent: true, opacity: 0.32, depthWrite: false, side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2 });
const outline = new THREE.LineBasicMaterial({ color: amber, transparent: true, opacity: 0.95, depthTest: false });

const zero = handle => handle.kind === 'planar' ? [0, 0] : 0;
const vector = a => new THREE.Vector3(...a);

/** The move of a handle's faces from t = from to t = to, as a matrix. */
export function ghostMatrix(handle, from, to) {
  const { n, u, v } = frameOf(handle);
  const m = new THREE.Matrix4();
  if (handle.kind === 'planar') return m.makeTranslation(vector(u).multiplyScalar(to[0] - from[0]).addScaledVector(vector(v), to[1] - from[1]));
  if (handle.kind === 'angle') {
    const c = vector(handle.center ?? handle.anchor);
    return m.makeTranslation(c).multiply(new THREE.Matrix4().makeRotationAxis(vector(n), (to - from) * Math.PI / 180))
      .multiply(new THREE.Matrix4().makeTranslation(c.clone().negate()));
  }
  if (handle.id.endsWith(':diameter')) {
    // The wall grows radially about the hole's axis (holes are along z), which is half the diameter behind the anchor.
    const c = vector(handle.anchor).addScaledVector(vector(n), -handle.value / 2), k = valueAt(handle, to) / valueAt(handle, from);
    return m.makeTranslation(c.x, c.y, 0).multiply(new THREE.Matrix4().makeScale(k, k, 1)).multiply(new THREE.Matrix4().makeTranslation(-c.x, -c.y, 0));
  }
  return m.makeTranslation(vector(n).multiplyScalar(to - from));
}

export function createGhosts(viewer, store) {
  const group = new THREE.Group();
  group.matrixAutoUpdate = false;
  group.renderOrder = 5;
  viewer.scene.add(group);
  let handle = null, t = 0, base = null; // base: {result, t} the ghost's faces were copied from

  function clearGeometry() {
    group.children.forEach(o => o.geometry.dispose());
    group.clear();
  }

  // Copies the handle's faces from the mesh on screen.
  function rebase() {
    clearGeometry();
    const s = store.state, result = shown(s);
    base = null;
    if (!handle || !result) return;
    base = { result, t: s.preview?.handle === handle.id ? s.preview.t : zero(handle) };
    const wanted = new Set(handle.faces), picked = [];
    const { positions, faceOfTriangle } = result.mesh;
    faceOfTriangle.forEach((f, k) => { if (result.faces[f].some(n => wanted.has(n))) picked.push(...positions.subarray(k * 9, k * 9 + 9)); });
    if (picked.length) {
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(picked), 3));
      const mesh = new THREE.Mesh(geometry, fill), edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 30), outline);
      mesh.renderOrder = edges.renderOrder = 5;
      group.add(mesh, edges);
    }
    place();
  }

  function place() {
    const moved = base && [t].flat().some((x, i) => Math.abs(x - [base.t].flat()[i]) > 1e-9);
    group.visible = !!moved && group.children.length > 0;
    if (moved) group.matrix.copy(ghostMatrix(handle, base.t, t));
    group.matrixWorldNeedsUpdate = true;
  }

  store.subscribe((s, before) => { if (handle && shown(s) !== shown(before)) rebase(); });

  return {
    /** A drag of this handle starts. */
    begin(h) {
      handle = h;
      t = zero(h);
      rebase();
    },
    /** The drag is at t. */
    move(at) {
      t = at;
      place();
    },
    clear() {
      handle = null;
      base = null;
      clearGeometry();
    },
    /** For the tests: whether a ghost shows, and how far it is moved from the mesh on screen. */
    state: () => ({ visible: group.visible && group.children.length > 0, faces: handle?.faces ?? [], from: base?.t ?? null, t }),
  };
}
