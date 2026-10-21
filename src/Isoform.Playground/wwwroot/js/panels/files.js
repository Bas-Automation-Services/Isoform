// Open and Save of .isoform.json documents, and STEP and DXF downloads (lib/files.js decides names and modes). With
// the File System Access API (Edge, Chrome) Open remembers the file, so Save writes it again; elsewhere Open reads an
// uploaded file and Save downloads one. Opening replaces the editor text and regenerates: one history entry.
import { documentOf } from '../lib/features.js';
import { documentFile, dxfFile, dxfSource, exported, saveMode, stem, stepFile } from '../lib/files.js';
import { pretty } from './editor.js';

const types = [{ description: 'Isoform document', accept: { 'application/json': ['.json'] } }];

/** buttons: {open, save, step, dxf}; status: the line that says what happened; call is kernelClient.call. */
export function createFiles(store, { buttons, status, examplesBox, regenerate, call }) {
  let handle = null; // the file opened or saved, where Save writes again
  let file = null;   // its name

  const say = (text, bad = false) => {
    status.textContent = text;
    status.className = `share-status ${bad ? 'bad' : 'good'}`;
  };
  const current = () => stem({ file, example: examplesBox.value || null });
  // Another example is another document: Save must not write it over the file.
  examplesBox.addEventListener('change', () => { handle = file = null; });

  function download(name, text, type) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([text], { type }));
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  // The fallback for Open: a file input, resolving to the file chosen or null.
  const upload = () => new Promise(resolve => {
    const input = Object.assign(document.createElement('input'), { type: 'file', accept: '.json' });
    input.onchange = () => resolve(input.files[0] ?? null);
    input.oncancel = () => resolve(null);
    input.click();
  });

  /** Opens a document: replaces the editor text and regenerates it. Resolves to the result, or null when cancelled. */
  async function open(chosen) {
    let picked = null;
    if (!chosen && window.showOpenFilePicker) {
      try { [picked] = await showOpenFilePicker({ types }); } catch { return null; } // cancelled
      chosen = await picked.getFile();
    }
    chosen ??= await upload();
    if (!chosen) return null;
    let text;
    try { text = pretty(JSON.parse(await chosen.text())); } catch {
      say(`${chosen.name} is not a JSON document`, true);
      return null;
    }
    [handle, file] = [picked, chosen.name];
    let option = [...examplesBox.options].find(o => o.dataset.file);
    if (!option) {
      option = new Option('', '');
      option.dataset.file = '1';
      examplesBox.add(option, 0);
    }
    option.text = `(${chosen.name})`;
    option.selected = true;
    store.set({ text, selection: [] });
    const result = await regenerate({ fit: true, origin: 'file', label: `open ${chosen.name}` });
    if (result) say(result.error ? `Opened ${chosen.name}: ${result.error}` : `Opened ${chosen.name}`, !!result.error);
    return result;
  }

  /** Saves the editor text: to the file opened or saved before, else to one picked, else as a download. */
  async function save() {
    const text = store.state.text;
    const mode = saveMode({ handle, picker: !!window.showSaveFilePicker });
    if (mode === 'download') {
      download(documentFile(current()), text, 'application/json');
      return say(`Downloaded ${documentFile(current())}`);
    }
    if (mode === 'picker') {
      try { handle = await showSaveFilePicker({ suggestedName: documentFile(current()), types }); } catch { return; } // cancelled
      file = handle.name;
    }
    const writable = await handle.createWritable();
    await writable.write(text);
    await writable.close();
    say(`Saved ${handle.name}`);
  }

  /** STEP of the committed result. Resolves to the kernel's reply. */
  async function step() {
    const name = current();
    const { result } = await call('step', { name });
    if (result.text) download(stepFile(name), result.text, 'model/step');
    const { text, bad } = exported('STEP', stepFile(name), result);
    say(text, bad);
    return result;
  }

  /** DXF of the selected sketch or flat face. Resolves to the kernel's reply, or null with nothing to draw. */
  async function dxf() {
    const source = dxfSource(store.state, documentOf(store.state.result));
    if (!source) return null;
    const { result } = await call('dxf', { document: store.state.result.text, ...source });
    const name = dxfFile(current(), source);
    if (result.text) download(name, result.text, 'image/vnd.dxf');
    const { text, bad } = exported('DXF', name, result);
    say(text, bad);
    return result;
  }

  buttons.open.onclick = () => open();
  buttons.save.onclick = save;
  buttons.step.onclick = step;
  buttons.dxf.onclick = dxf;
  store.subscribe((s, before) => {
    if (s.result === before.result && s.feature === before.feature && s.selection === before.selection) return;
    buttons.step.disabled = !s.result;
    const source = s.result && dxfSource(s, documentOf(s.result));
    buttons.dxf.disabled = !source;
    buttons.dxf.title = source ? `Download DXF of ${source.sketch ? `sketch ${source.sketch}` : source.face}` : 'Select a sketch in the tree or a flat face for its DXF';
  });
  buttons.step.disabled = buttons.dxf.disabled = true;

  return { open, save, step, dxf, status: () => status.textContent };
}
