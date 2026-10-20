// Opens every example in headless Edge or Chrome, waits for the part to regenerate, checks the stats line and
// saves a screenshot to tests/playground/out/screenshots; then picks a face and pins one. Exits non-zero if anything
// fails.
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

const site = await serve(root);
const browser = await launch();
let failures = 0, picking = false, pins = false;
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

  // Reference pins: pin plate/z+ on example 0, cut a slot across it, and the pin reports it split in two.
  const pinProblems = [];
  try {
    await page.open(`${site.url}?example=0`);
    await page.waitFor(settled, { timeout });
    const pinned = await page.evaluate(`window.isoform.pin('plate/z+')`);
    if (pinned?.status !== 'resolved') pinProblems.push(`pinned as ${JSON.stringify(pinned)}`);
    const slot = { insert: { id: 'slot', type: 'block', op: 'cut', origin: [60, -1, 5], size: [5, 52, 10] }, before: 'fillet' };
    const { text } = JSON.parse(kernel.Apply(await page.evaluate('window.isoform.document()'), JSON.stringify(slot)));
    await page.evaluate(`window.isoform.edit(${JSON.stringify(text)})`);
    await page.waitFor(`(p => p?.status === 'split' && p.faces.length === 2)(window.isoform.pins()[0])`, { timeout });
    await page.evaluate(`document.querySelector('#references li').click()`);
    const shown = await page.evaluate(`(li => [li.querySelector('.chip').textContent, li.classList.contains('lit')])(document.querySelector('#references li'))`);
    if (shown.join() !== 'split,true') pinProblems.push(`the pin shows ${JSON.stringify(shown)}`);
    await page.frames();
    await page.screenshot(join(shots, 'pins.png'));
  } catch (e) {
    pinProblems.push(e.message);
  }
  pinProblems.push(...page.errors);
  console.log(`${pinProblems.length ? 'FAIL' : 'pass'}  pins: plate/z+ pinned, split by a slot across it`);
  for (const p of pinProblems) console.log(`        ${p}`);
  pins = !pinProblems.length;
} finally {
  await browser.close();
  await site.close();
}
console.log(`\n${names.length - failures}/${names.length} examples passed, picking ${picking ? 'passed' : 'FAILED'}, pins ${pins ? 'passed' : 'FAILED'}; screenshots in ${relative(repo, shots)}`);
process.exitCode = failures || !picking || !pins ? 1 : 0;
