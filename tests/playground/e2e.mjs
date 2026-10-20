// Opens every example in headless Edge or Chrome, waits for the part to regenerate, checks the stats line and
// saves a screenshot to tests/playground/out/screenshots; then picks a face, and commits, undoes and redoes an edit.
// Exits non-zero if anything fails.
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
const names = Object.keys(JSON.parse((await loadKernel(root)).Examples()));

// The stats line once the page has a result: "812 ms · 19,598 triangles · 8 faces, ..." or an error.
const settled = `(s => /\\d ms ·|Error:|does not parse/.test(s) && s)(document.getElementById('stats')?.textContent ?? '')`;

const site = await serve(root);
const browser = await launch();
let failures = 0, picking = false, history = false;
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

  // Undo is document history: an edit list commits one entry (agent view and editor flash), one that does not apply
  // commits nothing, and undo gives back the identical hash.
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
  history = !steps.length;
} finally {
  await browser.close();
  await site.close();
}
const verdict = ok => ok ? 'passed' : 'FAILED';
console.log(`\n${names.length - failures}/${names.length} examples passed, picking ${verdict(picking)}, history ${verdict(history)}; screenshots in ${relative(repo, shots)}`);
process.exitCode = failures || !picking || !history ? 1 : 0;
