// The line diff (wwwroot/js/lib/diff.js) the agent panel and the editor flash use: node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { changedLines, hunks, lineDiff } from '../../src/Isoform.Playground/wwwroot/js/lib/diff.js';

const lines = text => text.split('\n');
// Before and after, rebuilt from the ops: proves the ops are a complete, ordered account of both texts.
const sides = ops => [ops.filter(o => o.op !== '+').map(o => o.line), ops.filter(o => o.op !== '-').map(o => o.line)];
const changes = ops => ops.filter(o => o.op !== ' ').map(o => o.op + o.line);

test('equal texts: every line kept, with both line numbers', () => {
  const ops = lineDiff('a\nb\nc', 'a\nb\nc');
  assert.deepEqual(ops, [{ op: ' ', a: 0, b: 0, line: 'a' }, { op: ' ', a: 1, b: 1, line: 'b' }, { op: ' ', a: 2, b: 2, line: 'c' }]);
});

test('one changed line is one removal and one addition', () => {
  const ops = lineDiff('{\n  "size": 100,\n  "z": 5\n}', '{\n  "size": 120,\n  "z": 5\n}');
  assert.deepEqual(changes(ops), ['-  "size": 100,', '+  "size": 120,']);
  assert.deepEqual(ops.find(o => o.op === '+'), { op: '+', b: 1, line: '  "size": 120,' });
  assert.deepEqual(ops.find(o => o.op === '-'), { op: '-', a: 1, line: '  "size": 100,' });
});

test('insertions and removals at both ends and in the middle', () => {
  for (const [before, after] of [['', 'a'], ['a', ''], ['a\nb', 'x\na\nb'], ['a\nb', 'a\nb\nx'], ['a\nb\nc', 'a\nc'], ['a\nb\nc\nd', 'b\nx\nd\ny']]) {
    const ops = lineDiff(before, after);
    assert.deepEqual(sides(ops), [lines(before), lines(after)], `${JSON.stringify(before)} → ${JSON.stringify(after)}`);
  }
  assert.deepEqual(changes(lineDiff('a\nb\nc', 'a\nc')), ['-b']);
});

test('the script is shortest: a moved line costs two, not the whole text', () => {
  const ops = lineDiff('a\nb\nc\nd\ne', 'b\nc\nd\ne\na');
  assert.deepEqual(changes(ops), ['-a', '+a']);
});

test('line numbers count in their own text', () => {
  const ops = lineDiff('x\na\nb', 'a\nb\ny\nz');
  assert.deepEqual(ops.filter(o => o.op === ' ').map(o => [o.a, o.b]), [[1, 0], [2, 1]]);
  assert.deepEqual(ops.filter(o => o.op === '+').map(o => o.b), [2, 3]);
});

test('a random set of texts round-trips', () => {
  let seed = 7;
  const random = n => (seed = (seed * 1103515245 + 12345) % 2147483648) % n;
  const text = () => Array.from({ length: random(12) }, () => 'abcd'[random(4)]).join('\n');
  for (let i = 0; i < 300; i++) {
    const [before, after] = [text(), text()];
    assert.deepEqual(sides(lineDiff(before, after)), [lines(before), lines(after)]);
  }
});

test('hunks keep the changes with context and mark what is skipped', () => {
  const before = Array.from({ length: 20 }, (_, i) => `l${i}`).join('\n');
  const after = before.replace('l3', 'L3').replace('l15', 'L15');
  const shown = hunks(lineDiff(before, after), 1).map(o => o ? o.op + o.line : '⋯');
  assert.deepEqual(shown, [' l2', '-l3', '+L3', ' l4', '⋯', ' l14', '-l15', '+L15', ' l16']);
  assert.deepEqual(hunks(lineDiff('a', 'a')), []);
});

test('changedLines: the added lines, or the line in place of a removal', () => {
  assert.deepEqual(changedLines(lineDiff('a\nb\nc', 'a\nB\nc')), [1]);
  assert.deepEqual(changedLines(lineDiff('a\nb\nc', 'a\nb\nx\ny\nc')), [2, 3]);
  assert.deepEqual(changedLines(lineDiff('a\nb\nc\nd', 'a\nd')), [1]);
  assert.deepEqual(changedLines(lineDiff('a\nb', 'a\nb')), []);
});
