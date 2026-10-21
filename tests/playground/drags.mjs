// Browser checks of dragging (J2), run by e2e.mjs: one drag per handle kind in the examples, with the preview and its
// ghost while it drags, and the phase-1 demo of docs/playground-plan.md section 3 as one scenario. Each returns its
// problems (empty when it passed) and prints its line.
import { join } from 'node:path';

/** page: a browser.mjs page; url: the served playground; settled: the stats-line expression e2e.mjs waits on. */
export function createDrags(page, { url, settled, timeout, shots }) {
  const evaluate = e => page.evaluate(e);
  const feature = id => evaluate(`JSON.parse(window.isoform.document()).features.find(f => f.id === ${JSON.stringify(id)})`);

  async function open(example) {
    await page.open(`${url}?example=${example}`);
    await page.waitFor(settled, { timeout });
  }

  /** Selects a face and waits for the handle; its screen position once the view is still. */
  async function grab(face, id) {
    await evaluate(`window.isoform.select(${JSON.stringify([face])})`);
    await page.waitFor(`window.isoform.handles().some(h => h.id === ${JSON.stringify(id)})`, { timeout: 10000 });
    await page.frames(3);
    return evaluate(`window.isoform.handles().find(h => h.id === ${JSON.stringify(id)})`);
  }

  /**
   * Drags a handle along a screen direction, step px at a time up to px, until until(drag state) holds; during(state)
   * runs before the release (screenshots). Resolves once the release has committed (the hash changed) to
   * {state, before, after} with before/after the hashes.
   */
  async function drag(h, direction, { px = 40, step = 4, until = () => false, modifiers = 0, during, problems = [], checkGhost = true } = {}) {
    const at = d => [h.screen[0] + direction[0] * d, h.screen[1] + direction[1] * d];
    const before = await evaluate('window.isoform.hash()');
    await page.mouse('mouseMoved', h.screen);
    await page.mouse('mousePressed', h.screen, { buttons: 1 });
    let state = null, d = 0, ghost = null;
    while (d < px) {
      d = Math.min(d + step, px);
      await page.mouse('mouseMoved', at(d), { buttons: 1, modifiers });
      // The ghost on the first move that changes something, and whether a preview had come back by then.
      const now = await evaluate('({ state: window.isoform.dragging(), preview: !!window.isoform.preview(), ghost: window.isoform.ghost() })');
      state = now.state;
      if (!ghost && state && [state.t].flat().some(t => t !== 0)) ghost = now;
      if (state && until(state)) break;
    }
    // Before any preview the ghost shows the move; once one is on screen it moves from where that preview was made.
    if (checkGhost && ghost && !ghost.preview && !ghost.ghost.visible) problems.push(`no ghost on the first move: ${JSON.stringify(ghost.ghost)}`);
    if (during) await during(state);
    await page.mouse('mouseReleased', at(d), { modifiers });
    const after = await page.waitFor(`(h => h !== ${JSON.stringify(before)} && h)(window.isoform.hash())`, { timeout });
    await page.waitFor('!window.isoform.preview()', { timeout: 10000 });
    return { state, before, after };
  }

  /** While dragging: a preview on screen, badged, and the ghost moved from where the preview was made to the pointer. */
  async function previewing(problems) {
    try {
      await page.waitFor('window.isoform.preview()', { timeout: 15000 });
      if (!(await evaluate(`!!document.querySelector('#verdict .preview')`))) problems.push('the preview is not badged');
      const { p, g, d } = await evaluate('({ p: window.isoform.preview(), g: window.isoform.ghost(), d: window.isoform.dragging() })');
      const moved = JSON.stringify(g.t) !== JSON.stringify(g.from);
      if (JSON.stringify(g.from) !== JSON.stringify(p.t) || JSON.stringify(g.t) !== JSON.stringify(d.t) || (g.faces.length && g.visible !== moved))
        problems.push(`the ghost does not follow: ${JSON.stringify({ preview: p.t, drag: d.t, ghost: g })}`);
    } catch (e) {
      problems.push(`no preview while dragging: ${e.message}`);
    }
  }

  const report = (name, problems) => {
    problems.push(...page.errors);
    console.log(`${problems.length ? 'FAIL' : 'pass'}  ${name}`);
    for (const p of problems) console.log(`        ${p}`);
    return problems;
  };

  /** A drag per handle kind: planar (hole centre), linear (fillet radius), angle (revolve), and a pattern's count. */
  async function kinds() {
    const all = [];
    const one = async (name, run) => {
      const problems = [];
      let what = '';
      try { what = await run(problems); } catch (e) { problems.push(e.message); }
      all.push(...report(`drag ${name}${what ? `: ${what}` : ''}`, problems));
    };

    await one('planar hole:center', async problems => {
      await open(0);
      const h = await grab('hole/wall', 'hole:center');
      await drag(h, h.direction, {
        px: 48, problems, during: async () => {
          await previewing(problems);
          await page.frames();
          await page.screenshot(join(shots, 'drag-planar.png'));
        },
      });
      const { center } = await feature('hole');
      if (!(center[0] > 30) || Math.abs(center[1] - 25) > 1) problems.push(`dragged along u, the centre went from [30, 25] to ${JSON.stringify(center)}`);
      return `hole.center ${JSON.stringify(center)}`;
    });

    await one('linear fillet:radius', async problems => {
      await open(0);
      const h = await grab('fillet/face', 'fillet:radius');
      await drag(h, h.direction, { px: 30, problems, during: () => previewing(problems) });
      const { radius } = await feature('fillet');
      if (!(radius > 2)) problems.push(`dragged outwards, the radius went from 2 to ${radius}`);
      return `fillet.radius ${radius}`;
    });

    await one('angle shaft:angle', async problems => {
      await open(2);
      const h = await grab('shaft/big', 'shaft:angle');
      // Back from 360°, round the axis through the shaft's centre; a full turn moves no faces, so there is no ghost.
      await drag(h, h.direction.map(x => -x), {
        px: 40, problems, checkGhost: false, during: async () => {
          await previewing(problems);
          await page.frames();
          await page.screenshot(join(shots, 'drag-angle.png'));
        },
      });
      const { angle } = await feature('shaft');
      if (!(angle < 360 && angle > 180)) problems.push(`dragged back, the angle went from 360 to ${angle}`);
      return `shaft.angle ${angle}`;
    });

    await one('pattern count +', async problems => {
      await open(1);
      await grab('row/2:bore/wall', 'row:spacing');
      const before = await evaluate('window.isoform.hash()');
      const plus = await page.center('.handle-count .plus');
      if (!plus) throw new Error('no count buttons on row:spacing');
      await page.screenshot(join(shots, 'drag-count.png'));
      await page.click(plus);
      await page.waitFor(`window.isoform.hash() !== ${JSON.stringify(before)}`, { timeout });
      const { linear } = await feature('row');
      if (linear.count !== 4) problems.push(`+ made the count ${linear.count}, not 4`);
      return `row.linear.count ${linear.count}`;
    });
    return all;
  }

  /**
   * The phase-1 demo (docs/playground-plan.md, section 3) on example 0: drag plate/x+ (one set in the agent view);
   * drag the hole towards the y+ edge (a below-resolution thin spot naming both faces) and resolve it; carry on to
   * tangent (tangent-contact); drag the fillet radius up (fillet-invalid); undo four times, back to the original hash.
   */
  async function demo() {
    const problems = [], said = [];
    const step = (text, ok) => { said.push(text); if (!ok) problems.push(text); };
    const diagnostics = () => evaluate('window.isoform.diagnostics()');
    const shot = name => page.frames().then(() => page.screenshot(join(shots, `demo-${name}.png`)));
    try {
      await open(0);
      const original = await evaluate('window.isoform.hash()');

      // 1. plate/x+: one set in the agent view, the hash changes.
      let h = await grab('plate/x+', 'plate:x+');
      const widened = await drag(h, h.direction, { px: 40, problems, during: async () => { await previewing(problems); await shot('1-drag'); } });
      const last = (await evaluate('window.isoform.history()')).entries.at(-1);
      const agent = await evaluate(`[document.getElementById('agent').dataset.hash, document.getElementById('agent').textContent]`);
      step(`1. plate/x+: ${JSON.stringify(last.edits)}`, last.edits.length === 1 && last.edits[0].set === 'plate.size.0' && agent[0] === widened.after && agent[1].includes('plate.size.0'));
      await shot('1-agent');

      // 2. The hole towards the y+ edge, in fine steps (shift): a wall under 2 cells is a thin spot.
      h = await grab('hole/wall', 'hole:center');
      const y = s => s.edits.find(e => e.set === 'hole.center.1')?.value ?? 0;
      await drag(h, h.directionV, { px: 900, step: 2, modifiers: 8, until: s => y(s) >= 44.4, during: () => shot('2-drag-hole') });
      const thin = (await diagnostics()).find(d => d.code === 'below-resolution' && !d.stale);
      step(`2. hole at y = ${(await feature('hole')).center[1]}: ${thin ? `${thin.thickness?.toFixed(3)} mm between ${thin.faces.join(' and ')}` : 'no thin spot'}`,
        thin?.faces.join() === 'plate/y+,hole/wall' && thin.resolve);
      await shot('2-thin-spot');
      if (thin?.resolve) {
        await evaluate(`document.querySelector('#diagnostics li.item[data-index="${thin.index}"] button.resolve').click()`);
        await page.waitFor(`window.isoform.cell() === ${thin.resolve}`, { timeout });
        step(`   resolved at ${thin.resolve} mm`, !(await diagnostics()).some(d => d.code === 'below-resolution'));
        await shot('2-resolved');
      }

      // 3. On to tangent: whole millimetres again, so 45 exactly.
      h = await grab('hole/wall', 'hole:center');
      await drag(h, h.directionV, { px: 300, step: 2, until: s => y(s) >= 45 });
      const tangent = (await diagnostics()).find(d => d.code === 'tangent-contact');
      step(`3. hole at y = ${(await feature('hole')).center[1]}: ${tangent ? 'tangent-contact' : 'no tangent-contact'}`, !!tangent);
      await shot('3-tangent');

      // 4. The fillet radius up past what fits.
      h = await grab('fillet/face', 'fillet:radius');
      await drag(h, h.direction, { px: 600, step: 4, problems, until: s => s.value >= 11, during: () => previewing(problems) });
      const invalid = (await diagnostics()).find(d => d.code === 'fillet-invalid');
      step(`4. fillet radius ${(await feature('fillet')).radius}: ${invalid ? 'fillet-invalid' : 'no fillet-invalid'}`, !!invalid);
      await shot('4-fillet-invalid');

      // 5. Undo four times: the original document, to the hash.
      for (let i = 0; i < 4; i++) await evaluate('window.isoform.undo()');
      const { at, entries } = await evaluate('window.isoform.history()');
      step(`5. undone 4 times: history at ${at} of ${entries.length}`, (await evaluate('window.isoform.hash()')) === original && at === 0 && entries.length === 5);
      await shot('5-undone');
    } catch (e) {
      problems.push(e.message);
    }
    report(`demo: ${said.join('; ')}`, problems);
    return problems;
  }

  /** What the previews cost on this machine, and the kernels' memory. */
  async function costs() {
    const timings = await evaluate('window.isoform.previewTimings()');
    const byCell = new Map();
    for (const t of timings) byCell.set(t.cell, [...(byCell.get(t.cell) ?? []), t.ms]);
    const median = a => [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)];
    const memory = await evaluate('window.isoform.memory()');
    const mb = b => (b == null ? '?' : `${(b / 2 ** 20).toFixed(0)} MB`);
    console.log(`info  previews (this page): ${[...byCell].map(([cell, ms]) => `${cell} mm ×${ms.length} median ${median(ms).toFixed(0)} ms`).join(', ') || 'none'}; `
      + `wasm memory: primary ${mb(memory.primary)}, preview ${mb(memory.preview)}`);
  }

  return { kinds, demo, costs };
}
