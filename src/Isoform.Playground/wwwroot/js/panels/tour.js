// The guided tour: a scripted agent. Each step is a caption and an edit list, committed like any other change
// (commits.applyEdits, origin 'tour'), so the agent view, the history, the diagnostics and the pins all show what it did.
// It follows the phase-1 demo (docs/playground-plan.md): widen the plate, move the hole into a thin wall and resolve
// it, on to tangent, a fillet that does not fit, then undo step by step back to the hash it started from.
// Next, Back and Play step through it; Escape exits. Back restores the history entry and cell the step before ended on.
import { short } from '../lib/history.js';
import { pretty } from './editor.js';

/** The example the tour opens, and the faces it pins so every step says what it did to them. */
export const example = 'Plate with hole and fillet';
export const pinned = ['plate/z+', 'plate/x+', 'plate/y+', 'fillet/face'];

// A step: edits (committed), undo, or cell alone (the same document regenerated at another cell size); cell before
// edits picks the cell they regenerate at ('default': the page's). expect: a code that must be reported after it,
// gone: one that must not, pins: statuses the pinned faces must have, original: back at the starting hash.
export const script = [
  {
    title: 'An agent at work', open: true,
    caption: 'This tour edits the plate the way an agent would: one edit list per step, each committed to the document '
      + 'history. Four faces are pinned, so each step also shows what it did to them.',
  },
  {
    title: 'Widen the plate', edits: [{ set: 'plate.size.0', value: 120 }], label: 'widen plate/x+',
    caption: 'One set on plate.size.0 moves plate/x+ out by 20 mm. The agent view shows the edit, the line it changed '
      + 'and the new hash. The pins still resolve, because they go by name.',
  },
  {
    title: 'Hole towards the edge', edits: [{ set: 'hole.center.1', value: 44.6 }], label: 'move the hole towards plate/y+',
    expect: 'below-resolution',
    caption: 'Move the hole until 0.4 mm of wall is left to plate/y+. At 0.5 mm cells that is below what the kernel '
      + 'resolves, and it says so: both faces lit, the thickness at the spot.',
  },
  {
    title: 'Resolve it', cell: 0.2, gone: 'below-resolution',
    caption: 'Regenerate the same document at 0.2 mm cells: the wall is resolved and the report is gone. The hash does '
      + 'not change, because it belongs to the document, not to the mesh.',
  },
  {
    title: 'Until tangent', cell: 'default', edits: [{ set: 'hole.center.1', value: 45 }], label: 'hole tangent to plate/y+',
    expect: 'tangent-contact', pins: { 'plate/y+': 'split' },
    caption: 'Carry on until the hole touches plate/y+: tangent-contact, a wall of zero thickness, reported instead of '
      + 'meshed quietly. The pinned plate/y+ is now split in two.',
  },
  {
    title: 'Fillet past what fits', edits: [{ set: 'fillet.radius', value: 11 }], label: 'fillet radius 11',
    expect: 'fillet-invalid', pins: { 'fillet/face': 'gone' },
    caption: 'Raise the fillet radius to 11 mm on a 10 mm plate: a precise fillet-invalid that names its edge. The rest '
      + 'of the part stays valid. The pinned fillet/face is gone.',
  },
  {
    title: 'Undo', undo: true, gone: 'fillet-invalid', expect: 'tangent-contact', pins: { 'fillet/face': 'resolved' },
    caption: 'Undo is document history. One entry back, the fillet fits again.',
  },
  {
    title: 'Undo', undo: true, expect: 'below-resolution', pins: { 'plate/y+': 'resolved' },
    caption: 'The hole steps back from tangent, and the thin wall is reported again.',
  },
  {
    title: 'Undo', undo: true, gone: 'below-resolution',
    caption: 'The hole is back where it was. Nothing to report.',
  },
  {
    title: 'Back at the start', undo: true, original: true,
    caption: 'Undo the widening and the text is the original, byte for byte, so the hash is too.',
  },
];

