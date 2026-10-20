import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const $ = id => document.getElementById(id);
const editor = $('editor'), stats = $('stats'), parse = $('parse'), diagnostics = $('diagnostics');
const selectionBox = $('selection'), tooltip = $('tooltip'), examplesBox = $('examples');

// Anything that goes wrong shows in the status line instead of leaving the page stuck.
const report = e => { stats.textContent = `Error: ${e?.message ?? e}`; };
addEventListener('error', e => report(e.error ?? e.message));
addEventListener('unhandledrejection', e => report(e.reason));

// ---- Viewer -------------------------------------------------------------------------------------------------

const host = $('view');
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

let model = null;          // { group, mesh, faceOfTriangle, trianglesOfFace, sources }
let hovered = -1, picked = [];

function faceColor(i, highlight) {
  return new THREE.Color().setHSL((i * 0.61803398875) % 1, 0.38, highlight ? 0.78 : 0.64);
}

function paint(face) {
  const colors = model.mesh.geometry.attributes.color;
  const c = faceColor(face, face === hovered || picked.includes(face));
  for (const t of model.trianglesOfFace[face])
    for (let k = 0; k < 3; k++) colors.setXYZ(t * 3 + k, c.r, c.g, c.b);
  colors.needsUpdate = true;
}

function geometryOf(positions) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  return g;
}

function show(result, fit) {
  const { info, positions, faces, sharp, smooth } = result;
  const previous = model;
  if (previous) {
    scene.remove(previous.group);
    previous.group.traverse(o => o.geometry?.dispose());
  }

  const trianglesOfFace = info.faces.map(() => []);
  faces.forEach((f, t) => trianglesOfFace[f].push(t));

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
    new THREE.Points(geometryOf(new Float32Array(info.thinSpots.flat())), spotMaterial));
  scene.add(group);

  model = { group, mesh, faceOfTriangle: faces, trianglesOfFace, sources: info.faces };
  hovered = -1;
  // Keep a selection whose faces still exist under the same names.
  picked = picked.filter(f => previous && same(previous.sources[f], info.faces[f]));
  info.faces.forEach((_, f) => paint(f));
  if (fit) frame(geometry);
}

const same = (a, b) => !!a && !!b && a.join() === b.join();

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

