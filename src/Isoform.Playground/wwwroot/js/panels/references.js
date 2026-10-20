// Pinned references: pin the selected face or edge (the Pin button, or P), and after every regeneration each pin
// shows what the edit did to it: resolved, split, merged, gone or ambiguous. The kernel resolves pins by name
// (ReferencePins in src/Isoform.Interactive), so they survive renumbering. Click a pin to light the faces it lands on
// now, every piece of a split one.
//
// Store fields this panel owns (they outlive edits; committed() leaves them alone):
//   pins       [{id, ref, status, faces, edges?}]: ref is the pin by name, as the kernel returned it
//   highlight  {pin, result, faces}: faces of that result that picking lights besides the selection, or null
import { faceName } from '../store.js';

/**
 * What to pin for a selection: two names an edge, one a face. When the clicked face (a mesh index) is the selected
 * one it is pinned by index, so the kernel can tell a piece of a face that is already split by its neighbours.
 */
export function pinTarget(selection, result, clicked = -1) {
  if (selection.length === 2) return { edge: [...selection] };
  if (selection.length !== 1) return null;
  return clicked >= 0 && result && faceName(result.faces[clicked] ?? []) === selection[0] ? { face: clicked } : { face: selection[0] };
}

/** The pins with the kernel's replies (by id) folded in; pins it did not answer for stay as they were. */
export function withStatus(pins, replies) {
  const byId = new Map(replies.map(r => [r.id, r]));
  return pins.map(p => byId.has(p.id) ? { ...p, ...byId.get(p.id) } : p);
}

export const label = ref => ref.edge ? ref.edge.join(' | ') : ref.face;

/** resolve(refs) answers {hash, pins} for the kernel's main slot (the worker's resolve op). */
export function createReferences(store, { box, viewer, resolve }) {
  const pins = () => store.state.pins ?? [];
  let next = 0, clicked = -1;
  // picking.js turns a click into a selection by name; the index says which piece of that name was clicked.
  viewer.canvas.addEventListener('pointerup', e => { clicked = viewer.faceAt(e.clientX, e.clientY); });

  async function pin(target = pinTarget(store.state.selection, store.state.result, clicked)) {
    const { result, regenerating } = store.state;
    if (!target || !result) return null;
    // An index is a face of the mesh on screen, which the kernel's slot holds only while nothing regenerates.
    if (typeof target.face === 'number' && regenerating) target = { face: faceName(result.faces[target.face]) };
    const [reply] = (await resolve([{ id: `p${++next}`, ...target }])).pins;
    store.set({ pins: [...pins(), reply] });
    if (store.state.result !== result) refresh();
    return reply;
  }

  async function refresh() {
    const { result } = store.state;
    if (!pins().length) return;
    const reply = await resolve(pins().map(p => ({ id: p.id, ...p.ref })));
    if (store.state.result !== result || reply.hash !== result.hash) return; // a newer result resolves its own
    const updated = withStatus(pins(), reply.pins);
    const focus = updated.find(p => p.id === store.state.highlight?.pin);
    store.set({ pins: updated, highlight: focus ? { pin: focus.id, result, faces: focus.faces } : null });
  }

  const head = document.createElement('div');
  head.className = 'references-head';
  const button = document.createElement('button');
  button.textContent = 'Pin selection (P)';
  button.onclick = () => pin();
  head.append('Pinned references', button);
  const list = document.createElement('ul');
  box.replaceChildren(head, list);

  function render(s) {
    button.disabled = !s.selection.length;
    list.replaceChildren(...pins().map(p => {
      const li = document.createElement('li');
      li.dataset.pin = p.id;
      li.classList.toggle('lit', s.highlight?.pin === p.id);
      li.title = `${p.faces.length} face${p.faces.length === 1 ? '' : 's'} now; click to light them`;
      const chip = document.createElement('span');
      chip.className = `chip ${p.status}`;
      chip.textContent = p.status;
      const name = document.createElement('code');
      name.textContent = label(p.ref) + (p.faces.length > 1 && !p.ref.edge ? ` ×${p.faces.length}` : '');
      const remove = document.createElement('button');
      remove.textContent = '×';
      remove.title = 'Unpin';
      remove.onclick = e => {
        e.stopPropagation();
        store.set({ pins: pins().filter(q => q !== p), highlight: store.state.highlight?.pin === p.id ? null : store.state.highlight });
      };
      li.onclick = () => store.set({ highlight: store.state.highlight?.pin === p.id ? null : { pin: p.id, result: store.state.result, faces: p.faces } });
      li.append(chip, name, remove);
      return li;
    }));
  }

  store.subscribe((s, before) => {
    if (s.result !== before.result) { clicked = -1; refresh(); }
    if (s.pins !== before.pins || s.highlight !== before.highlight || s.selection !== before.selection) render(s);
  });
  addEventListener('keydown', e => {
    if (e.key.toLowerCase() !== 'p' || e.ctrlKey || e.metaKey || e.altKey || e.target.closest?.('textarea, input, select')) return;
    pin();
  });
  render(store.state);

  return { pin, refresh };
}
