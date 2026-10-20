// The document history as a git log, newest first: hash, label, origin. A click restores an entry; Undo and Redo
// (Ctrl+Z, Ctrl+Shift+Z or Ctrl+Y) step through it. Entries past the shown one are what redo would bring back.
import { short } from '../lib/history.js';

/** root: an empty element. commits: createCommits(…) (lib/commit.js). */
export function createHistory(store, root, commits) {
  root.classList.add('history');
  const head = document.createElement('div');
  head.className = 'history-head';
  const title = document.createElement('b');
  title.textContent = 'History';
  const button = (text, act) => {
    const b = document.createElement('button');
    b.textContent = text;
    b.addEventListener('click', act);
    return b;
  };
  const undo = button('Undo', () => commits.undo()), redo = button('Redo', () => commits.redo());
  undo.title = 'Ctrl+Z';
  redo.title = 'Ctrl+Shift+Z';
  undo.disabled = redo.disabled = true;
  head.append(title, undo, redo);
  const list = document.createElement('ol');
  root.append(head, list);

  // The document history is the undo, in the editor too: its own undo would not survive the next commit anyway.
  // Other inputs (the inspector's numbers) keep theirs.
  addEventListener('keydown', e => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.target.tagName === 'INPUT') return;
    const key = e.key.toLowerCase();
    if (key === 'z' && !e.shiftKey) commits.undo();
    else if ((key === 'z' && e.shiftKey) || key === 'y') commits.redo();
    else return;
    e.preventDefault();
  });

  store.subscribe((s, before) => {
    if (s.history === before.history && s.historyAt === before.historyAt) return;
    undo.disabled = s.historyAt <= 0;
    redo.disabled = s.historyAt >= s.history.length - 1;
    list.replaceChildren(...s.history.map((entry, i) => {
      const li = document.createElement('li');
      li.className = i === s.historyAt ? 'current' : i > s.historyAt ? 'ahead' : '';
      const hash = document.createElement('code');
      hash.textContent = short(entry.hash);
      const label = document.createElement('span');
      label.textContent = entry.label ?? '…';
      const origin = document.createElement('span');
      origin.className = 'muted';
      origin.textContent = entry.origin;
      li.append(hash, label, origin);
      li.title = entry.edits ? JSON.stringify(entry.edits) : '';
      li.addEventListener('click', () => commits.restore(i));
      return li;
    }).reverse());
  });
}
