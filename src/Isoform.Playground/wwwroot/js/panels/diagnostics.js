// The status line under the editor and the list of diagnostics.

/** The status line for a state; the browser tests read the "<ms> ms · … faces" form. */
export function statusLine(s, workers) {
  if (s.failure) return `Error: ${s.failure}`;
  if (s.regenerating) return 'Regenerating…';
  if (s.problem) return 'The document does not parse.';
  if (!s.result) return 'Loading the kernel…';
  const { ms, counts: c, faces, bodies } = s.result;
  return `${ms.toFixed(0)} ms · ${workers} worker${workers > 1 ? 's' : ''}, `
    + `${c.meshed} of ${c.meshed + c.reused} regions meshed · ${c.triangles.toLocaleString()} triangles · `
    + `${faces.length} faces, ${c.edges} edges, ${c.vertices} vertices · `
    + (bodies.length === 1 ? '1 body' : `${bodies.length} bodies: ${bodies.map(b => b.name).join(', ')}`);
}

export function createDiagnostics(store, { stats, list, workers }) {
  store.subscribe((s, before) => {
    stats.textContent = statusLine(s, workers);
    if (s.result === before.result) return;
    list.replaceChildren(...s.result.diagnostics.map(d => {
      const li = document.createElement('li');
      if (d.code === 'invalid-parameter') li.className = 'error';
      const b = document.createElement('b');
      b.textContent = d.code;
      li.append(b, d.feature ? ` · ${d.feature}: ` : ': ', d.message);
      return li;
    }));
  });
}