const el = (tag, className, ...children) => {
  const e = document.createElement(tag);
  if (className) e.className = className;
  e.append(...children);
  return e;
};
const button = (text, title, act) => Object.assign(el('button', null, text), { title, onclick: act });

/**
 * box: where the tour card goes (the 3D view); agentBox: the agent view, scrolled to after edits. viewer, diagnostics,
 * references: those panels' handles; call: the kernel client's; regenerate: main.js's; pickCell(cell) sets the cell
 * select without regenerating; defaultCell: the page's.
 */
export function createTour(store, { box, agentBox, examplesBox, viewer, commits, diagnostics, references, call, regenerate, pickCell, defaultCell, delay = 3500 }) {
  let index = -1, playing = false, busy = false, original = null;
  let ended = [];  // per step run: {at, cell}, the history entry and cell it ended on (what Back returns to)
  let log = [];    // per step run: {index, title, hash, before, codes, ok, problems, error?}

  const card = el('div', 'tour');
  card.hidden = true;
  box.append(card);

  async function open() {
    const text = (await call('examples')).result[example];
    pickCell(defaultCell);
    if (examplesBox && [...examplesBox.options].some(o => o.value === example)) examplesBox.value = example;
    store.set({ text: pretty(JSON.parse(text)), selection: [] });
    await regenerate({ fit: true, origin: 'tour', label: `open ${example}` });
    const have = new Set((store.state.pins ?? []).map(p => p.ref.face));
    for (const face of pinned) if (!have.has(face)) await references.pin({ face });
  }

  async function perform(step) {
    if (step.open) return open();
    if (step.cell) pickCell(step.cell === 'default' ? defaultCell : step.cell);
    if (step.edits) return commits.applyEdits(step.edits, step.label, 'tour');
    if (step.undo) return commits.undo();
    return regenerate();
  }

  /** What a step's run left to check: its expected codes, pins and hash. */
  function check(step, hash) {
    const codes = diagnostics.items().map(d => d.code);
    const problems = [];
    if (step.expect && !codes.includes(step.expect)) problems.push(`no ${step.expect}`);
    if (step.gone && codes.includes(step.gone)) problems.push(`${step.gone} still reported`);
    for (const [face, status] of Object.entries(step.pins ?? {})) {
      const pin = (store.state.pins ?? []).find(p => p.ref.face === face);
      if (pin?.status !== status) problems.push(`${face} is ${pin?.status ?? 'not pinned'}, not ${status}`);
    }
    if (step.original && hash !== original) problems.push(`hash ${short(hash)} is not the original ${short(original)}`);
    return { codes, problems };
  }

  /** The diagnostic the step is about in focus (its faces lit, the camera on it), else the whole part in view. */
  function look(step) {
    const focus = diagnostics.items().find(d => d.code === step.expect);
    if (focus) return diagnostics.focus(focus.index);
    diagnostics.focus(null);
    viewer.flyTo(store.state.result?.faces.map((_, f) => f) ?? []);
  }

  async function run(i) {
    const step = script[i], before = store.state.hash;
    busy = true;
    index = i;
    render();
    try {
      const result = await perform(step);
      if (result?.error) throw new Error(result.error);
      await references.refresh();
      const { hash } = store.state;
      if (step.open) original = hash;
      const { codes, problems } = check(step, hash);
      log = [...log.slice(0, i), { index: i, title: step.title, hash, before, codes, ok: !problems.length, problems }];
      if (!step.open) look(step);
      if (step.edits || step.undo) agentBox?.scrollIntoView({ block: 'nearest' });
    } catch (e) {
      const error = String(e?.message ?? e);
      log = [...log.slice(0, i), { index: i, title: step.title, hash: store.state.hash, before, codes: [], ok: false, problems: [error], error }];
    } finally {
      ended = [...ended.slice(0, i), { at: store.state.historyAt, cell: store.state.result?.cell ?? defaultCell }];
      busy = false;
      render();
    }
    return log[i];
  }

  const next = () => busy || index >= script.length - 1 ? Promise.resolve(null) : run(index + 1);

  async function back() {
    if (busy || index <= 0) return null;
    const target = index - 1, { at, cell } = ended[target];
    busy = true;
    try {
      pickCell(cell);
      await commits.restore(at);
      await references.refresh();
      look(script[target]);
      index = target;
    } finally {
      busy = false;
      render();
    }
    return log[target];
  }

  async function play() {
    if (playing) return;
    playing = true;
    render();
    while (playing && index < script.length - 1) {
      await next();
      if (playing && index < script.length - 1) await new Promise(done => setTimeout(done, delay));
    }
    playing = false;
    render();
  }
  const pause = () => { playing = false; render(); };

  function start() {
    if (!card.hidden) return Promise.resolve(log[index] ?? null);
    card.hidden = false;
    [index, playing, original, ended, log] = [-1, false, null, [], []];
    return next();
  }

  function exit() {
    playing = false;
    card.hidden = true;
  }

  const mark = (ok, text) => el('span', ok ? 'good' : 'bad', `${ok ? '✓' : '✗'} ${text}`);

  /** The step's hash line and its checks. */
  function facts(i) {
    const entry = log[i], step = script[i];
    if (!entry) return [el('span', 'muted', busy ? 'running…' : '')];
    const hash = step.original
      ? `hash ${short(entry.hash)} ${entry.hash === original ? '=' : '≠'} original ${short(original)}`
      : step.open ? `original hash ${short(entry.hash)}`
        : entry.before === entry.hash ? `hash ${short(entry.hash)} unchanged` : `hash ${short(entry.before)} → ${short(entry.hash)}`;
    return [
      el('code', null, hash),
      ...(step.expect ? [mark(entry.codes.includes(step.expect), step.expect)] : []),
      ...(step.gone ? [mark(!entry.codes.includes(step.gone), `no ${step.gone}`)] : []),
      ...(step.original ? [mark(entry.hash === original, entry.hash === original ? 'identical' : 'differs')] : []),
      ...(entry.error ? [el('span', 'bad', entry.error)] : []),
    ];
  }

  /** The tour's pins with their status now; those the step expects a status of are checked. */
  function pinChips(step) {
    return (store.state.pins ?? []).filter(p => pinned.includes(p.ref.face)).map(p => {
      const wanted = log[index] && step?.pins?.[p.ref.face];
      return el('span', `tour-pin${wanted ? ' expected' : ''}`, el('span', `chip ${p.status}`, p.status), ' ', el('code', null, p.ref.face),
        ...(wanted ? [' ', mark(p.status === wanted, '')] : []));
    });
  }

  function render() {
    if (card.hidden) return;
    const step = script[index];
    const pins = pinChips(step);
    card.replaceChildren(
      el('div', 'tour-head', el('span', 'muted', `Tour · ${index + 1} of ${script.length}`), el('b', null, step?.title ?? ''),
        button('×', 'Exit the tour (Esc)', exit)),
      el('p', 'tour-caption', step?.caption ?? ''),
      el('div', 'tour-facts', ...facts(index)),
      el('div', 'tour-pins', ...pins),
      el('div', 'tour-controls',
        Object.assign(button('◀ Back', 'The step before', () => { pause(); back(); }), { disabled: busy || index <= 0 }),
        button(playing ? '❚❚ Pause' : '▶ Play', playing ? 'Stop after this step' : 'Run the rest, a step every few seconds', () => playing ? pause() : play()),
        Object.assign(button('Next ▶', 'The next step', () => { pause(); next(); }), { disabled: busy || index >= script.length - 1 })),
    );
  }

  store.subscribe((s, before) => { if (s.pins !== before.pins) render(); });
  addEventListener('keydown', e => { if (e.key === 'Escape' && !card.hidden) exit(); });

  return {
    start, next, back, play, pause, exit,
    /** For the browser tests: where the tour is and what each step run found. */
    state: () => ({ active: !card.hidden, index, count: script.length, playing, busy, original, log }),
  };
}
