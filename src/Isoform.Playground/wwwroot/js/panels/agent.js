// The agent view: what every commit did to the document, the way an agent sees it. The edit list as JSON, a line
// diff of the canonical text, and the hash before and after. Undo and restore show the edits that lead back, from
// the kernel's diff; an edit list that did not apply shows with its problem.
import { hunks, lineDiff } from '../lib/diff.js';
import { move, short } from '../lib/history.js';

const el = (tag, className, text) => {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text != null) e.textContent = text;
  return e;
};

/** An edit list one edit per line; the failed one marked. */
function editList(edits, failed) {
  if (!edits) return [el('span', 'muted', 'diffing…')];
  if (!edits.length) return [el('span', 'muted', '[]')];
  return ['[\n', ...edits.flatMap((e, i) => [el('span', i === failed ? 'bad' : null, '  ' + JSON.stringify(e)), i < edits.length - 1 ? ',\n' : '\n']), ']'];
}

function textDiff(before, after) {
  return hunks(lineDiff(before, after)).map(o =>
    o ? el('div', o.op === '+' ? 'add' : o.op === '-' ? 'del' : null, `${o.op} ${o.line}`) : el('div', 'skip', '⋯'));
}

/** root: an empty element. diff(before, after) resolves to the edit list between two canonical texts. */
export function createAgent(store, root, { diff }) {
  root.classList.add('agent');
  let shown = null; // {from, to, kind, at}: the change on display
  let asked = 0;    // the diff asked for last; older replies are dropped

  function render({ kind, label, from, to, edits, failed, problem }) {
    const head = el('div', 'agent-head');
    head.append(el('b', null, 'Agent view'), el('span', 'muted', `${kind}${label ? ' · ' + label : ''}`));
    const hash = el('code', 'agent-hash', to ? `hash ${short(from?.hash)} → ${short(to.hash)}` : `hash ${short(from?.hash)} unchanged`);
    const parts = [head, hash, el('div', 'agent-label', 'edits'), el('pre', 'agent-edits')];
    parts[3].append(...editList(edits, failed));
    if (problem) parts.push(el('div', 'agent-problem', problem));
    if (from && to) {
      const lines = el('pre', 'agent-diff');
      lines.append(...textDiff(from.text, to.text));
      parts.push(el('div', 'agent-label', 'text diff'), lines);
    }
    root.replaceChildren(...parts);
    root.dataset.hash = to?.hash ?? from?.hash ?? '';
  }

  store.subscribe((s, before) => {
    if (s.rejected && s.rejected !== before.rejected) {
      const { edits, label, origin, problem, failedEdit } = s.rejected;
      render({ kind: `rejected (${origin})`, label, from: s.history[s.historyAt], edits, failed: failedEdit, problem });
      return;
    }
    const to = s.history[s.historyAt];
    if (!to || to === shown?.to) return;
    if (to.hash !== shown?.to.hash) {
      // A new entry, or a step through the history.
      const fresh = before.history[s.historyAt]?.hash !== to.hash;
      const kind = fresh ? to.origin : move(shown?.at ?? -1, s.historyAt);
      shown = { from: shown?.to ?? null, to, kind, at: s.historyAt };
    } else {
      shown = { ...shown, to }; // the same entry, its edits filled in
    }
    const { from, kind } = shown;
    const view = { kind, label: to.label, from, to };
    // Forward one step the entry's own edits are the change; any other move asks the kernel for the way back.
    if (!from || (kind !== 'undo' && kind !== 'restore')) return render({ ...view, edits: from ? to.edits : [] });
    render({ ...view, edits: null });
    const ask = ++asked;
    diff(from.text, to.text).then(edits => { if (ask === asked && shown?.to === to) render({ ...view, edits }); });
  });
}
