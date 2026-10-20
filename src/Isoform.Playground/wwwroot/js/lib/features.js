// The document as the feature tree and the inspector see it. Pure, so it is tested in Node.
//
// An item is one row of the tree: a feature of a part, the part's parameter table, or a component or mate of an
// assembly. Its key is also its edit path prefix (DocumentEdits): "hole", "parameters", "components.pin",
// "mates.seat". The store keeps the selected item by key, so it survives regenerations like face names do.

const BUNDLE = 'isoform-bundle/0', ASSEMBLY = 'isoform-assembly/0';

/** The ops the format allows per type (BooleanOp); a combine joins or cuts a body it already has. */
export const OPS = { block: ['new', 'join', 'cut'], extrude: ['new', 'join', 'cut'], revolve: ['new', 'join', 'cut'], combine: ['join', 'cut'] };
export const MATE_KINDS = ['mate', 'flush', 'axis'];

/** The parsed document of a regeneration (its canonical text), or null. */
export function documentOf(result) {
  try { return result?.text ? JSON.parse(result.text) : null; } catch { return null; }
}

/** The assembly of a bundle or assembly document, else null. */
const assemblyOf = doc => doc?.format === BUNDLE ? doc.assembly : doc?.format === ASSEMBLY ? doc : null;

/**
 * The body each feature works on, as the regeneration picks it: its body key, else its own id when it starts a
 * body, else (fillet, chamfer) the body of the faces it rounds, else the current one.
 */
export function bodiesOf(features) {
  const body = new Map();
  let current = null;
  for (const f of features) {
    const named = f.body ?? (f.op === 'new' ? f.id : null);
    const face = f.loop ?? f.edge?.[0];
    const b = named ?? ((f.type === 'fillet' || f.type === 'chamfer') && face ? body.get(face.split('/')[0]) : null) ?? current;
    if (named) current = named;
    body.set(f.id, b);
  }
  return body;
}

/** The tree: groups of items in document order; a group's title is null when there is nothing to tell apart. */
export function treeOf(doc) {
  const asm = assemblyOf(doc);
  if (asm) {
    const component = c => ({ key: `components.${c.id}`, kind: 'component', id: c.id, type: 'component', json: c });
    const mate = m => ({ key: `mates.${m.id}`, kind: 'mate', id: m.id, type: 'mate', json: m });
    return [
      { title: 'Components', items: (asm.components ?? []).map(component) },
      ...(asm.mates?.length ? [{ title: 'Mates', items: asm.mates.map(mate) }] : []),
    ];
  }
  const features = doc?.features ?? [];
  const parameters = { key: 'parameters', kind: 'parameters', id: 'parameters', type: 'parameters', json: doc?.parameters ?? {} };
  const items = features.map(f => ({ key: f.id, kind: 'feature', id: f.id, type: f.type, json: f }));
  const body = bodiesOf(features);
  const names = [...new Set(features.map(f => body.get(f.id)))];
  if (names.length < 2) return [{ title: null, items: [parameters, ...items] }];
  return [{ title: null, items: [parameters] },
    ...names.map(name => ({ title: name ?? '(no body)', items: items.filter(i => body.get(i.id) === name) }))];
}

export const itemsOf = tree => tree.flatMap(g => g.items);

/** An item's type as the tree shows it: with its op when it joins or cuts, a mate's kind, the parameter count. */
export function describe(item) {
  if (item.kind === 'parameters') return `${Object.keys(item.json).length || 'none'} defined`;
  if (item.kind === 'mate') return `mate · ${item.json.kind}`;
  return item.json.op && item.json.op !== 'new' ? `${item.type} · ${item.json.op}` : item.type;
}

/** The key of the item that made a face, from its name: the feature in a part, the component in an assembly. */
export function keyOfFace(doc, name) {
  const first = name?.split('/')[0];
  if (!first) return null;
  return assemblyOf(doc) ? `components.${first}` : first;
}

/** The mesh indices of an item's faces: any source name under its id, or for a mate the two faces it names. */
export function facesOf(result, item) {
  if (!result || !item || item.kind === 'parameters') return [];
  const has = item.kind === 'mate'
    ? (n => n === item.json.a || n === item.json.b)
    : (n => n.startsWith(`${item.id}/`));
  const indices = [];
  result.faces.forEach((face, i) => { if (face.some(has)) indices.push(i); });
  return indices;
}

/** The diagnostics that name an item (a component also owns those of its own features, "<id>/…"). */
export const diagnosticsOf = (result, item) => (result?.diagnostics ?? []).filter(d =>
  d.feature != null && (d.feature === item.id || (item.kind === 'component' && d.feature.startsWith(`${item.id}/`))));

/** A number or an "=expression" string: a value a scalar field takes. */
export const isScalar = v => (typeof v === 'number' && Number.isFinite(v)) || (typeof v === 'string' && v.startsWith('='));

