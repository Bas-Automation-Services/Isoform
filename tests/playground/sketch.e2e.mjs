// Sketch mode in the page (wwwroot/js/sketch/), in headless Edge or Chrome: on example 0, select plate/z+, enter
// sketch mode, draw a rectangle with the mouse, dimension its bottom line, pick its region and finish it as an
// extrude: one history entry, faces <feature>/<entity>. Then edit the sketch from the tree (a dimension changed in the
// panel saves as one set), and Escape leaves a new sketch without touching the document. Exits non-zero on failure.
// node tests/playground/sketch.e2e.mjs [published wwwroot]      (env: ISOFORM_BROWSER, ISOFORM_TIMEOUT, ISOFORM_SERVER)
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launch } from './browser.mjs';
import { publishedRoot } from './published.mjs';
import { serve } from './serve.mjs';

const root = publishedRoot(process.argv[2]);
const shots = join(dirname(fileURLToPath(import.meta.url)), 'out', 'screenshots');
const timeout = +(process.env.ISOFORM_TIMEOUT ?? 120000);
const settled = `(s => /\\d ms ·|Error:|does not parse/.test(s) && s)(document.getElementById('stats')?.textContent ?? '')`;

const site = process.env.ISOFORM_SERVER ? { url: process.env.ISOFORM_SERVER, close: async () => {} } : await serve(root);
const browser = await launch();
const page = await browser.newPage();
const sketch = 'window.isoform.sketch';
const state = () => page.evaluate(`${sketch}.state()`);
const at = p => page.evaluate(`${sketch}.toScreen(${JSON.stringify(p)})`);
const button = async selector => page.click(await page.center(selector));
// A solve that has caught up with the last change.
const solved = (extra = "true") => page.waitFor(`(s => s && s.current && (${extra}) && s)(${sketch}.state())`, { timeout: 10000 });

