// Pointer to faces: hover lights a face and names it in the tooltip, a click selects it by name, shift+click a
// second face for an edge. The selection lives in the store; the hovered face is only this view's.
import { faceName, facesNamed, pick } from './store.js';

export function createPicking(viewer, store, tooltip) {
  const canvas = viewer.canvas;
  let hovered = -1, shown = null;

  function relight() {
    const { result, selection } = store.state;
    if (result !== shown) { hovered = -1; shown = result; } // a new mesh: its indices mean other faces
    const faces = facesNamed(result, selection);
    if (hovered >= 0) faces.push(hovered);
    viewer.light(faces);
  }
  // Subscribed after the viewer shows a new result, so this lights faces of the mesh on screen.
  store.subscribe((s, before) => { if (s.result !== before.result || s.selection !== before.selection) relight(); });

  canvas.addEventListener('pointermove', event => {
    const face = viewer.faceAt(event.clientX, event.clientY);
    if (face !== hovered) {
      hovered = face;
      relight();
    }
    if (face < 0) { tooltip.hidden = true; return; }
    const { faces, bodies } = store.state.result;
    const r = tooltip.parentElement.getBoundingClientRect();
    tooltip.textContent = (bodies.length > 1 ? `${faces[face].body}: ` : '') + faces[face].names.join(' · ');
    tooltip.style.left = `${event.clientX - r.left + 14}px`;
    tooltip.style.top = `${event.clientY - r.top + 12}px`;
    tooltip.hidden = false;
  });
  canvas.addEventListener('pointerleave', () => { tooltip.hidden = true; });

  let down = null;
  canvas.addEventListener('pointerdown', e => { down = [e.clientX, e.clientY]; });
  canvas.addEventListener('pointerup', event => {
    // A click, not the end of an orbit drag.
    if (!down || Math.hypot(event.clientX - down[0], event.clientY - down[1]) > 4) return;
    const face = viewer.faceAt(event.clientX, event.clientY);
    const name = face < 0 ? null : faceName(store.state.result.faces[face]);
    store.set({ selection: pick(store.state.selection, name, event.shiftKey) });
  });
}
