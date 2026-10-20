// Committing changes to the document: the one way panels change it besides typing, and undo/redo over its history.
//
//   const commits = createCommits(store, { call, regenerate, flash });
//
//   await commits.applyEdits(edits, label, origin)
//       edits   one edit or an array of them, as DocumentEdits reads them: [{"set": "plate.size.0", "value": 120}]
//       label   what the user did, shown in the history list ("drag plate/x+"); defaults to a summary of the edits
//       origin  who did it: 'handle', 'inspector', 'tour', …
//     Applies the edits to the committed document (worker op `apply`). If they do not apply, nothing is committed:
//     store.problem says why and store.rejected holds {edits, label, origin, problem, failedEdit?} for the agent
//     panel. Otherwise the editor gets the new text, it regenerates, and one history entry is pushed. Resolves to
//     the regenerated result (adaptResult), {error, failedEdit?} when the edits did not apply, or null when a
//     newer regeneration overtook it.
//
//   commits.undo(), commits.redo(), commits.restore(index)
//     Regenerate an entry of store.history; historyAt moves to it and no entry is added. Resolve like applyEdits,
//     or to null when there is nothing to undo or redo.
//
//   commits.diff(before, after)   the edit list between two canonical texts (worker op `diff`), cached; null if
//                                 one does not read
//
// Typed changes are committed by the editor's own regeneration; their entry gets its edits from diff afterwards.
// Everything but typing flashes the changed lines in the editor.
//
// Dependencies, so Node tests drive it with fakes: call(op, args) is kernelClient.call; regenerate(intent) regenerates
// store.text, commits with store.committed(result, intent) and resolves to the result or null (main.js);
// flash(before, after) lights the lines of the editor text that changed.
import { pretty } from '../panels/editor.js';
import { describe } from './history.js';

/** The editor's form of a canonical text. */
const shown = text => pretty(JSON.parse(text));

export function createCommits(store, { call, regenerate, flash = () => { } }) {
  const diffs = new Map(); // before + '\0' + after → edits or null
  let aim = null;          // the history index an undo/redo in flight goes to, so repeated presses keep stepping

  function diff(before, after) {
    const key = before + '\0' + after;
    if (!diffs.has(key)) diffs.set(key, call('diff', { before, after }).then(({ result }) => result.edits ?? null, () => null));
    return diffs.get(key);
  }

  // Sets the editor text, regenerates and flashes what changed in the editor if it was committed.
  async function commit(text, intent) {
    const before = store.state.text;
    store.set({ text: shown(text) });
    const result = await regenerate(intent);
    if (result && !result.error) flash(before, store.state.text);
    return result;
  }

  async function applyEdits(edits, label, origin = 'edits') {
    edits = [edits].flat();
    label ??= describe(edits);
    // The committed document, not the editor's: handles and the inspector were made from it.
    const document = store.state.result?.text ?? store.state.text;
    const { result: applied } = await call('apply', { document, edits });
    if (applied.problem) {
      const { problem, failedEdit } = applied;
      store.set({ problem, rejected: { edits, label, origin, problem, failedEdit } });
      return { error: problem, failedEdit };
    }
    store.set({ rejected: null });
    return commit(applied.text, { origin, edits, label });
  }

  async function restore(index) {
    const entry = store.state.history[index];
    if (!entry) return null;
    aim = index;
    const result = await commit(entry.text, { restore: index });
    if (aim === index) aim = null;
    return result;
  }

  const at = () => aim ?? store.state.historyAt;

  // A typed entry learns its edits from the one before it; the first entry is the document as opened.
  const filling = new WeakSet();
  store.subscribe(s => {
    s.history.forEach((entry, i) => {
      if (entry.edits || filling.has(entry)) return;
      filling.add(entry);
      (i ? diff(s.history[i - 1].text, entry.text) : Promise.resolve([])).then(edits => {
        const filled = { ...entry, edits, label: entry.label ?? (i ? (edits ? describe(edits) : 'replaced the document') : 'opened') };
        filling.add(filled); // edits stay null when the diff failed: do not ask again
        store.set({ history: store.state.history.map(e => e === entry ? filled : e) });
      });
    });
  });

  return {
    applyEdits,
    restore,
    undo: () => at() > 0 ? restore(at() - 1) : Promise.resolve(null),
    redo: () => at() < store.state.history.length - 1 ? restore(at() + 1) : Promise.resolve(null),
    diff,
  };
}
