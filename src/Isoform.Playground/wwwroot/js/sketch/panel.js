// The sketch panel, in the right-hand column while sketching: status and problems, the entities (renamed in place:
// their ids become face names), every constraint (hover lights what it binds, × deletes it, a dimension's value is
// edited in place), the parameters an expression can use, and the finish. It only shows what it is given and calls
// back; sketch/mode.js owns the state.
import { isDimension, kindOf, refsOf, shapeOf } from './model.js';
import { showValue } from './dimension.js';
import { describeConstraint, summary } from './status.js';

const el = (tag, props = {}, ...children) => {
  const e = Object.assign(document.createElement(tag), props);
  e.append(...children);
  return e;
};

/**
 * actions: rename(from, to) → problem or null, removeConstraint(i), setValue(i, text) → problem or null,
 * highlight(names), finishChanged(patch), finish(), cancel().
 */
export function createPanel(host, actions) {
  const root = el('section', { className: 'sketch-panel' });
  host.prepend(root);

  // A name or value being typed keeps its input across the re-renders every solve makes.
  let typing = null;

  function nameRow(name, kind, state, construction) {
    const input = el('input', { value: name, spellcheck: false, title: 'Rename: the id becomes the face name' });
    input.onkeydown = e => {
      if (e.key === 'Enter') input.blur();
      if (e.key === 'Escape') { input.value = name; input.blur(); }
    };
    input.onfocus = () => { typing = name; };
    input.onblur = () => {
      typing = null;
      if (input.value === name) return;
      const problem = actions.rename(name, input.value.trim());
      if (problem) { input.value = name; input.title = problem; root.querySelector('.sketch-note').textContent = problem; }
    };
    const li = el('li', { className: `${state ?? ''}${construction ? ' construction' : ''}` }, el('span', { className: 'kind', textContent: kind }), input);
    li.onmouseenter = () => actions.highlight([name]);
    li.onmouseleave = () => actions.highlight([]);
    return li;
  }

  function constraintRow(c, i, conflicts) {
    const kind = kindOf(c);
    const li = el('li', { className: conflicts.has(i) ? 'conflict' : '' },
      el('span', { className: 'kind', textContent: kind }),
      el('span', { className: 'refs', textContent: refsOf(c).join(' ') }));
    if (isDimension(c)) {
      const input = el('input', { className: 'value', value: showValue(c.value, kind), spellcheck: false, title: 'A number or =expression' });
      input.dataset.index = i;
      input.onfocus = () => { typing = `#${i}`; };
      input.onkeydown = e => {
        if (e.key === 'Enter') input.blur();
        if (e.key === 'Escape') { input.value = showValue(c.value, kind); input.blur(); }
      };
      input.onblur = () => {
        typing = null;
        if (input.value !== showValue(c.value, kind)) {
          const problem = actions.setValue(i, input.value);
          if (problem) root.querySelector('.sketch-note').textContent = problem;
        }
      };
      li.append(input);
    }
    li.append(el('button', { className: 'remove', textContent: '×', title: 'Delete this constraint', onclick: () => actions.removeConstraint(i) }));
    li.onmouseenter = () => actions.highlight(refsOf(c));
    li.onmouseleave = () => actions.highlight([]);
    return li;
  }

  function finishBlock({ editing, finish, lines, picked, regionCount }) {
    if (editing) {
      return el('div', { className: 'sketch-finish' },
        el('div', { className: 'buttons' },
          el('button', { className: 'primary', textContent: 'Save sketch', onclick: () => actions.finish() }),
          el('button', { textContent: 'Cancel', onclick: () => actions.cancel() })));
    }
    const op = el('select', { title: 'What the sketch makes' },
      ...[['extrude', 'Extrude'], ['cut', 'Cut'], ['revolve', 'Revolve'], ['sketch', 'Sketch only']].map(([v, t]) => el('option', { value: v, textContent: t, selected: v === finish.op })));
    op.onchange = () => actions.finishChanged({ op: op.value });
    const amount = finish.op === 'revolve' ? 'angle' : 'depth';
    const size = el('input', { className: 'amount', type: 'number', value: finish[amount], min: 0, step: amount === 'angle' ? 15 : 1 });
    size.onchange = () => actions.finishChanged({ [amount]: +size.value });
    const id = el('input', { className: 'feature-id', value: finish.id, spellcheck: false, title: 'The feature id: its faces are <id>/<entity>' });
    id.onchange = () => actions.finishChanged({ id: id.value.trim() });
    const rows = [el('label', {}, 'Make ', op)];
    if (finish.op !== 'sketch') {
      rows.push(el('label', {}, amount === 'angle' ? 'Angle ° ' : 'Depth mm ', size), el('label', {}, 'Feature id ', id));
      if (finish.op === 'revolve') {
        const axis = el('select', {}, el('option', { value: '', textContent: '(sketch y axis)' }),
          ...lines.map(l => el('option', { value: l, textContent: l, selected: l === finish.axis })));
        axis.onchange = () => actions.finishChanged({ axis: axis.value || null });
        rows.push(el('label', {}, 'Axis ', axis));
      }
      rows.push(el('div', { className: 'muted', textContent: regionCount === 0 ? 'No closed regions yet.'
        : picked ? `${picked} of ${regionCount} region${regionCount > 1 ? 's' : ''} picked` : `All ${regionCount} region${regionCount > 1 ? 's' : ''} (click inside one to pick it)` }));
    }
    return el('div', { className: 'sketch-finish' }, ...rows,
      el('div', { className: 'buttons' },
        el('button', { className: 'primary', textContent: 'Finish', onclick: () => actions.finish() }),
        el('button', { textContent: 'Cancel', onclick: () => actions.cancel() })));
  }

  /**
   * Shows the sketch: s is {sketch, editing, face, reply, conflicts, state (names → free/fixed/conflict),
   * parameters (names), finish, lines (line ids), picked (count), regionCount}.
   */
  function render(s) {
    if (typing) return; // after the input is done
    const { sketch, reply } = s;
    const status = reply ? summary(reply) : { text: 'solving…', level: 'free' };
    const problems = (reply?.problems ?? []).map(p => el('li', { className: `group ${p.code === 'sketch-redundant' ? '' : 'error'}` },
      el('div', { className: 'group-head' }, el('b', { textContent: p.code })),
      el('div', { textContent: p.message }),
      el('div', { className: 'faces', textContent: p.constraints.map(i => sketch.constraints[i] ? describeConstraint(sketch.constraints[i], i) : `#${i}`).join(' · ') })));
    root.replaceChildren(
      el('div', { className: 'model-head', textContent: `${s.editing ? 'Editing' : 'New'} sketch ${sketch.id}${s.face ? ` on ${s.face}` : ''}` }),
      el('div', { className: `sketch-status ${status.level}`, textContent: status.text }),
      el('ul', { className: 'diagnostics' }, ...problems),
      el('div', { className: 'sketch-note muted' }),
      el('div', { className: 'model-head', textContent: 'Entities' }),
      el('ul', { className: 'sketch-list entities' }, ...sketch.entities.map(e => nameRow(e.id, shapeOf(e), s.state.get(e.id), e.construction)),
        ...Object.keys(sketch.points).filter(p => !sketch.entities.some(e => (e.line ?? []).includes(p) || e.arc?.from === p || e.arc?.to === p))
          .map(p => nameRow(p, 'point', s.state.get(p)))),
      el('div', { className: 'model-head', textContent: 'Constraints' }),
      el('ul', { className: 'sketch-list constraints' }, ...sketch.constraints.map((c, i) => constraintRow(c, i, s.conflicts))),
      el('div', { className: 'sketch-params muted', textContent: s.parameters.length ? `Parameters for =expressions: ${s.parameters.join(', ')}` : 'No parameters: a dimension takes a number, or =expression once the document has some.' }),
      finishBlock(s));
  }

  return { render, destroy: () => root.remove(), root };
}
