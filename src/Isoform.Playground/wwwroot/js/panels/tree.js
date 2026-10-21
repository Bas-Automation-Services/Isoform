// The feature tree: the document's features in order (by body when there are several; components and mates for an
// assembly), each with its type and a badge for the diagnostics that name it. Clicking selects the item (its key in
// the store's `feature`) and tints its faces; hovering tints them too. Picking a face selects the item that made it.
import { describe, diagnosticsOf, documentOf, facesOf, itemsOf, keyOfFace, treeOf } from '../lib/features.js';
import { shown } from '../store.js';

const ICONS = {
  block: '▭', hole: '◯', 'plane-cut': '◪', fillet: '◜', chamfer: '◸', shell: '▢', extrude: '⇡', revolve: '↻',
  offset: '⇲', pattern: '⁘', combine: '⊕', component: '◫', mate: '⋈', parameters: 'ƒ', sketch: '✎',
};

/** open(item): a double-click on an item (sketch mode edits a sketch). */
export function createTree(store, viewer, list, { open } = {}) {
  let doc = null, items = [], hovered = null;
  const item = key => items.find(i => i.key === key) ?? null;

  function tint() {
    const keys = new Set([store.state.feature, hovered]);
    // Faces of the mesh on screen: a drag's preview while there is one (J2).
    viewer.tint([...keys].flatMap(k => facesOf(shown(store.state), item(k))));
  }

  function mark() {
    for (const li of list.querySelectorAll('li[data-key]')) {
      li.classList.toggle('selected', li.dataset.key === store.state.feature);
      li.classList.toggle('hovered', li.dataset.key === hovered);
    }
    list.querySelector('li.selected')?.scrollIntoView({ block: 'nearest' });
  }

  function row(it, result) {
    const li = document.createElement('li');
    li.dataset.key = it.key;
    const icon = Object.assign(document.createElement('span'), { className: 'icon', textContent: ICONS[it.type] ?? '·' });
    const name = Object.assign(document.createElement('span'), { className: 'name', textContent: it.id });
    const type = Object.assign(document.createElement('span'), { className: 'type', textContent: describe(it) });
    li.append(icon, name, type);
    const found = diagnosticsOf(result, it);
    if (found.length) {
      const badge = Object.assign(document.createElement('span'), { className: 'badge', textContent: found.length });
      if (found.some(d => d.code === 'invalid-parameter')) badge.classList.add('error');
      badge.title = found.map(d => `${d.code}: ${d.message}`).join('\n');
      li.append(badge);
    }
    li.onclick = () => store.set({ feature: store.state.feature === it.key ? null : it.key, selection: [] });
    li.ondblclick = () => open?.(it);
    li.onmouseenter = () => { hovered = it.key; mark(); tint(); };
    li.onmouseleave = () => { hovered = null; mark(); tint(); };
    return li;
  }

  function render(result) {
    doc = documentOf(result) ?? doc;
    const tree = treeOf(doc);
    items = itemsOf(tree);
    list.replaceChildren(...tree.flatMap(g => [
      ...(g.title ? [Object.assign(document.createElement('li'), { className: 'group', textContent: g.title })] : []),
      ...g.items.map(it => row(it, result)),
    ]));
    if (hovered && !item(hovered)) hovered = null;
  }

  // Subscribed after the viewer, so the tint lands on the mesh on screen. Sets made from here wait a microtask, so
  // every listener has seen this change first.
  store.subscribe((s, before) => {
    if (s.result !== before.result) {
      render(s.result);
      if (s.feature && !item(s.feature)) queueMicrotask(() => store.set({ feature: null })); // gone, like a face
    }
    // A face picked in the view (not a regeneration renaming the selection, not a re-pick): its item.
    if (s.selection !== before.selection && s.result === before.result && s.feature === before.feature && !s.repick && !before.repick) {
      const key = s.selection.length ? keyOfFace(doc, s.selection[0]) : null;
      if (key !== s.feature) queueMicrotask(() => store.set({ feature: item(key) ? key : null }));
    }
    if (shown(s) !== shown(before) || s.feature !== before.feature) {
      mark();
      tint();
    }
  });
}
