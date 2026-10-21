// The status line under the editor, the diagnostics grouped by code, and the same diagnostics in the 3D view: the
// verdict badge, a thickness label at each thin spot, and for the diagnostic in focus its faces in colours of their
// own with the camera flown to it. Focus is this panel's own: a diagnostic, kept across a regeneration while it is
// still reported, dropped when the selection changes.
import { diagnose, featureFaces, groups, mm, refind, verdict } from '../lib/diagnose.js';
import { shown } from '../store.js';

/** The status line for a state; the browser tests read the "<ms> ms · … faces" form. */
export function statusLine(s, workers) {
  if (s.failure) return `Error: ${s.failure}`;
  if (s.regenerating) return 'Regenerating…';
  if (s.problem) return 'The document does not parse.';
  if (!s.result) return 'Loading the kernel…';
  const { ms, meshed, reused, triangles, faces, edges, vertices, bodies } = s.result;
  return `${ms.toFixed(0)} ms · ${workers} worker${workers > 1 ? 's' : ''}, `
    + `${meshed} of ${meshed + reused} regions meshed · ${triangles.toLocaleString()} triangles · `
    + `${faces.length} faces, ${edges} edges, ${vertices} vertices · `
    + (bodies.length === 1 ? '1 body' : `${bodies.length} bodies: ${bodies.map(b => b.name).join(', ')}`);
}

/** The two faces of a thin spot or edge, then any further ones; a feature's own faces get the tint. */
const pairColors = ['#ff8a1f', '#7b5cff', '#1fb5a8', '#e0457b'];
const tint = '#f2c94c';

const el = (tag, className, ...children) => {
  const e = document.createElement(tag);
  if (className) e.className = className;
  e.append(...children);
  return e;
};

/** The mesh indices of the faces carrying a source name (any of their sources, not only the first). */
const facesWith = (result, name) => result.faces.flatMap((sources, i) => (sources.includes(name) ? [i] : []));

/**
 * view: the 3D view's element (labels and the verdict go on it); defaultCell: the page's cell size, offered as the way
 * back; resolve(cell): regenerates the document at that cell size, keeping the view.
 */