function resize() {
  const { clientWidth: w, clientHeight: h } = host;
  renderer.setSize(w, h);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(host);

renderer.setAnimationLoop(() => {
  controls.update();
  renderer.render(scene, camera);
});

// ---- Picking ------------------------------------------------------------------------------------------------

const raycaster = new THREE.Raycaster();
function faceAt(event) {
  if (!model) return -1;
  const r = renderer.domElement.getBoundingClientRect();
  const ndc = new THREE.Vector2(((event.clientX - r.left) / r.width) * 2 - 1, -((event.clientY - r.top) / r.height) * 2 + 1);
  raycaster.setFromCamera(ndc, camera);
  const hit = raycaster.intersectObject(model.mesh)[0];
  return hit ? model.faceOfTriangle[hit.faceIndex] : -1;
}

renderer.domElement.addEventListener('pointermove', event => {
  const face = faceAt(event);
  if (face !== hovered) {
    const before = hovered;
    hovered = face;
    if (before >= 0) paint(before);
    if (face >= 0) paint(face);
  }
  if (face < 0) { tooltip.hidden = true; return; }
  const r = host.getBoundingClientRect();
  tooltip.textContent = model.sources[face].join(' · ');
  tooltip.style.left = `${event.clientX - r.left + 14}px`;
  tooltip.style.top = `${event.clientY - r.top + 12}px`;
  tooltip.hidden = false;
});
renderer.domElement.addEventListener('pointerleave', () => { tooltip.hidden = true; });

let down = null;
renderer.domElement.addEventListener('pointerdown', e => { down = [e.clientX, e.clientY]; });
renderer.domElement.addEventListener('pointerup', event => {
  // A click, not the end of an orbit drag.
  if (!down || Math.hypot(event.clientX - down[0], event.clientY - down[1]) > 4) return;
  const face = faceAt(event);
  const before = picked;
  picked = face < 0 ? [] : event.shiftKey && picked.length === 1 && picked[0] !== face ? [picked[0], face] : [face];
  [...before, ...picked].forEach(f => paint(f));
  showSelection();
});

function showSelection() {
  if (!model || picked.length === 0) { selectionBox.hidden = true; return; }
  const names = picked.map(f => model.sources[f][0]);
  const text = picked.length === 2 ? JSON.stringify(names) : JSON.stringify(names[0]);
  const label = picked.length === 2 ? 'Edge' : `Face (${model.sources[picked[0]].join(', ')})`;
  selectionBox.replaceChildren(`${label}: `);
  const code = document.createElement('code');
  code.textContent = text;
  const copy = document.createElement('button');
  copy.textContent = 'copy';
  copy.onclick = () => navigator.clipboard?.writeText(text);
  selectionBox.append(code, copy);
  selectionBox.hidden = false;
}

// ---- Regeneration in the worker -----------------------------------------------------------------------------

const kernel = new Worker('kernel.js', { type: 'module' });
let busy = false, pending = false, fitNext = true, timer = 0;

function regenerate() {
  // One run at a time; edits made meanwhile are folded into a single next run.
  if (busy) { pending = true; return; }
  busy = true;
  stats.textContent = 'Regenerating…';
  kernel.postMessage({ document: editor.value, cell: +$('cell').value, chord: +$('chord').value });
}

function received(result) {
  busy = false;
  const { info } = result;
  if (info.error) {
    editor.classList.add('bad');
    parse.textContent = info.error;
    stats.textContent = 'The document does not parse.';
  } else {
    editor.classList.remove('bad');
    parse.textContent = '';
    show(result, fitNext);
    fitNext = false;
    stats.textContent = `${info.ms.toFixed(0)} ms · ${info.triangles.toLocaleString()} triangles · `
      + `${info.faces.length} faces, ${info.edges} edges, ${info.vertices} vertices · ${info.bodies} ${info.bodies === 1 ? 'body' : 'bodies'}`;
    diagnostics.replaceChildren(...info.diagnostics.map(d => {
      const li = document.createElement('li');
      if (d.code === 'invalid-parameter') li.className = 'error';
      const b = document.createElement('b');
      b.textContent = d.code;
      li.append(b, d.feature ? ` · ${d.feature}: ` : ': ', d.message);
      return li;
    }));
    showSelection();
    try { localStorage.setItem('isoform.document', editor.value); } catch { }
  }
  if (pending) {
    pending = false;
    regenerate();
  }
}

function schedule() {
  clearTimeout(timer);
  timer = setTimeout(regenerate, 300);
}

editor.addEventListener('input', schedule);
editor.addEventListener('keydown', e => {
  if (e.key !== 'Tab') return;
  e.preventDefault();
  editor.setRangeText('  ', editor.selectionStart, editor.selectionEnd, 'end');
  schedule();
});
$('cell').addEventListener('change', regenerate);
$('chord').addEventListener('change', regenerate);

// Like the canonical text, but with arrays of plain values on one line: easier to read and edit.
function pretty(value, indent = '') {
  const inner = indent + '  ';
  if (Array.isArray(value)) {
    if (value.every(v => v === null || typeof v !== 'object')) return JSON.stringify(value).replaceAll(',', ', ');
    return '[\n' + value.map(v => inner + pretty(v, inner)).join(',\n') + '\n' + indent + ']';
  }
  if (value && typeof value === 'object')
    return '{\n' + Object.entries(value).map(([k, v]) => inner + JSON.stringify(k) + ': ' + pretty(v, inner)).join(',\n') + '\n' + indent + '}';
  return JSON.stringify(value);
}

function start(examplesText) {
  const examples = Object.fromEntries(Object.entries(JSON.parse(examplesText)).map(([k, v]) => [k, pretty(JSON.parse(v))]));
  for (const name of Object.keys(examples)) examplesBox.add(new Option(name, name));
  examplesBox.addEventListener('change', () => {
    if (!examplesBox.value) return;
    editor.value = examples[examplesBox.value];
    picked = [];
    fitNext = true;
    regenerate();
  });

  // ?example=3 opens an example by number; otherwise the last document you edited, or the first example.
  const asked = Object.keys(examples)[+new URLSearchParams(location.search).get('example')];
  let saved = null;
  try { saved = localStorage.getItem('isoform.document'); } catch { }
  if (asked) {
    examplesBox.value = asked;
    editor.value = examples[asked];
  } else if (saved) {
    examplesBox.add(new Option('(your last document)', ''), 0);
    examplesBox.value = '';
    editor.value = saved;
  } else {
    editor.value = Object.values(examples)[0];
  }
  regenerate();
}

kernel.onmessage = ({ data }) => data.type === 'ready' ? start(data.examples) : received(data);
kernel.onerror = e => report(e.message ?? 'the kernel worker failed to start');
