// The document editor and the example picker. Typing regenerates after a pause; the text is kept in local storage.

// Like the canonical text, but with arrays of plain values on one line: easier to read and edit.
export function pretty(value, indent = '') {
  const inner = indent + '  ';
  if (Array.isArray(value)) {
    if (value.every(v => v === null || typeof v !== 'object')) return JSON.stringify(value).replaceAll(',', ', ');
    return '[\n' + value.map(v => inner + pretty(v, inner)).join(',\n') + '\n' + indent + ']';
  }
  if (value && typeof value === 'object')
    return '{\n' + Object.entries(value).map(([k, v]) => inner + JSON.stringify(k) + ': ' + pretty(v, inner)).join(',\n') + '\n' + indent + '}';
  return JSON.stringify(value);
}

const saved = 'isoform.document';

export function createEditor(store, { editor, parse, examplesBox, regenerate }) {
  let timer = 0;
  const schedule = () => {
    clearTimeout(timer);
    timer = setTimeout(regenerate, 300);
  };
  const typed = () => {
    store.set({ text: editor.value });
    schedule();
  };

  editor.addEventListener('input', typed);
  editor.addEventListener('keydown', e => {
    if (e.key !== 'Tab') return;
    e.preventDefault();
    editor.setRangeText('  ', editor.selectionStart, editor.selectionEnd, 'end');
    typed();
  });

  store.subscribe((s, before) => {
    if (s.text !== editor.value) editor.value = s.text; // set from elsewhere: an example, later an edit or undo
    if (s.problem !== before.problem) {
      editor.classList.toggle('bad', !!s.problem);
      parse.textContent = s.problem ?? '';
    }
    if (s.result !== before.result) try { localStorage.setItem(saved, s.text); } catch { }
  });

  return {
    /** Fills the picker and opens ?example=N, else the last document edited here, else the first example. */
    load(examplesText) {
      const examples = Object.fromEntries(Object.entries(examplesText).map(([k, v]) => [k, pretty(JSON.parse(v))]));
      for (const name of Object.keys(examples)) examplesBox.add(new Option(name, name));
      examplesBox.addEventListener('change', () => {
        if (!examplesBox.value) return;
        store.set({ text: examples[examplesBox.value], selection: [] });
        regenerate({ fit: true });
      });

      const asked = Object.keys(examples)[+new URLSearchParams(location.search).get('example')];
      let last = null;
      try { last = localStorage.getItem(saved); } catch { }
      if (asked) {
        examplesBox.value = asked;
        store.set({ text: examples[asked] });
      } else if (last) {
        examplesBox.add(new Option('(your last document)', ''), 0);
        examplesBox.value = '';
        store.set({ text: last });
      } else {
        store.set({ text: Object.values(examples)[0] });
      }
    },
  };
}
