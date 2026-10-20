// Document history, pure: a list of entries {text, hash, edits, label, origin}, oldest first, and the index of the one
// shown (historyAt, -1 while empty). Undo and redo move the index; a new commit drops what lies after it.
//
//   text    canonical text of the document        hash    its SHA-256
//   edits   the edit list from the entry before (null until it is known: a typed change is diffed afterwards)
//   label   what the user did, for the history list    origin  who did it: 'editor' (typing), 'handle', …

/**
 * The history after a regeneration of entry came back. restore is the index an undo, redo or click aimed at: it
 * only moves historyAt. Regenerating the shown document again (another cell size) changes nothing.
 */
export function recorded({ history, historyAt }, entry, restore) {
  if (restore != null && history[restore]?.hash === entry.hash) return { history, historyAt: restore };
  if (history[historyAt]?.hash === entry.hash) return { history, historyAt };
  const kept = history.slice(0, historyAt + 1);
  return { history: [...kept, entry], historyAt: kept.length };
}

/** A short label for an edit list: its first edit and how many more. */
export function describe(edits) {
  if (!edits?.length) return 'no change';
  const [e] = edits;
  const op = Object.keys(e).find(k => k !== 'value') ?? 'edit';
  const first = `${op} ${e[op]}` + ('value' in e ? ` = ${JSON.stringify(e.value)}` : '');
  return edits.length > 1 ? `${first} (+${edits.length - 1})` : first;
}

/** A hash as the panels show it. */
export const short = hash => hash ? hash.slice(0, 8) + '…' : '∅';

/** How moving from entry `from` to entry `to` reads in the agent panel. */
export const move = (from, to) => to === from - 1 ? 'undo' : to === from + 1 ? 'redo' : 'restore';