async function draw() {
  const problems = [];
  let faces = [];
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const before = await page.evaluate('window.isoform.history()');
    const hash = await page.evaluate('window.isoform.hash()');
    await page.evaluate(`window.isoform.select(['plate/z+'])`);
    await button('.sketch-enter');
    await page.waitFor(`${sketch}.active()`, { timeout: 10000 });
    await solved();

    // A rectangle: two clicks at opposite corners.
    await button('.sketch-bar button[title^="Rectangle"]');
    await page.click(await at([60, 10]));
    await page.click(await at([85, 35]));
    let s = await solved('s.sketch.entities.length === 4');
    const kinds = s.sketch.constraints.map(c => Object.keys(c)[0]).join();
    if (kinds !== 'horizontal,vertical,horizontal,vertical') problems.push(`rectangle constraints ${kinds}`);
    if (s.freeDegrees !== 4) problems.push(`a free rectangle has ${s.freeDegrees} free degrees, not 4`);

    // A length on the bottom line: click it, click where the dimension goes, type the value.
    await button('.sketch-bar button[title^="Dimension"]');
    await page.click(await at([72.5, 10]));
    await page.click(await at([72.5, 2]));
    await page.waitFor(`!document.querySelector('.sketch-value').hidden`, { timeout: 5000 });
    await page.evaluate(`(i => { i.value = '20'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })(document.querySelector('.sketch-value input'))`);
    s = await solved(`s.sketch.constraints.some(c => c.length === 'e1' && c.value === 20) && Math.abs(s.sketch.points.p2[0] - s.sketch.points.p1[0] - 20) < 1e-6`);
    if (s.freeDegrees !== 3) problems.push(`with the length ${s.freeDegrees} free degrees, not 3`);
    if (s.regions.length !== 1) problems.push(`${s.regions.length} regions`);
    await page.frames();
    await page.screenshot(join(shots, 'sketch-draw.png'));

    // Its region picked, a depth, and Finish.
    await button('.sketch-bar button[title^="Select"]');
    await page.click(await at([70, 20]));
    s = await state();
    if (s.picked.length !== 1) problems.push(`picked ${JSON.stringify(s.picked)}`);
    await page.evaluate(`(i => { i.value = '5'; i.dispatchEvent(new Event('change')); })(document.querySelector('.sketch-finish input.amount'))`);
    await button('.sketch-finish button.primary');
    await page.waitFor(`!${sketch}.active() && window.isoform.hash() !== ${JSON.stringify(hash)}`, { timeout });
    await page.waitFor(settled, { timeout });
    faces = (await page.evaluate('window.isoform.faces()')).map(f => f[0]);
    for (const f of ['extrude1/e1', 'extrude1/e2', 'extrude1/e3', 'extrude1/e4', 'extrude1/end'])
      if (!faces.includes(f)) problems.push(`no face ${f}`);
    const after = await page.evaluate('window.isoform.history()');
    const entry = after.entries.at(-1);
    if (after.entries.length !== before.entries.length + 1) problems.push(`${after.entries.length - before.entries.length} history entries, not 1`);
    if (entry.origin !== 'sketch' || entry.edits.length !== 2 || !entry.edits[0].insert || entry.edits[1].insert?.type !== 'extrude')
      problems.push(`history entry ${JSON.stringify({ origin: entry.origin, label: entry.label, edits: entry.edits.map(e => Object.keys(e)) })}`);
    const pad = JSON.parse(await page.evaluate('window.isoform.document()')).features.find(f => f.id === 'extrude1');
    if (pad?.end !== 5 || pad.sketch !== 'sketch1') problems.push(`extrude1 ${JSON.stringify(pad)}`);
    if ((await page.evaluate('window.isoform.diagnostics()')).some(d => /sketch/.test(d.code))) problems.push('sketch diagnostics after the finish');
    await page.frames();
    await page.screenshot(join(shots, 'sketch-extrude.png'));
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  sketch: a rectangle on plate/z+, dimensioned and extruded to ${faces.filter(f => f.startsWith('extrude1/')).join(', ')}`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}

// Editing the sketch just made: double-click it in the tree, change its dimension in the panel, save.
async function edit() {
  const problems = [];
  try {
    const hash = await page.evaluate('window.isoform.hash()');
    const count = (await page.evaluate('window.isoform.history()')).entries.length;
    const [x, y] = await page.center('#tree li[data-key="sketch1"]');
    await page.mouse('mouseMoved', [x, y]);
    await page.mouse('mousePressed', [x, y], { button: 'left', buttons: 1, clickCount: 1 });
    await page.mouse('mouseReleased', [x, y], { button: 'left', clickCount: 1 });
    await page.mouse('mousePressed', [x, y], { button: 'left', buttons: 1, clickCount: 2 });
    await page.mouse('mouseReleased', [x, y], { button: 'left', clickCount: 2 });
    await page.waitFor(`${sketch}.active() && ${sketch}.state().editing`, { timeout: 10000 });
    await solved();
    await page.evaluate(`(i => { i.focus(); i.value = '30'; i.blur(); })(document.querySelector('.sketch-panel .constraints input.value'))`);
    const s = await solved(`s.sketch.constraints.some(c => c.length === 'e1' && c.value === 30) && Math.abs(s.sketch.points.p2[0] - s.sketch.points.p1[0] - 30) < 1e-6`);
    if (s.freeDegrees !== 3) problems.push(`${s.freeDegrees} free degrees`);
    await button('.sketch-finish button.primary');
    await page.waitFor(`!${sketch}.active() && window.isoform.hash() !== ${JSON.stringify(hash)}`, { timeout });
    await page.waitFor(settled, { timeout });
    const { entries } = await page.evaluate('window.isoform.history()');
    if (entries.length !== count + 1 || entries.at(-1).edits.length !== 1 || entries.at(-1).edits[0].set !== 'sketch1')
      problems.push(`history ${JSON.stringify(entries.slice(count).map(e => e.edits))}`);
    const stored = JSON.parse(await page.evaluate('window.isoform.document()')).features.find(f => f.id === 'sketch1');
    if (Math.abs(stored.points.p2[0] - stored.points.p1[0] - 30) > 1e-6) problems.push(`stored points ${JSON.stringify(stored.points)}`);

    // Escape leaves a new sketch, with a line drawn in it, and the document as it was.
    const now = await page.evaluate('window.isoform.hash()');
    await page.evaluate(`window.isoform.select(['plate/z+'])`);
    await page.evaluate(`dispatchEvent(new KeyboardEvent('keydown', { key: 's' }))`);
    await page.waitFor(`${sketch}.active() && !${sketch}.state().editing`, { timeout: 10000 });
    await page.evaluate(`dispatchEvent(new KeyboardEvent('keydown', { key: 'l' }))`);
    await page.click(await at([10, 40]));
    await page.click(await at([20, 45]));
    await solved('s.sketch.entities.length === 1');
    for (let i = 0; i < 3 && await page.evaluate(`${sketch}.active()`); i++) await page.evaluate(`dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))`);
    if (await page.evaluate(`${sketch}.active()`)) problems.push('Escape did not leave sketch mode');
    if (await page.evaluate('window.isoform.hash()') !== now) problems.push('leaving changed the document');
  } catch (e) {
    problems.push(e.message);
  }
  problems.push(...page.errors);
  console.log(`${problems.length ? 'FAIL' : 'pass'}  sketch edit: sketch1 from the tree, its length set to 30 as one set; Escape leaves a new one untouched`);
  for (const p of problems) console.log(`        ${p}`);
  return !problems.length;
}

let ok = false;
try {
  ok = await draw();
  ok = await edit() && ok;
} finally {
  await browser.close();
  await site.close();
}
console.log(`\nsketch mode ${ok ? 'passed' : 'FAILED'}`);
process.exitCode = ok ? 0 : 1;
