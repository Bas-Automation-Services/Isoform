// Opens every example in headless Edge or Chrome, waits for the part to regenerate, checks the stats line and
// saves a screenshot to tests/playground/out/screenshots; then picks a face. Exits non-zero if anything fails.
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
let failures = 0, picking = false, inspector = false;
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

  // Tree and inspector: select a feature in the tree, type a number, scrub it; the document and its hash follow, and
  // picking a face selects its feature.
  inspector = await (async () => {
    const problems = [];
    const diameter = `JSON.parse(window.isoform.document()).features.find(f => f.id === 'hole').diameter`;
    const field = `document.querySelector('#inspector [data-path="hole.diameter"]')`;
    let seen = [];
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
  })();
} finally {
  await browser.close();
  await site.close();
}
console.log(`\n${names.length - failures}/${names.length} examples passed, picking ${picking ? 'passed' : 'FAILED'}, inspector ${inspector ? 'passed' : 'FAILED'}; screenshots in ${relative(repo, shots)}`);
process.exitCode = failures || !picking || !inspector ? 1 : 0;
