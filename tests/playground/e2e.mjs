// Opens every example in headless Edge or Chrome, waits for the part to regenerate, checks the stats line and
// saves a screenshot to tests/playground/out/screenshots; then picks a face, resolves a thin spot from the
// diagnostics and drags a handle. Exits non-zero if anything fails.
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
let failures = 0, picking = false, diagnosing = false, handles = 'skipped';
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
  handles = await dragHandle(page);
} finally {
  await browser.close();
  await site.close();
}
console.log(`\n${names.length - failures}/${names.length} examples passed, picking ${picking ? 'passed' : 'FAILED'}, `
  + `diagnostics ${diagnosing ? 'passed' : 'FAILED'}, handles ${handles}; screenshots in ${relative(repo, shots)}`);
process.exitCode = failures || !picking || !diagnosing || handles === 'FAILED' ? 1 : 0;

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
