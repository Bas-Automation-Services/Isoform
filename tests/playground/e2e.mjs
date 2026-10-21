// Opens every example in headless Edge or Chrome, waits for the part to regenerate, checks the stats line and
// saves a screenshot to tests/playground/out/screenshots; then picks a face, resolves a thin spot from the
// diagnostics, pins a face, commits, undoes and redoes an edit, and drags a handle; runs the guided tour and opens a
// share link. Exits non-zero if anything fails.
// node tests/playground/e2e.mjs [published wwwroot]      (env: ISOFORM_BROWSER, ISOFORM_TIMEOUT in ms)
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launch } from './browser.mjs';
import { loadKernel, publishedRoot, repo } from './published.mjs';
import { serve } from './serve.mjs';

const root = publishedRoot(process.argv[2]);
const shots = join(dirname(fileURLToPath(import.meta.url)), 'out', 'screenshots');
const timeout = +(process.env.ISOFORM_TIMEOUT ?? 120000);

// The example list from the kernel itself, so it does not depend on how the page shows it.
const kernel = await loadKernel(root);
const names = Object.keys(JSON.parse(kernel.Examples()));

// The stats line once the page has a result: "812 ms · 19,598 triangles · 8 faces, ..." or an error.
const settled = `(s => /\\d ms ·|Error:|does not parse/.test(s) && s)(document.getElementById('stats')?.textContent ?? '')`;

/**
 * Handles: on example 0, select plate/x+, drag its arrow 40 px outwards, and the plate gets wider and the hash
 * changes. Skipped while the kernel has no Handles export. Returns 'passed', 'FAILED' or 'skipped'.
 */
async function dragHandle(page) {
  if (typeof kernel.Handles !== 'function') {
    console.log('skip  handles: the kernel has no Handles export yet');
    return 'skipped';
  }
  const problems = [];
  let width;
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const before = await page.evaluate('window.isoform.hash()');
    await page.evaluate(`window.isoform.select(['plate/x+'])`);
    const h = await page.waitFor(`window.isoform.handles().find(h => h.kind === 'linear' && !h.readOnly && h.faces.includes('plate/x+'))`, { timeout: 10000 });
    await page.frames();
    const to = [h.screen[0] + 40 * h.direction[0], h.screen[1] + 40 * h.direction[1]];
    // By hand rather than page.drag, for a screenshot with the label showing mid-drag.
    await page.mouse('mouseMoved', h.screen);
    await page.mouse('mousePressed', h.screen, { buttons: 1 });
    for (let i = 1; i <= 12; i++)
      await page.mouse('mouseMoved', [h.screen[0] + (to[0] - h.screen[0]) * i / 12, h.screen[1] + (to[1] - h.screen[1]) * i / 12], { buttons: 1 });
    await page.frames();
    await page.screenshot(join(shots, 'handle-drag.png'));
    await page.mouse('mouseReleased', to);
    await page.waitFor(`window.isoform.hash() !== ${JSON.stringify(before)}`, { timeout });
    width = await page.evaluate(`JSON.parse(window.isoform.document()).features.find(f => f.id === 'plate').size[0]`);
    if (!(width > 100)) problems.push(`plate.size.0 is ${width}, not more than 100`);
    if (!(await page.evaluate('window.isoform.selection()')).includes('plate/x+')) problems.push('plate/x+ is no longer selected');
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  handles: dragged plate/x+ to plate.size.0 = ${width}, the hash changed`);
  for (const p of problems) console.log(`        ${p}`);
  return problems.length ? 'FAILED' : 'passed';
}

const site = await serve(root);
const browser = await launch();
let failures = 0, picking = false, diagnosing = false, pinning = false, committing = false, handles = 'skipped', inspector = false;
let touring = false, sharing = false;
try {
  const page = await browser.newPage();
  for (const [i, name] of names.entries()) {
    const start = performance.now();
    const problems = [];
    let stats = '';
    try {
      await page.open(`${site.url}?example=${i}`);
      stats = await page.waitFor(settled, { timeout });
      await page.frames();
      await page.screenshot(join(shots, `example-${i}.png`));
    } catch (e) {
      problems.push(e.message);
    }
    if (/Error:|does not parse/.test(stats)) problems.push(stats);
    else if (stats && !/\b[1-9]\d* faces/.test(stats)) problems.push(`no faces: ${stats}`);
    problems.push(...page.errors);

    const page_s = ((performance.now() - start) / 1000).toFixed(1);
    const kernel_ms = stats.match(/^(\d+) ms/)?.[1] ?? '-';
    const faces = stats.match(/(\d+) faces/)?.[1] ?? '-';
    console.log(`${problems.length ? 'FAIL' : 'pass'}  ${String(i).padStart(2)}  kernel ${kernel_ms.padStart(5)} ms  page ${page_s.padStart(5)} s  ${faces.padStart(3)} faces  ${name}`);
    for (const p of problems) console.log(`        ${p}`);
    if (problems.length) failures++;
  }

  // Picking: a click selects the face under the pointer by name, and the selection survives a regeneration.
  const problems = [];
  let selected = [];
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const [x, y] = await page.center('#view canvas');
    // The middle of the view may look through a hole: try around it.
    for (const [dx, dy] of [[0, 0], [80, 40], [-80, 40], [80, -40], [-80, -40]]) {
      await page.click([x + dx, y + dy]);
      selected = await page.evaluate('window.isoform.selection()');
      if (selected.length) break;
    }
    if (!selected.length) throw new Error('no face under the pointer');
    await page.evaluate(`(c => { c.value = '1'; c.dispatchEvent(new Event('change')); })(document.getElementById('cell'))`);
    await page.waitFor(settled, { timeout });
    const after = await page.evaluate('window.isoform.selection()');
    if (after.join() !== selected.join()) problems.push(`selection ${JSON.stringify(selected)} became ${JSON.stringify(after)}`);
    if (await page.evaluate(`document.getElementById('selection').hidden`)) problems.push('the selection box is hidden');
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  picking: ${JSON.stringify(selected)} kept across a regeneration`);
  for (const p of problems) console.log(`        ${p}`);
  picking = !problems.length;

  diagnosing = await diagnostics(page);
  pinning = await pins(page);
  committing = await commits(page);
  handles = await dragHandle(page);
  inspector = await inspecting(page);
  [touring, sharing] = [await tour(page), await share(page)]; // T1
} finally {
  await browser.close();
  await site.close();
}
console.log(`\n${names.length - failures}/${names.length} examples passed, picking ${picking ? 'passed' : 'FAILED'}, `
  + `diagnostics ${diagnosing ? 'passed' : 'FAILED'}, pins ${pinning ? 'passed' : 'FAILED'}, history ${committing ? 'passed' : 'FAILED'}, handles ${handles}, inspector ${inspector ? 'passed' : 'FAILED'}, `
  + `tour ${touring ? 'passed' : 'FAILED'}, share ${sharing ? 'passed' : 'FAILED'}; screenshots in ${relative(repo, shots)}`);
