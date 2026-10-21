// The single app state. Pure, so it is tested in Node. Panels and the viewer subscribe; nothing else keeps state that
// outlives a regeneration.
import { recorded } from './lib/history.js';
//
//   text       the document as edited (the result carries its canonical text)
//   result     the last good regeneration (result v2 from adaptResult), or null
//   hash       the hash of the result's canonical text
//   problem    why the last regeneration failed (the document does not read, or an edit did not apply), or null
//   regenerating  a regeneration has been asked for and not come back yet
//   failure    an error of the page or a worker, shown until the next regeneration comes back
//   selection  face names, not mesh indices: names stay stable across regenerations, indices do not
//   history    {text, hash, edits, label, origin} per committed document, oldest first (lib/history.js)
//   historyAt  the index in history of the document shown; undo/redo move it (lib/commit.js)
//   rejected   the last edit list that did not apply: {edits, label, origin, problem, failedEdit?}, or null
//   feature    the key of the selected tree item (lib/features.js: "hole", "components.pin", "parameters"), or null
//   repick     {path, count} while the inspector waits for the face(s) to put in a field, else null
//   preview    while a handle drags (lib/preview.js): the preview regeneration on screen in place of result, with
//              preview: true, the t it was made at and the handle's id; null otherwise

/** The name a document refers to a face by: its first source. */
export const faceName = face => face[0];

/** The regeneration on screen: a drag's preview, else the committed result. Mesh indices are this one's. */
export const shown = state => state.preview ?? state.result;

/** The mesh indices of the faces with these names; a name split into several faces gives them all. */
export function facesNamed(result, names) {
  const wanted = new Set(names);
  const indices = [];
  result?.faces.forEach((face, i) => { if (wanted.has(faceName(face))) indices.push(i); });
  return indices;
}

/** A click on a face (null: on nothing). extend (shift) adds a second face to one already picked: an edge. */
export function pick(selection, name, extend = false) {
  if (name == null) return [];
  return extend && selection.length === 1 && selection[0] !== name ? [selection[0], name] : [name];
}

/** The selected names that still exist in the result. */
export const surviving = (selection, result) => selection.filter(name => result.faces.some(face => faceName(face) === name));

export function createStore(initial = {}) {
  let state = { text: '', result: null, hash: null, problem: null, regenerating: false, failure: null, selection: [], history: [], historyAt: -1, rejected: null, feature: null, repick: null, preview: null, ...initial };
  const listeners = new Set();

  /** Replaces the given fields and tells every listener (state, previous state). */
  function set(patch) {
    const previous = state;
    state = { ...state, ...patch };
    for (const listener of listeners) listener(state, previous);
  }

  return {
    get state() { return state; },
    set,

    /** Returns the unsubscribe function. */
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    /**
     * A regeneration came back. A failure keeps the last good result and selection; a success keeps the selected
     * names that still exist and adds a history entry (its canonical text) when the document changed. intent says
     * what was committed: {origin, edits, label}, or {restore: index} for undo/redo (lib/commit.js); a string is
     * the origin.
     */
    committed(result, intent = {}) {
      if (result.error) {
        set({ problem: result.error, regenerating: false, failure: null });
        return;
      }
      const { origin = 'editor', edits = null, label = null, restore } = typeof intent === 'string' ? { origin: intent } : intent;
      const { text, hash } = result;
      set({
        result, hash, problem: null, regenerating: false, failure: null, selection: surviving(state.selection, result),
        ...recorded(state, { text, hash, edits, label, origin }, restore),
      });
    },
  };
}
