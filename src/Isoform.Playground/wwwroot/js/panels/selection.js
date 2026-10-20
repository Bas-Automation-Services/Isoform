// The selected face or edge as the document refers to it, ready to copy into a feature.
import { facesNamed } from '../store.js';

export function createSelection(store, box) {
  store.subscribe((s, before) => {
    if (s.result === before.result && s.selection === before.selection) return;
    const { result, selection } = s;
    if (!result || selection.length === 0) { box.hidden = true; return; }
    const text = JSON.stringify(selection.length === 2 ? selection : selection[0]);
    const label = selection.length === 2 ? 'Edge' : `Face (${result.faces[facesNamed(result, selection)[0]].names.join(', ')})`;
    box.replaceChildren(`${label}: `);
    const code = document.createElement('code');
    code.textContent = text;
    const copy = document.createElement('button');
    copy.textContent = 'copy';
    copy.onclick = () => navigator.clipboard?.writeText(text);
    box.append(code, copy);
    box.hidden = false;
  });
}
