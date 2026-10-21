// Share links (wwwroot/js/lib/share.js): the document packed into the URL fragment and back. node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { base64url, fromBase64url, hashLength, linkTo, pack, readFragment, sameHash, unpack } from '../../src/Isoform.Playground/wwwroot/js/lib/share.js';

const doc = JSON.stringify({
  format: 'isoform/0', units: 'mm',
  features: [
    { id: 'plate', type: 'block', op: 'new', origin: [0, 0, 0], size: [100, 50, 10] },
    { id: 'hole', type: 'hole', center: [30, 25], diameter: 10 },
  ],
}, null, 2);
const hash = 'd98dc59b' + '0123456789abcdef'.repeat(3) + '01234567';

test('pack and unpack round trip, UTF-8 included', async () => {
  assert.equal(await unpack(await pack(doc)), doc);
  const odd = 'Ø 12 mm · “quoted” · 寸法 · 😀\n\t\\"';
  assert.equal(await unpack(await pack(odd)), odd);
  assert.equal(await unpack(await pack('')), '');
});

test('the packed text is URL-safe: base64url, no padding', async () => {
  const packed = await pack(doc);
  assert.match(packed, /^[A-Za-z0-9_-]+$/);
  assert.equal(encodeURIComponent(packed), packed);
  for (const n of [0, 1, 2, 3, 4, 255]) {
    const bytes = Uint8Array.from({ length: n }, (_, i) => 255 - i);
    assert.match(base64url(bytes), /^[A-Za-z0-9_-]*$/);
    assert.deepEqual(fromBase64url(base64url(bytes)), bytes);
  }
});

test('a long document: compressed well below its size, and back exactly', async () => {
  const features = Array.from({ length: 4000 }, (_, i) => ({ id: `hole${i}`, type: 'hole', center: [i % 97, (i * 7) % 53], diameter: 1 + (i % 9) / 10 }));
  const long = JSON.stringify({ format: 'isoform/0', units: 'mm', features }, null, 2);
  assert.ok(long.length > 400_000);
  const packed = await pack(long);
  assert.ok(packed.length < long.length / 5, `${packed.length} of ${long.length}`);
  assert.equal(await unpack(packed), long);
});

test('linkTo drops the query and the old fragment; readFragment gives the document and hash back', async () => {
  const link = await linkTo('http://localhost:8137/index.html?example=3&workers=2#doc=old', doc, hash);
  const url = new URL(link);
  assert.equal(url.origin + url.pathname, 'http://localhost:8137/index.html');
  assert.equal(url.search, '');
  const read = readFragment(url.hash);
  assert.equal(read.hash, hash.slice(0, hashLength));
  assert.equal(await unpack(read.packed), doc);
  assert.ok(sameHash(hash, read.hash));
  assert.ok(!sameHash('ff' + hash.slice(2), read.hash));
  assert.ok(!sameHash(hash, null));
});

test('readFragment: nothing to read without a doc', () => {
  assert.equal(readFragment(''), null);
  assert.equal(readFragment('#'), null);
  assert.equal(readFragment('#hash=abc'), null);
  assert.deepEqual(readFragment('#doc=abc'), { packed: 'abc', hash: null });
});

test('unpack rejects what is not a pack', async () => {
  await assert.rejects(unpack('not-a-document'));
});
