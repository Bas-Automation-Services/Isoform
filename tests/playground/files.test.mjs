// lib/files.js: the names of files saved from the page, how Save writes, what a DXF is of, what an export says.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { baseName, documentFile, dxfFile, dxfSource, exported, saveMode, stem, stepFile } from '../../src/Isoform.Playground/wwwroot/js/lib/files.js';

test('a file is named after the file opened, else the example, else part', () => {
  assert.equal(baseName('bracket.isoform.json'), 'bracket');
  assert.equal(baseName('Bracket.JSON'), 'Bracket');
  assert.equal(baseName(null), null);
  assert.equal(stem({ file: 'bracket.isoform.json', example: 'Plate with hole and fillet' }), 'bracket');
  assert.equal(stem({ example: 'Plate with hole and fillet' }), 'plate-with-hole-and-fillet');
  assert.equal(stem({ example: 'Bracket: extrude, inside round, hole row' }), 'bracket');
  assert.equal(stem(), 'part');
  assert.equal(documentFile('bracket'), 'bracket.isoform.json');
  assert.equal(stepFile('bracket'), 'bracket.step');
  assert.equal(dxfFile('bracket', { face: 'plate/z+' }), 'bracket-plate-z+.dxf');
  assert.equal(dxfFile('bracket', { sketch: 'base' }), 'bracket-base.dxf');
});

test('Save writes the file it has, else asks for one where the browser can, else downloads', () => {
  assert.equal(saveMode({ handle: {}, picker: true }), 'handle');
  assert.equal(saveMode({ handle: {}, picker: false }), 'handle');
  assert.equal(saveMode({ picker: true }), 'picker');
  assert.equal(saveMode({ picker: false }), 'download');
});

test('a DXF is of the sketch selected in the tree, else of the one face selected', () => {
  const doc = { format: 'isoform/0', features: [{ id: 'plate', type: 'block' }, { id: 'base', type: 'sketch' }] };
  assert.deepEqual(dxfSource({ feature: 'base', selection: ['plate/z+'] }, doc), { sketch: 'base' });
  assert.deepEqual(dxfSource({ feature: 'plate', selection: ['plate/z+'] }, doc), { face: 'plate/z+' });
  assert.equal(dxfSource({ feature: 'plate', selection: ['plate/z+', 'plate/x+'] }, doc), null, 'an edge');
  assert.equal(dxfSource({ feature: null, selection: [] }, doc), null);
  assert.deepEqual(dxfSource({ feature: 'components.pin', selection: ['pin/pin/cap'] }, { format: 'isoform-bundle/0' }), { face: 'pin/pin/cap' });
});

test('an export says what it wrote, or why not, naming the faces STEP cannot write exactly', () => {
  assert.deepEqual(exported('STEP', 'part.step', { text: 'ISO', faces: 7 }), { text: 'Downloaded part.step', bad: false });
  assert.deepEqual(exported('DXF', 'part-cut-face.dxf', { text: '0', approximated: 1 }), { text: 'Downloaded part-cut-face.dxf (1 curve as lines)', bad: false });
  assert.deepEqual(exported('STEP', 'part.step', { error: 'faces without a closed-form surface: boss/blend (Blend)', unsupported: ['boss/blend'] }),
    { text: 'No STEP: no exact form for boss/blend', bad: true });
  assert.deepEqual(exported('DXF', 'x.dxf', { error: 'hole/wall is not a flat face of the part' }), { text: 'No DXF: hole/wall is not a flat face of the part', bad: true });
});
