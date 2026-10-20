// The 3D view: one mesh coloured per face, sharp and tangent edges, thin spots. It knows faces by mesh index only;
// which faces are lit is decided by whoever holds the selection.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const faceColor = (i, lit) => new THREE.Color().setHSL((i * 0.61803398875) % 1, 0.38, lit ? 0.78 : 0.64);

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

  function paint(face) {
    const colors = model.mesh.geometry.attributes.color;
    const c = faceColor(face, lit.has(face));
    for (const t of model.trianglesOfFace[face] ?? [])
      for (let k = 0; k < 3; k++) colors.setXYZ(t * 3 + k, c.r, c.g, c.b);
    colors.needsUpdate = true;
  }

  function frame(geometry) {
    geometry.computeBoundingSphere();
    const { center, radius } = geometry.boundingSphere;
    if (!radius) return;
    controls.target.copy(center);
    camera.position.copy(center).add(new THREE.Vector3(1.1, -1.6, 1.0).normalize().multiplyScalar(radius * 2.6));
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

    /** The mesh index of the face under a client (CSS pixel) point, or -1. */
    faceAt(clientX, clientY) {
      if (!model) return -1;
      const r = renderer.domElement.getBoundingClientRect();
      raycaster.setFromCamera(new THREE.Vector2(((clientX - r.left) / r.width) * 2 - 1, -((clientY - r.top) / r.height) * 2 + 1), camera);
      const hit = raycaster.intersectObject(model.mesh)[0];
      return hit ? model.faceOfTriangle[hit.faceIndex] : -1;
    },
  };
}