export function createDiagnostics(store, { stats, list, workers, viewer, view, badge, defaultCell, resolve }) {
  let items = [], focus = null, whole = false; // whole: the focused diagnostic's feature, not only its faces
  const labels = el('div', 'spot-labels');
  view.append(labels);

  /** The faces the focused diagnostic lights, as [face, colour]: the faces it names, else (or whole) its feature's too. */
  function marksOf(item, result) {
    if (!item) return [];
    const named = item.faces.flatMap((name, k) => facesWith(result, name).map(f => [f, pairColors[k % pairColors.length]]));
    return named.length && !whole ? named : [...featureFaces(result, item.feature).map(f => [f, tint]), ...named];
  }

  function show({ fly = false } = {}) {
    const result = shown(store.state);
    const marks = marksOf(focus, result);
    viewer.mark(marks);
    if (fly && focus && (marks.length || focus.spot)) viewer.flyTo(marks.map(([f]) => f), focus.spot?.at);
    for (const li of list.querySelectorAll('li.item')) li.classList.toggle('focused', +li.dataset.index === focus?.index);
    for (const label of labels.children) label.classList.toggle('focused', +label.dataset.index === focus?.index);
  }

  function focusOn(item, feature = false) {
    if (item?.index === focus?.index && feature === whole) return;
    [focus, whole] = [item, feature];
    show({ fly: !!item });
  }
  const toggle = (item, feature = false) => focusOn(item.index === focus?.index && feature === whole ? null : item, feature);

  function itemOf(item) {
    const li = el('li', `item${item.stale ? ' stale' : ''}`);
    li.dataset.index = item.index;
    li.title = 'Show it in the view';
    if (item.feature) {
      const chip = el('button', 'feature', item.feature);
      chip.title = `Show ${item.feature} in the view`;
      chip.onclick = e => { e.stopPropagation(); toggle(item, true); };
      li.append(chip, ' ');
    }
    li.append(item.message);
    if (item.faces.length) {
      li.append(el('div', 'faces', ...item.faces.map((name, k) => {
        const swatch = el('i');
        swatch.style.background = pairColors[k % pairColors.length];
        return el('span', null, swatch, name);
      })));
    }
    if (item.stale) li.append(el('div', 'muted', 'preview: coarse cells report thin walls that may not be there'));
    if (item.resolve) {
      const button = el('button', 'resolve', `Resolve at ${mm(item.resolve)}`);
      button.title = `Regenerate at ${mm(item.resolve)} cells: walls of ${mm(2 * item.resolve)} and more are resolved`;
      button.onclick = e => { e.stopPropagation(); resolve(item.resolve); };
      li.append(' ', button);
    }
    li.onclick = () => toggle(item);
    return li;
  }

  // A drag's preview is shown in place of the committed result (store.shown), with its own diagnostics.
  function render(s) {
    const result = shown(s);
    items = diagnose(result);
    focus = refind(items, focus);
    if (!focus) whole = false;
    const committed = s.result.cell; // a preview's coarser cell is not the one to go back from
    const cell = committed && committed !== defaultCell
      ? [el('li', 'cell', `Cells ${mm(committed)} `, Object.assign(el('button', 'resolve', `back to ${mm(defaultCell)}`), { onclick: () => resolve(defaultCell) }))]
      : [];
    list.replaceChildren(...cell, ...groups(items).map(g => el('li', `group ${g.error ? 'error' : 'warn'}`,
      el('div', 'group-head', el('b', null, g.title), el('span', 'muted', ` ${g.code} · ${g.items.length}`)),
      el('ul', null, ...g.items.map(itemOf)))));

    // A label at each thin spot that counts, on the view; positioned every frame below.
    labels.replaceChildren(...items.filter(i => i.spot && !i.stale).map(item => {
      const label = el('button', 'spot-label', mm(item.spot.thickness));
      label.dataset.index = item.index;
      label.title = item.message;
      label.onclick = () => toggle(item);
      return label;
    }));
    show();
  }

  function renderBadge(s) {
    s = { ...s, result: shown(s) };
    const v = verdict(s);
    badge.hidden = !v;
    if (!v) return;
    badge.className = `verdict ${v.kind}`;
    badge.replaceChildren(el('i'), el('b', null, v.text), el('span', 'muted', v.detail ? ` · ${v.detail}` : ''),
      ...(s.result?.preview ? [el('span', 'preview', `preview · ${s.result.ms.toFixed(0)} ms`)] : []));
  }
  // The badge brings the first problem into focus.
  badge.onclick = () => focusOn(items.find(i => !i.stale) ?? null);

  store.subscribe((s, before) => {
    stats.textContent = statusLine(s, workers);
    if (shown(s) !== shown(before) || s.problem !== before.problem) renderBadge(s);
    if (shown(s) !== shown(before)) render(s);
    else if (s.selection !== before.selection && focus) focusOn(null);
  });

  // The labels follow the camera.
  const place = () => {
    for (const label of labels.children) {
      const at = viewer.toScreen(items[+label.dataset.index].spot.at);
      label.hidden = !at;
      if (at) label.style.transform = `translate(${at[0]}px, ${at[1]}px)`;
    }
    requestAnimationFrame(place);
  };
  requestAnimationFrame(place);

  return {
    /** The diagnostics as shown, plain data. */
    items: () => items.map(({ index, code, feature, faces, size, stale, resolve, spot }) => ({ index, code, feature, faces, size, stale, resolve, thickness: spot?.thickness ?? null })),
    /** The diagnostic in focus (its index), or null. */
    focused: () => focus?.index ?? null,
    /** Focuses a diagnostic by index (null: none), flying to it; feature: its whole feature. */
    focus: (index, feature = false) => focusOn(items[index] ?? null, feature),
  };
}
