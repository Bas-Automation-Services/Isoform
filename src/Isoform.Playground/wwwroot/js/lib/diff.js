// Line diff of two texts (Myers, O(ND)): the agent panel shows it for the canonical text, the editor flash uses it to
// find the changed lines. Pure, so it is tested in Node.

/**
 * The lines of before and after as ops in order: {op: ' ', a, b, line} for a kept line, {op: '-', a, line} for a
 * removed one, {op: '+', b, line} for an added one; a and b are 0-based line numbers in before and after.
 */
export function lineDiff(before, after) {
  const a = before.split('\n'), b = after.split('\n');
  // The common head and tail cost nothing; most edits change a line or two of a long document.
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let endA = a.length, endB = b.length;
  while (endA > head && endB > head && a[endA - 1] === b[endB - 1]) { endA--; endB--; }

  const ops = [];
  for (let i = 0; i < head; i++) ops.push({ op: ' ', a: i, b: i, line: a[i] });
  for (const o of myers(a.slice(head, endA), b.slice(head, endB)))
    ops.push(o.op === '+' ? { op: '+', b: o.b + head, line: o.line }
      : o.op === '-' ? { op: '-', a: o.a + head, line: o.line }
        : { op: ' ', a: o.a + head, b: o.b + head, line: o.line });
  for (let i = endA, j = endB; i < a.length; i++, j++) ops.push({ op: ' ', a: i, b: j, line: a[i] });
  return ops;
}

// The shortest edit script: v[k] is the furthest x on diagonal k = x - y; trace keeps v per round to walk back.
function myers(a, b) {
  const n = a.length, m = b.length, off = n + m + 1;
  const v = new Int32Array(2 * off + 1);
  const trace = [];
  for (let d = 0; d <= n + m; d++) {
    trace.push(v.slice());
    for (let k = -d; k <= d; k += 2) {
      let x = k === -d || (k !== d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
      let y = x - k;
      while (x < n && y < m && a[x] === b[y]) { x++; y++; }
      v[off + k] = x;
      if (x >= n && y >= m) return backtrack(trace, a, b, off);
    }
  }
  return [];
}

function backtrack(trace, a, b, off) {
  const ops = [];
  let x = a.length, y = b.length;
  for (let d = trace.length - 1; d >= 0; d--) {
    const v = trace[d], k = x - y;
    const prevK = k === -d || (k !== d && v[off + k - 1] < v[off + k + 1]) ? k + 1 : k - 1;
    const prevX = v[off + prevK], prevY = prevX - prevK;
    while (x > prevX && y > prevY) { x--; y--; ops.push({ op: ' ', a: x, b: y, line: a[x] }); }
    if (d > 0) {
      if (x === prevX) { y--; ops.push({ op: '+', b: y, line: b[y] }); }
      else { x--; ops.push({ op: '-', a: x, line: a[x] }); }
    }
  }
  return ops.reverse();
}

/** The changes with `context` kept lines around each, as runs of ops; null between two runs stands for skipped lines. */
export function hunks(ops, context = 2) {
  const near = new Set();
  ops.forEach((o, i) => {
    if (o.op === ' ') return;
    for (let j = Math.max(0, i - context); j <= Math.min(ops.length - 1, i + context); j++) near.add(j);
  });
  const out = [];
  ops.forEach((o, i) => {
    if (!near.has(i)) return;
    if (out.length && !near.has(i - 1)) out.push(null);
    out.push(o);
  });
  return out;
}

/**
 * The lines of after (0-based) to light up: the added ones, and where only lines were removed, the line that now
 * stands in their place.
 */
export function changedLines(ops) {
  const lines = new Set();
  ops.forEach((o, i) => {
    if (o.op === '+') lines.add(o.b);
    // The last line of a removal that nothing was added for: light the kept line after it.
    else if (o.op === '-' && ops[i + 1]?.op === ' ' && ops[i - 1]?.op !== '+') lines.add(ops[i + 1].b);
  });
  return [...lines].sort((p, q) => p - q);
}