process.exitCode = failures || !picking || !diagnosing || !pinning || !committing || handles === 'FAILED' || !inspector || !touring || !sharing ? 1 : 0;

/**
 * Diagnostics in place: example 0 with its hole moved to leave a 0.6 mm wall gives a below-resolution thin spot at
 * 0.5 mm cells. Clicking it lights both faces in two colours and flies the camera there; Resolve regenerates at
 * 0.25 mm cells, where the wall is resolved and the report is gone; "back to" returns to the default cell. Then a
 * fillet too large to fit gives fillet-invalid, lights the edge it names, and leaves the rest of the part.
 */
async function diagnostics(page) {
  const problems = [];
  let found = null;
  const clickOn = async selector => {
    await page.evaluate(`document.querySelector(${JSON.stringify(selector)})?.scrollIntoView({ block: 'center' })`);
    const at = await page.center(selector);
    if (!at) throw new Error(`nothing matches ${selector}`);
    await page.click(at);
  };
  const verdict = () => page.evaluate(`document.getElementById('verdict').textContent`);
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    await page.evaluate(`(d => { d.features.find(f => f.id === 'hole').center = [30, 44.4]; return window.isoform.edit(JSON.stringify(d, null, 2)); })(JSON.parse(window.isoform.document()))`);
    const items = await page.evaluate('window.isoform.diagnostics()');
    found = items.find(d => d.code === 'below-resolution' && d.thickness);
    if (!found) throw new Error(`no below-resolution thin spot: ${JSON.stringify(items)}`);
    if (found.faces.join() !== 'plate/y+,hole/wall') problems.push(`the thin spot names ${JSON.stringify(found.faces)}`);
    if (found.resolve !== 0.25) problems.push(`resolve offers ${found.resolve} mm, not 0.25`);
    if (!/1 problem/.test(await verdict())) problems.push(`the verdict says ${await verdict()}`);

    const item = `#diagnostics li.item[data-index="${found.index}"]`;
    await clickOn(item);
    const marked = await page.evaluate('window.isoform.marked()');
    const colours = Object.fromEntries(marked.map(m => [m.face, m.color]));
    if (!colours['plate/y+'] || !colours['hole/wall'] || colours['plate/y+'] === colours['hole/wall'])
      problems.push(`both faces should be lit in two colours: ${JSON.stringify(marked)}`);
    // The thin spot is on the y+ face, near y = 49.7.
    await page.waitFor(`(t => t[1] > 49 && t[1] < 50.5)(window.isoform.target())`, { timeout: 5000 })
      .catch(e => problems.push(`the camera did not fly to the thin spot: ${e.message}`));
    await page.frames();
    await page.screenshot(join(shots, 'diagnostics.png'));

    const hash = await page.evaluate('window.isoform.hash()');
    await clickOn(`${item} button.resolve`);
    await page.waitFor(`window.isoform.cell() === 0.25`, { timeout });
    const after = await page.evaluate('window.isoform.diagnostics()');
    if (after.some(d => d.code === 'below-resolution')) problems.push(`still reported at 0.25 mm: ${JSON.stringify(after)}`);
    if (await page.evaluate('window.isoform.hash()') !== hash) problems.push('resolving changed the document');
    if (await page.evaluate(`document.getElementById('cell').value`) !== '0.25') problems.push('the cell select does not show 0.25 mm');
    if (!/^Valid/.test(await verdict())) problems.push(`after resolving the verdict says ${await verdict()}`);

    await clickOn('#diagnostics li.cell button');
    await page.waitFor(`window.isoform.cell() === 0.5`, { timeout });
    if (!(await page.evaluate('window.isoform.diagnostics()')).some(d => d.code === 'below-resolution'))
      problems.push('back at 0.5 mm the thin spot is not reported again');

    // A fillet radius past what fits: a precise fillet-invalid naming the edge, and the rest of the part stays.
    await page.evaluate(`(d => { d.features.find(f => f.id === 'fillet').radius = 12; return window.isoform.edit(JSON.stringify(d, null, 2)); })(JSON.parse(window.isoform.document()))`);
    const fillet = (await page.evaluate('window.isoform.diagnostics()')).find(d => d.code === 'fillet-invalid');
    if (!fillet) problems.push('no fillet-invalid for a radius of 12 mm');
    else {
      if (!/problem/.test(await verdict())) problems.push(`with fillet-invalid the verdict says ${await verdict()}`);
      if (!(await page.evaluate('window.isoform.faces()')).some(f => f[0] === 'hole/wall')) problems.push('the rest of the part is gone');
      await page.evaluate(`window.isoform.focusDiagnostic(${fillet.index})`);
      const lit = (await page.evaluate('window.isoform.marked()')).map(m => m.face).sort().join();
      if (lit !== 'plate/x+,plate/z+') problems.push(`fillet-invalid lights ${lit}, not the edge plate/z+ · plate/x+`);
    }
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  const what = found ? `${found.thickness.toFixed(3)} mm between ${found.faces.join(' and ')}` : '-';
  console.log(`${problems.length ? 'FAIL' : 'pass'}  diagnostics: thin spot ${what} lit, resolved at 0.25 mm and back; fillet-invalid lights its edge`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}

/** Reference pins: plate/z+ pinned on example 0, a slot cut across it, and the pin reports it split in two. */
async function pins(page) {
  const problems = [];
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const pinned = await page.evaluate(`window.isoform.pin('plate/z+')`);
    if (pinned?.status !== 'resolved') problems.push(`pinned as ${JSON.stringify(pinned)}`);
    const slot = { insert: { id: 'slot', type: 'block', op: 'cut', origin: [60, -1, 5], size: [5, 52, 10] }, before: 'fillet' };
    const { text } = JSON.parse(kernel.Apply(await page.evaluate('window.isoform.document()'), JSON.stringify(slot)));
    await page.evaluate(`window.isoform.edit(${JSON.stringify(text)})`);
    await page.waitFor(`(p => p?.status === 'split' && p.faces.length === 2)(window.isoform.pins()[0])`, { timeout });
    await page.evaluate(`document.querySelector('#references li').click()`);
    const shown = await page.evaluate(`(li => [li.querySelector('.chip').textContent, li.classList.contains('lit')])(document.querySelector('#references li'))`);
    if (shown.join() !== 'split,true') problems.push(`the pin shows ${JSON.stringify(shown)}`);
    await page.frames();
    await page.screenshot(join(shots, 'pins.png'));
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  pins: plate/z+ pinned, split by a slot across it`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}

/**
 * Undo is document history: an edit list commits one entry (agent view and editor flash), one that does not apply
 * commits nothing, undo gives back the identical hash and redo the edited one.
 */
async function commits(page) {
  const steps = [];
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const original = await page.evaluate('window.isoform.hash()');
    const edited = await page.evaluate(`window.isoform.applyEdits([{ set: 'plate.size.0', value: 120 }], 'e2e', 'test')`);
    if (!edited?.hash || edited.hash === original) throw new Error(`applyEdits gave ${JSON.stringify(edited)}`);
    const shown = await page.evaluate(`[document.getElementById('agent').dataset.hash, document.getElementById('agent').textContent,
      document.querySelectorAll('.editor-flash.on div').length]`);
    if (shown[0] !== edited.hash || !shown[1].includes('plate.size.0') || !shown[1].includes('→')) steps.push(`agent view: ${shown[1].slice(0, 200)}`);
    if (!shown[2]) steps.push('no lines flashed in the editor');
    const rejected = await page.evaluate(`window.isoform.applyEdits([{ remove: 'slot' }], 'e2e', 'test')`);
    if (!rejected?.error || await page.evaluate('window.isoform.hash()') !== edited.hash) steps.push(`a bad edit gave ${JSON.stringify(rejected)}`);
    const undone = await page.evaluate('window.isoform.undo()');
    if (undone?.hash !== original) steps.push(`undo gave ${undone?.hash}, not the original ${original}`);
    const { at, entries } = await page.evaluate('window.isoform.history()');
    if (at !== 0 || entries.length !== 2 || entries[1].label !== 'e2e') steps.push(`history ${JSON.stringify({ at, entries })}`);
    const redone = await page.evaluate('window.isoform.redo()');
    if (redone?.hash !== edited.hash) steps.push(`redo gave ${redone?.hash}, not ${edited.hash}`);
    await page.screenshot(join(shots, 'history.png'));
  } catch (e) {
    steps.push(e.message);
  }
  steps.push(...page.errors);
  console.log(`${steps.length ? 'FAIL' : 'pass'}  history: apply, reject, undo to the original hash, redo`);
  for (const p of steps) console.log(`        ${p}`);
  return !steps.length;
}

/**
 * Tree and inspector: select a feature in the tree, type a number, scrub it (the document and its hash follow, the
 * feature stays selected), re-pick a fillet's edge, and pick a face to select its feature.
 */
async function inspecting(page) {
  const problems = [];
  const diameter = `JSON.parse(window.isoform.document()).features.find(f => f.id === 'hole').diameter`;
  const field = `document.querySelector('#inspector [data-path="hole.diameter"]')`;
  const seen = [];
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const hash = await page.evaluate('window.isoform.hash()');
    await page.click(await page.center('#tree li[data-key="hole"]'));
    await page.waitFor(`window.isoform.feature() === 'hole' && !!${field}`);
    await page.evaluate(`(i => { i.value = '12'; i.dispatchEvent(new Event('change')); })(${field})`);
    await page.waitFor(`${diameter} === 12 && window.isoform.hash() !== ${JSON.stringify(hash)} && !!${field}`, { timeout });
    seen.push(12);
    const label = await page.center('#inspector [data-scrub="hole.diameter"]');
    await page.drag(label, [label[0] + 40, label[1]]);
    seen.push(await page.waitFor(`(d => d > 12 && d)(${diameter})`, { timeout }));
    if (await page.evaluate('window.isoform.feature()') !== 'hole') problems.push('the feature is no longer selected after the edit');
    // Re-pick the fillet's edge: the next two faces picked (as a shift+click would) replace it.
    await page.click(await page.center('#tree li[data-key="fillet"]'));
    await page.waitFor(`!!document.querySelector('#inspector .repick')`);
    await page.evaluate(`document.querySelector('#inspector .repick').click()`);
    await page.evaluate(`window.isoform.select(['plate/z+', 'plate/y-'])`);
    await page.waitFor(`JSON.parse(window.isoform.document()).features.find(f => f.id === 'fillet').edge.join() === 'plate/z+,plate/y-'`, { timeout });
    await page.evaluate(`window.isoform.select(['plate/z+'])`);
    await page.waitFor(`window.isoform.feature() === 'plate' && document.querySelector('#tree li.selected')?.dataset.key === 'plate'`);
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  inspector: hole.diameter set to ${seen.join(', then scrubbed to ')}; fillet edge re-picked; a face selects its feature`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}

/**
 * Guided tour (T1): start it on example 0, step to the end with Next (and once Back), and check that every step's
 * expected diagnostic appeared, the pins changed status, the edits were committed as the tour's, and the last undo
 * gave the hash it started from. Escape exits.
 */
async function tour(page) {
  const problems = [];
  const { script, pinned } = await import(new URL('../../src/Isoform.Playground/wwwroot/js/panels/tour.js', import.meta.url));
  let log = [];
  // After the camera's flight to what the step is about (650 ms).
  const still = async i => {
    await page.evaluate('new Promise(done => setTimeout(done, 800))');
    await page.frames();
    await page.screenshot(join(shots, `tour-${i}.png`));
  };
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    await page.evaluate('window.isoform.tour.start()');
    const faces = (await page.evaluate('window.isoform.pins()')).map(p => p.ref.face);
    if (pinned.some(f => !faces.includes(f))) problems.push(`pinned ${JSON.stringify(faces)}`);
    for (let i = 1; i < script.length; i++) {
      await page.evaluate('window.isoform.tour.next()');
      if (i === 2) {
        // Back returns to the step before, its hash; Next runs the step again.
        await page.evaluate('window.isoform.tour.back()');
        const { index, log: [, widened] } = await page.evaluate('window.isoform.tour.state()');
        if (index !== 1 || await page.evaluate('window.isoform.hash()') !== widened.hash) problems.push(`back went to step ${index}`);
        await page.evaluate('window.isoform.tour.next()');
      }
      if (i === 2 || i === 5) await still(i);
    }
    ({ log } = await page.evaluate('window.isoform.tour.state()'));
    for (const [i, step] of script.entries()) {
      const entry = log[i];
      if (!entry) { problems.push(`step ${i} did not run`); continue; }
      if (step.expect && !entry.codes.includes(step.expect)) problems.push(`step ${i} (${step.title}): no ${step.expect} in ${JSON.stringify(entry.codes)}`);
      if (step.gone && entry.codes.includes(step.gone)) problems.push(`step ${i} (${step.title}): ${step.gone} still reported`);
      if (!entry.ok) problems.push(`step ${i} (${step.title}): ${entry.problems.join('; ')}`);
    }
    if (log.at(-1)?.hash !== log[0]?.hash) problems.push(`the tour ended at ${log.at(-1)?.hash}, not the original ${log[0]?.hash}`);
    if (await page.evaluate('window.isoform.hash()') !== log[0]?.hash) problems.push('the page does not show the original hash');
    const { entries } = await page.evaluate('window.isoform.history()');
    if (entries.filter(e => e.origin === 'tour').length < 4) problems.push(`history ${JSON.stringify(entries.map(e => e.origin))}`);
    await still(script.length - 1);
    await page.evaluate(`dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))`);
    if (await page.evaluate('window.isoform.tour.state().active || !document.querySelector(".tour").hidden')) problems.push('Escape did not exit the tour');
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  const codes = script.flatMap((s, i) => s.expect && log[i]?.codes.includes(s.expect) ? [s.expect] : []);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  tour: ${log.length} steps (${codes.join(', ')}), back to the original hash ${log[0]?.hash.slice(0, 8)}`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}

/** Share link (T1): an edited example 0 shared as #doc=, opened in the page again, regenerates to the same hash. */
async function share(page) {
  const problems = [];
  let link = '';
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const { hash } = await page.evaluate(`window.isoform.applyEdits([{ set: 'hole.diameter', value: 12 }], 'e2e', 'test')`);
    link = await page.evaluate('window.isoform.shareLink()');
    const fragment = new URL(link).hash;
    if (!/^#doc=[A-Za-z0-9_-]+&hash=[0-9a-f]{16}$/.test(fragment)) problems.push(`link fragment ${fragment.slice(0, 80)}`);
    // The Share button puts the same link in the address bar (headless, the clipboard may refuse).
    await page.evaluate(`document.getElementById('share').click()`);
    await page.waitFor(`/hash/.test(window.isoform.shareStatus()) && location.href === ${JSON.stringify(link)}`, { timeout: 5000 })
      .catch(() => problems.push('the Share button did not show the link'));
    // From another page: opening the address bar's own URL would only scroll to its fragment.
    await page.open('about:blank');
    await page.open(link);
    await page.waitFor(settled, { timeout });
    const opened = await page.evaluate('window.isoform.hash()');
    if (opened !== hash) problems.push(`the link opened ${opened}, not ${hash}`);
    const status = await page.evaluate('window.isoform.shareStatus()');
    if (!/identical/.test(status)) problems.push(`share status: ${status}`);
    if (await page.evaluate('location.hash')) problems.push('the fragment stays in the address bar');
    if (await page.evaluate(`document.getElementById('examples').selectedOptions[0].textContent`) !== '(shared link)') problems.push('the example picker does not say shared');
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  share: a ${link.length}-character #doc= link opens the same document, same hash`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}
