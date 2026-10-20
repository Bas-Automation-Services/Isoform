// The single app state. Pure, so it is tested in Node. Panels and the viewer subscribe; nothing else keeps state that
// outlives a regeneration.
//
//   text       the document as edited (the result carries its canonical text)
//   result     the last good regeneration (result v2 from adaptResult), or null
//   hash       the hash of the result's canonical text
//   problem    why the last regeneration failed (the document does not read, or an edit did not apply), or null
//   regenerating  a regeneration has been asked for and not come back yet
//   failure    an error of the page or a worker, shown until the next regeneration comes back
//   selection  face names, not mesh indices: names stay stable across regenerations, indices do not
//   history    {text, hash, origin} per committed document, oldest first: the hook for undo/redo

/** The name a document refers to a face by: its first source. */
export const faceName = face => face[0];

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
  let state = { text: '', result: null, hash: null, problem: null, regenerating: false, failure: null, selection: [], history: [], ...initial };
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
     * names that still exist and adds a history entry (its canonical text) when the document changed.
     */
    committed(result, origin = 'editor') {
      if (result.error) {
        set({ problem: result.error, regenerating: false, failure: null });
        return;
      }
      const { text, hash } = result;
      const history = state.history.at(-1)?.hash === hash ? state.history : [...state.history, { text, hash, origin }];
      set({ result, hash, problem: null, regenerating: false, failure: null, selection: surviving(state.selection, result), history });
    },
  };
}
