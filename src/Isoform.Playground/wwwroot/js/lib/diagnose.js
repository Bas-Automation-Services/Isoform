// Diagnostics as the page shows them: grouped by code under a readable title, linked to the faces and thin spots
// they name, with the cell size that would resolve a below-resolution one. Pure, so it is tested in Node.

/** Codes (src/Isoform.Modeling/Model.cs) in the order their groups are listed, errors first. */
export const titles = {
  'invalid-parameter': 'Invalid parameter',
  'fillet-invalid': 'Fillet does not fit',
  'chamfer-invalid': 'Chamfer does not fit',
  'unsupported-fillet': 'Fillet not supported here',
  'unsupported-chamfer': 'Chamfer not supported here',
  'backend-failed': 'Kernel failure',
  'part-missing': 'Part missing',
  'mates-conflict': 'Mates conflict',
  'tangent-contact': 'Tangent contact',
  'below-resolution': 'Below resolution',
  'no-effect': 'No effect',
  'multiple-bodies': 'Several bodies',
  'part-changed': 'Part changed',
};

// A feature that failed or input that is wrong; the rest say something about geometry that was made.
const errors = new Set(['invalid-parameter', 'fillet-invalid', 'chamfer-invalid', 'unsupported-fillet', 'unsupported-chamfer',
  'backend-failed', 'part-missing', 'mates-conflict']);

export const title = code => titles[code] ?? code[0].toUpperCase() + code.slice(1).replaceAll('-', ' ');

const nameChar = /[\w/+\-]/;

/** The names (of faces) a message mentions, in the order it mentions them; "plate/x" is not found in "plate/x+". */
export function namesIn(message, names) {
  const found = [];
  for (const name of names) {
    for (let at = message.indexOf(name); at >= 0; at = message.indexOf(name, at + 1)) {
      if ((at > 0 && nameChar.test(message[at - 1])) || nameChar.test(message[at + name.length] ?? '')) continue;
      found.push([at, name]);
      break;
    }
  }
  return found.sort((a, b) => a[0] - b[0]).map(([, name]) => name);
}

/** Whether a source name belongs to a feature: "hole/wall" to hole, and in a bundle "lower/hole/wall" too. */
export const ofFeature = (name, feature) => name.startsWith(feature + '/') || name.includes('/' + feature + '/');

/** The mesh indices of a feature's faces: any of whose sources is the feature's. */
export function featureFaces(result, feature) {
  const indices = [];
  if (feature) result.faces.forEach((sources, i) => { if (sources.some(n => ofFeature(n, feature))) indices.push(i); });
  return indices;
}

/**
 * The face names a feature's definition refers to (a fillet's edge, a loop's face), from the document text: what a
 * feature that failed and left no faces of its own is about. Part documents only.
 */
export function referencedNames(text, feature, names) {
  let doc;
  try { doc = JSON.parse(text); } catch { return []; }
  const found = [];
  const walk = v => {
    if (typeof v === 'string') { if (names.has(v) && !found.includes(v)) found.push(v); }
    else if (v && typeof v === 'object') Object.values(v).forEach(walk);
  };
  walk(Array.isArray(doc?.features) ? doc.features.find(f => f.id === feature) : null);
  return found;
}

/** The size a below-resolution diagnostic is about, in mm: a thin spot's thickness, else the one in its message. */
export function sizeOf(message, spot) {
  if (spot) return spot.thickness;
  // The kernel formats numbers culture-invariantly: "0.601 mm apart" on every machine.
  const m = message.match(/\((\d[\d.]*) mm\)|(\d[\d.]*) mm (?:apart|thick)/);
  return m ? +(m[1] ?? m[2]) : null;
}

/** The largest of 1, 2, 2.5, 5 × 10^k not above x. */
export function niceBelow(x) {
  const decade = 10 ** Math.floor(Math.log10(x));
  return +([5, 2.5, 2, 1].find(m => m * decade <= x * (1 + 1e-9)) * decade).toPrecision(3);
}

/** Smallest cell offered: finer runs out of memory or patience in a browser. */
export const finestCell = 0.01;

/**
 * The cell size that resolves something of this size: walls, gaps and holes of 2 cells or more are always resolved
 * (ModelingSettings.CellSize), so size / 2, and at least half the current cell, since a coarse cell measures
 * roughly. Null when that is finer than finestCell (a zero-thickness contact) or the size is not known.
 */
export function resolveCell(size, cell) {
  const target = Math.min(size > 0 ? size / 2 : cell / 2, cell / 2);
  if (!(target >= finestCell)) return null;
  return niceBelow(target);
}

/**
 * The result's diagnostics for display. Each is the kernel's {code, feature, message} plus: index (in the result),
 * title, error, faces (source names the message mentions; for a feature that left no faces, the ones its definition
 * refers to: the edge a fillet could not round), spot (the thinnest thin spot between two of those faces,
 * or null), size (mm, below-resolution only), stale (a below-resolution diagnostic of a preview: coarse cells give
 * false reports) and resolve (the cell size that would resolve it, or null).
 */
export function diagnose(result) {
  if (!result) return [];
  const names = new Set([...result.faces.flat(), ...result.thinSpots.flatMap(s => s.faces)]);
  return result.diagnostics.map((d, index) => {
    let faces = namesIn(d.message, names);
    if (!faces.length && d.feature && result.text && !featureFaces(result, d.feature).length) faces = referencedNames(result.text, d.feature, names);
    const spot = result.thinSpots.filter(s => s.faces.every(n => faces.includes(n)))
      .reduce((a, s) => (a && a.thickness <= s.thickness ? a : s), null);
    const below = d.code === 'below-resolution';
    const size = below ? sizeOf(d.message, spot) : null;
    const stale = below && !!result.preview;
    return {
      ...d, index, title: title(d.code), error: errors.has(d.code), faces, spot, size, stale,
      resolve: below && !stale && result.cell ? resolveCell(size, result.cell) : null,
    };
  });
}

/** Items grouped by code: [{code, title, error, items}], errors first, then in the order of titles. */
export function groups(items) {
  const order = Object.keys(titles);
  const rank = code => (order.includes(code) ? order.indexOf(code) : order.length);
  const byCode = new Map();
  for (const item of items) {
    if (!byCode.has(item.code)) byCode.set(item.code, { code: item.code, title: item.title, error: item.error, items: [] });
    byCode.get(item.code).items.push(item);
  }
  return [...byCode.values()].sort((a, b) => b.error - a.error || rank(a.code) - rank(b.code));
}

/** A diagnostic again in a new result: the same code, feature and faces. Null when it is gone. */
export const refind = (items, wanted) =>
  wanted ? items.find(i => i.code === wanted.code && i.feature === wanted.feature && i.faces.join() === wanted.faces.join()) ?? null : null;

/** Thickness or size as a label: "0.4 mm". */
export const mm = x => `${+x.toPrecision(3)} mm`;

/**
 * The state at a glance, from the store's state: kind is valid, warn (warnings only), error, or broken (the document
 * does not read; the last good result stays on screen). Stale diagnostics do not count as problems.
 */
export function verdict({ result, problem }) {
  if (problem) return { kind: 'broken', text: 'Document does not read', detail: 'showing the last good result' };
  if (!result) return null;
  const items = diagnose(result).filter(i => !i.stale);
  const n = items.length;
  const kind = n === 0 ? 'valid' : items.some(i => i.error) ? 'error' : 'warn';
  return { kind, text: n === 0 ? 'Valid' : `${n} problem${n > 1 ? 's' : ''}`, detail: result.cell ? `${result.cell} mm cells` : '' };
}
