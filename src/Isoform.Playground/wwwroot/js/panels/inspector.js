// The inspector: the selected item's fields, generated from its JSON (lib/features.js fieldsOf). Numbers scrub by
// dragging their label sideways or take a typed value ("=expr" makes an expression); op and mate kind are selects;
// face names are chips with a re-pick that takes the next face(s) clicked in the view. Every change is an edit list
// [{set: path, value}] handed to commit(edits, label), the one way out.
import { describe, documentOf, fieldsOf, itemsOf, parseScalar, scrubbed, setEdit, treeOf } from '../lib/features.js';

const el = (tag, props = {}, ...children) => {
  const e = Object.assign(document.createElement(tag), props);
  e.append(...children);
  return e;
};
const show = v => typeof v === 'number' ? String(v) : v;

export function createInspector(store, box, commit) {
  let shown = null; // what is on screen: key + JSON + re-pick, so a regeneration that changes nothing keeps the inputs

  const set = (path, value) => commit([setEdit(path, value)], `${path} = ${JSON.stringify(value)}`);

  // A typed scalar: committed on change (Enter or leaving the field) when it means something new.
  function input(f) {
    const i = el('input', { className: 'value', value: show(f.value), spellcheck: false });
    i.dataset.path = f.path;
    i.onchange = () => {
      const v = parseScalar(i.value);
      if (v === undefined || v === f.value) i.value = show(f.value);
      else set(f.path, f.integer && typeof v === 'number' ? Math.round(v) : v);
    };
    return i;
  }

  // A label that scrubs its number: drag sideways, shift for faster; one commit on release.
  function scrub(f, text, target) {
    const label = el('span', { className: 'scrub', textContent: text, title: f.path });
    if (typeof f.value !== 'number') return label;
    label.classList.add('live');
    label.dataset.scrub = f.path;
    label.onpointerdown = down => {
      down.preventDefault();
      label.setPointerCapture(down.pointerId);
      let value = f.value;
      label.onpointermove = e => {
        value = scrubbed(f.value, e.clientX - down.clientX, { integer: f.integer, fast: e.shiftKey });
        target.value = show(value);
      };
      label.onpointerup = label.onpointercancel = () => {
        label.onpointermove = label.onpointerup = label.onpointercancel = null;
        if (value !== f.value) set(f.path, value);
      };
    };
    return label;
  }

  function scalarRow(f) {
    const i = input(f);
    return el('div', { className: 'field' }, scrub(f, f.label, i), i);
  }

  function faces(f) {
    const names = f.count === 2 ? f.value : [f.value];
    const active = store.state.repick?.path === f.path;
    const button = el('button', { className: `repick${active ? ' active' : ''}`, textContent: active ? 'cancel' : 're-pick' });
    button.title = f.count === 2 ? 'click a face, shift+click the second (or click a line)' : 'click a face';
    button.onclick = () => store.set({ repick: active ? null : { path: f.path, count: f.count }, selection: [] });
    const chips = el('span', { className: 'chips' }, ...names.map(n => el('code', { className: 'chip', textContent: n })));
    const row = el('div', { className: 'field' }, el('span', { className: 'scrub', textContent: f.label }), chips, button);
    if (active) row.append(el('div', { className: 'repick-hint', textContent: button.title }));
    return row;
  }

  function parameters(f) {
    const rows = f.entries.map(p => {
      const i = input(p);
      const remove = el('button', { className: 'remove', textContent: '×', title: `remove ${p.label}` });
      remove.onclick = () => commit([{ remove: p.path }], `remove ${p.path}`);
      return el('div', { className: 'field' }, scrub(p, p.label, i), i, remove);
    });
    const name = el('input', { className: 'name', placeholder: 'name', spellcheck: false });
    const value = el('input', { className: 'value', placeholder: 'value or =expression', spellcheck: false });
    const add = el('button', { className: 'add', textContent: 'add' });
    add.onclick = () => {
      const v = parseScalar(value.value);
      if (/^[A-Za-z_]\w*$/.test(name.value) && v !== undefined) set(`parameters.${name.value}`, v);
    };
    return el('div', { className: 'parameters' }, ...rows, el('div', { className: 'field new' }, name, value, add));
  }

  function field(f) {
    switch (f.kind) {
      case 'scalar': return scalarRow(f);
      case 'vector': {
        const entries = f.entries.map(e => { const i = input(e); return el('span', { className: 'entry' }, scrub(e, e.label, i), i); });
        return el('div', { className: 'field' }, el('span', { className: 'scrub', textContent: f.label }), el('span', { className: 'entries' }, ...entries));
      }
      case 'choice': {
        const s = el('select', {}, ...f.options.map(o => new Option(o, o, false, o === f.value)));
        s.dataset.path = f.path;
        s.onchange = () => set(f.path, s.value);
        return el('div', { className: 'field' }, el('span', { className: 'scrub', textContent: f.label }), s);
      }
      case 'bool': {
        const c = el('input', { type: 'checkbox', checked: f.value });
        c.dataset.path = f.path;
        c.onchange = () => set(f.path, c.checked);
        return el('div', { className: 'field' }, el('span', { className: 'scrub', textContent: f.label }), c);
      }
      case 'text': {
        const t = el('input', { className: 'value', value: f.list ? f.value.join(', ') : f.value, spellcheck: false });
        t.dataset.path = f.path;
        t.onchange = () => set(f.path, f.list ? t.value.split(',').map(s => s.trim()).filter(Boolean) : t.value.trim());
        return el('div', { className: 'field' }, el('span', { className: 'scrub', textContent: f.label }), t);
      }
      case 'faces': return faces(f);
      case 'group': return el('fieldset', {}, el('legend', { textContent: f.label }), ...f.fields.map(field));
      case 'parameters': return parameters(f);
    }
  }

  function render(s) {
    const it = s.feature ? itemsOf(treeOf(documentOf(s.result))).find(i => i.key === s.feature) : null;
    const now = it ? `${it.key}\n${JSON.stringify(it.json)}\n${s.repick?.path}` : null;
    if (now === shown) return;
    shown = now;
    const focused = box.contains(document.activeElement) ? document.activeElement.dataset.path : null;
    if (!it) {
      box.replaceChildren(el('p', { className: 'muted', textContent: 'Select a feature in the tree, or a face in the view.' }));
      return;
    }
    const title = el('div', { className: 'inspector-head' }, el('b', { textContent: it.id }),
      el('span', { className: 'muted', textContent: describe(it) }));
    box.replaceChildren(title, ...fieldsOf(it).map(field));
    if (focused) box.querySelector(`[data-path="${CSS.escape(focused)}"]`)?.focus();
  }

  store.subscribe((s, before) => {
    // A re-pick takes the face(s) the user clicks next; a regeneration renaming the selection does not count.
    const r = s.repick;
    if (r && s.selection !== before.selection && s.result === before.result && s.selection.length >= r.count) {
      queueMicrotask(() => {
        store.set({ repick: null });
        set(r.path, r.count === 2 ? s.selection.slice(0, 2) : s.selection[0]);
      });
    }
    if (s.feature !== before.feature && r) queueMicrotask(() => store.set({ repick: null }));
    if (s.feature !== before.feature || s.result !== before.result || s.repick !== before.repick) render(s);
  });
  addEventListener('keydown', e => { if (e.key === 'Escape' && store.state.repick) store.set({ repick: null }); });
  render(store.state);
}