/** What a typed value means: a number, an expression (with or without its "="), or undefined for nothing typed. */
export function parseScalar(text) {
  const t = String(text).trim();
  if (!t) return undefined;
  const n = Number(t);
  if (Number.isFinite(n)) return n;
  return t.startsWith('=') ? t : `=${t}`;
}

export const setEdit = (path, value) => ({ set: path, value });

// Optional fields the canonical form leaves out at their default, shown so they can be set.
function withDefaults(json, type) {
  const out = { ...json };
  if ((type === 'block' || type === 'extrude' || type === 'revolve') && json.op !== 'new' && !('blend' in json)) out.blend = 0;
  if (type === 'combine' && !('keep' in json)) out.keep = false;
  if (type === 'component' && !('grounded' in json)) out.grounded = false;
  if (type === 'mate' && !('offset' in json)) out.offset = 0;
  return out;
}

const FACE_KEYS = new Set(['edge', 'loop', 'a', 'b']);
const axes = (n, sketch) => n === 2 ? (sketch ? ['u', 'v'] : ['x', 'y']) : n === 3 ? ['x', 'y', 'z'] : null;

/**
 * The inspector's fields for an item, generated from its JSON. Every field carries the edit path it sets:
 *   scalar {path, label, value, integer}     a number or "=expression"
 *   vector {path, label, entries: scalar[]}  an array of scalars, one entry per index
 *   choice {path, label, value, options}     op, mate kind
 *   faces  {path, label, value, count}       face names: edge (2, an array) or loop / mate face (1, a string)
 *   text   {path, label, value, list}        other strings; list: an array of ids (a pattern's features)
 *   bool   {path, label, value}
 *   group  {label, fields}                   a nested object, or one entry of a list of objects (profile edges)
 *   parameters {path, entries: scalar[]}     the parameter table, one entry per name
 */
export function fieldsOf(item) {
  if (item.kind === 'parameters')
    return [{ kind: 'parameters', path: 'parameters', entries: Object.entries(item.json).map(([name, value]) => scalar(`parameters.${name}`, name, value)) }];
  return walk(withDefaults(item.json, item.type), item.key, item.type, false);
}

function scalar(path, label, value) {
  return { kind: 'scalar', path, label, value, integer: label === 'count' };
}

function walk(json, prefix, type, sketch) {
  const fields = [];
  for (const [key, value] of Object.entries(json)) {
    if (key === 'id' || key === 'type' || key === 'format' || key === 'units') continue;
    const path = `${prefix}.${key}`;
    if (key === 'op' && OPS[type]) fields.push({ kind: 'choice', path, label: key, value, options: OPS[type] });
    else if (key === 'kind' && type === 'mate') fields.push({ kind: 'choice', path, label: key, value, options: MATE_KINDS });
    else if (FACE_KEYS.has(key)) fields.push({ kind: 'faces', path, label: key, value, count: Array.isArray(value) ? 2 : 1 });
    else if (typeof value === 'boolean') fields.push({ kind: 'bool', path, label: key, value });
    else if (isScalar(value)) fields.push(scalar(path, key, value));
    else if (Array.isArray(value) && value.every(isScalar)) {
      const names = axes(value.length, sketch);
      fields.push({ kind: 'vector', path, label: key, entries: value.map((v, i) => scalar(`${path}.${i}`, names?.[i] ?? String(i), v)) });
    } else if (Array.isArray(value) && value.every(v => typeof v === 'string')) fields.push({ kind: 'text', path, label: key, value, list: true });
    else if (Array.isArray(value)) // a list of objects: profile edges, each labelled by its id; a bulge of 0 is left out
      value.forEach((v, i) => fields.push({ kind: 'group', label: `${key} ${v.id ?? i}`, fields: walk('to' in v && !('bulge' in v) ? { ...v, bulge: 0 } : v, `${path}.${i}`, type, sketch) }));
    else if (value && typeof value === 'object') fields.push({ kind: 'group', label: key, fields: walk(value, path, type, sketch || key === 'profile') });
    else if (typeof value === 'string') fields.push({ kind: 'text', path, label: key, value, list: false });
  }
  return fields;
}

/**
 * The value a horizontal drag of dx pixels gives from base: a step per 2 px, a hundredth of base's order of magnitude
 * (100 → 1, 10 → 0.1), at least 0.01; shift for ten times faster. Counts go by one per 8 px.
 */
export function scrubbed(base, dx, { integer = false, fast = false } = {}) {
  const ticks = Math.trunc(dx / 2) * (fast ? 10 : 1);
  if (integer) return Math.max(1, Math.round(base) + Math.trunc(ticks / 4));
  const step = Math.max(0.01, 10 ** (Math.floor(Math.log10(Math.abs(base) || 1)) - 2));
  const decimals = Math.max(0, -Math.floor(Math.log10(step)));
  return Number((base + ticks * step).toFixed(decimals));
}
