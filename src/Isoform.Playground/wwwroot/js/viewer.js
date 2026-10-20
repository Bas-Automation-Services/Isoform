// The 3D view: one mesh coloured per face, sharp and tangent edges, thin spots. It knows faces by mesh index only;
// which faces are lit is decided by whoever holds the selection.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const faceColor = (i, lit) => new THREE.Color().setHSL((i * 0.61803398875) % 1, 0.38, lit ? 0.78 : 0.64);
const tintColor = new THREE.Color(0x2f6fdb); // the page's accent: the selected feature's faces

function geometryOf(positions) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  return g;
}

export function createViewer(host) {
  // Transparent, so the page's own view colour shows behind the model in light and dark mode.
  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  host.prepend(renderer.domElement);

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(40, 1, 0.1, 10000);
  camera.up.set(0, 0, 1);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;

  scene.add(new THREE.HemisphereLight(0xffffff, 0x8890a0, 1.6));
  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  camera.add(sun);
  sun.position.set(1, 2, 3);
  scene.add(camera);

  const dark = matchMedia('(prefers-color-scheme: dark)').matches;
  const sharpMaterial = new THREE.LineBasicMaterial({ color: dark ? 0x0b0c0e : 0x30343b });
  const smoothMaterial = new THREE.LineBasicMaterial({ color: dark ? 0x0b0c0e : 0x30343b, transparent: true, opacity: 0.22 });
  const spotMaterial = new THREE.PointsMaterial({ color: 0xe0242b, size: 6, sizeAttenuation: false });

  let model = null; // { group, mesh, faceOfTriangle, trianglesOfFace }
  let lit = new Set();
  let marks = new Map(); // face → colour of its own, over the face colour (diagnostics)
  let flight = null;     // the camera flight under way; orbiting ends it
  controls.addEventListener('start', () => { flight = null; });

  let tinted = new Set();

  function paint(face) {
    const colors = model.mesh.geometry.attributes.color;
    const c = marks.has(face) ? new THREE.Color(marks.get(face)).offsetHSL(0, 0, lit.has(face) ? 0.08 : 0) : faceColor(face, lit.has(face));
    if (tinted.has(face) && !marks.has(face)) c.lerp(tintColor, 0.6);
    for (const t of model.trianglesOfFace[face] ?? [])
      for (let k = 0; k < 3; k++) colors.setXYZ(t * 3 + k, c.r, c.g, c.b);
    colors.needsUpdate = true;
  }

  function frame(geometry) {
    geometry.computeBoundingSphere();
    const { center, radius } = geometry.boundingSphere;
    if (!radius) return;
    controls.target.copy(center);
    // The whole bounding sphere in view, in the narrower of the two fields of view (the view sits between two panels).
    const half = THREE.MathUtils.degToRad(camera.fov) / 2;
    const narrow = Math.min(half, Math.atan(Math.tan(half) * camera.aspect));
    camera.position.copy(center).add(new THREE.Vector3(1.1, -1.6, 1.0).normalize().multiplyScalar(radius / Math.sin(narrow)));
    camera.near = radius / 100;
    camera.far = radius * 100;
    camera.updateProjectionMatrix();
  }

  new ResizeObserver(() => {
    const { clientWidth: w, clientHeight: h } = host;
    renderer.setSize(w, h);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }).observe(host);

  const frameHooks = new Set(); // e.g. handles keeping their size on screen as the camera moves
  renderer.setAnimationLoop(() => {
    controls.update();
    for (const hook of frameHooks) hook();
    renderer.render(scene, camera);
  });

  const raycaster = new THREE.Raycaster();

  return {
    canvas: renderer.domElement,
    // For overlays drawn in the view (handles): they add to the scene and pause the controls while they drag.
    scene, camera, controls,
    /** Calls hook before every frame is drawn. */
    onFrame: hook => frameHooks.add(hook),

    /** Replaces the model with a result's mesh; nothing is lit until light(). fit frames the camera on it. */
    show(result, fit) {
      if (model) {
        scene.remove(model.group);
        model.group.traverse(o => o.geometry?.dispose());
      }
      const { positions, faceOfTriangle, sharp, smooth } = result.mesh;
      const trianglesOfFace = result.faces.map(() => []);
      faceOfTriangle.forEach((f, t) => trianglesOfFace[f].push(t));

      const geometry = geometryOf(positions);
      geometry.setAttribute('color', new THREE.BufferAttribute(new Float32Array(positions.length), 3));
      geometry.computeVertexNormals();
      const mesh = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({
        vertexColors: true, roughness: 0.75, metalness: 0.05, polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
      }));
      const group = new THREE.Group();
      group.add(mesh,
        new THREE.LineSegments(geometryOf(sharp), sharpMaterial),
        new THREE.LineSegments(geometryOf(smooth), smoothMaterial),
        new THREE.Points(geometryOf(new Float32Array(result.thinSpots.flatMap(s => s.at))), spotMaterial));
      scene.add(group);

      model = { group, mesh, faceOfTriangle, trianglesOfFace };
      lit = new Set();
      marks = new Map();
      tinted = new Set();
      trianglesOfFace.forEach((_, f) => paint(f));
      if (fit) frame(geometry);
    },

    /** Lights exactly these faces (mesh indices), repainting only those that change. */
    light(faces) {
      if (!model) return;
      const before = lit;
      lit = new Set(faces);
      for (const f of before) if (!lit.has(f)) paint(f);
      for (const f of lit) if (!before.has(f)) paint(f);
    },

    /** Tints exactly these faces (mesh indices): a feature's faces, under the lit ones. */
    tint(faces) {
      if (!model) return;
      const before = tinted;
      tinted = new Set(faces);
      for (const f of before) if (!tinted.has(f)) paint(f);
      for (const f of tinted) if (!before.has(f)) paint(f);
    },

    /** The mesh index of the face under a client (CSS pixel) point, or -1. */
    faceAt(clientX, clientY) {
      if (!model) return -1;
      const r = renderer.domElement.getBoundingClientRect();
      raycaster.setFromCamera(new THREE.Vector2(((clientX - r.left) / r.width) * 2 - 1, -((clientY - r.top) / r.height) * 2 + 1), camera);
      const hit = raycaster.intersectObject(model.mesh)[0];
      return hit ? model.faceOfTriangle[hit.faceIndex] : -1;
    },

    // --- Diagnostics (J5): faces in colours of their own, a camera flight, world points to the screen.

    /** Gives these faces (Map or [face, colour] pairs, mesh indices) their own colour; the rest go back to theirs. */
    mark(colors) {
      if (!model) return;
      const before = marks;
      marks = new Map(colors);
      for (const f of new Set([...before.keys(), ...marks.keys()])) if (before.get(f) !== marks.get(f)) paint(f);
    },

    /** The marked faces as [face, colour] pairs. */
    marks: () => [...marks],

    /**
     * Flies the camera, in the direction it looks now, to frame these faces. With a point (a thin spot), to that point
     * with the faces' surroundings in view, turned to look along the first face there: a wall between two faces is
     * seen edge-on from the first face's side, not through the part. A smooth tween; orbiting takes over from it.
     */
    flyTo(faces, point) {
      if (!model) return;
      const box = new THREE.Box3(), p = model.mesh.geometry.attributes.position, v = new THREE.Vector3();
      for (const f of faces) for (const t of model.trianglesOfFace[f] ?? []) for (let k = 0; k < 3; k++) box.expandByPoint(v.fromBufferAttribute(p, t * 3 + k));
      const geometry = model.mesh.geometry;
      if (!geometry.boundingSphere) geometry.computeBoundingSphere();
      const whole = geometry.boundingSphere.radius;
      const sphere = box.isEmpty() ? new THREE.Sphere(controls.target.clone(), whole) : box.getBoundingSphere(new THREE.Sphere());
      const center = point ? new THREE.Vector3(...point) : sphere.center;
      // A thin spot is small: show it with some part around it, not the whole of a large face.
      const radius = Math.max(point ? Math.min(sphere.radius, whole * 0.4) : sphere.radius, whole * 0.08);
      const look = camera.position.clone().sub(controls.target).normalize();
      if (point && faces.length) {
        // The normal of the first face's triangle nearest the point: the thickness direction there.
        const triangle = new THREE.Triangle(), normal = new THREE.Vector3();
        let best = Infinity;
        for (const t of model.trianglesOfFace[faces[0]] ?? []) {
          const d = triangle.setFromAttributeAndIndices(p, t * 3, t * 3 + 1, t * 3 + 2).getMidpoint(v).distanceToSquared(center);
          if (d < best) { best = d; triangle.getNormal(normal); }
        }
        if (best < Infinity) look.addScaledVector(normal, -look.dot(normal) + 0.6).normalize();
      }
      const from = { position: camera.position.clone(), target: controls.target.clone() };
      const to = { target: center, position: center.clone().addScaledVector(look, radius * 2.6) };
      const start = performance.now(), ms = 650, mine = flight = Symbol('flight');
      const step = now => {
        if (flight !== mine) return;
        const t = Math.min((now - start) / ms, 1), e = t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2;
        camera.position.lerpVectors(from.position, to.position, e);
        controls.target.lerpVectors(from.target, to.target, e);
        if (t < 1) requestAnimationFrame(step); else flight = null;
      };
      requestAnimationFrame(step);
    },

    /** Where the camera looks: the orbit centre. */
    target: () => controls.target.toArray(),

    /** A world point in CSS pixels relative to the view, or null behind the camera. */
    toScreen([x, y, z]) {
      const v = new THREE.Vector3(x, y, z).project(camera);
      if (v.z > 1) return null;
      const { clientWidth: w, clientHeight: h } = renderer.domElement;
      return [(v.x + 1) / 2 * w, (1 - v.y) / 2 * h];
    },
  };
}
