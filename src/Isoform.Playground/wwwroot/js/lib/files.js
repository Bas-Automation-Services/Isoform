// Files in and out of the page, pure: the names they get, how a document is saved, what a DXF is of, and what an
// export that failed says. The buttons are panels/files.js.

/** A file name without its .isoform.json (or .json) ending; null for none. */
export function baseName(fileName) {
  return fileName ? fileName.replace(/(\.isoform)?\.json$/i, '') || null : null;
}

/** Characters a file name cannot hold (a face name's /, say) become '-'. */
export const safe = name => name.replace(/[\\/:*?"<>|\s]+/g, '-');

/** The stem of every file saved from the page: the file opened or saved, else the example shown, else 'part'. */
export function stem({ file = null, example = null } = {}) {
  return baseName(file) ?? (example ? safe(example.split(':')[0].trim().toLowerCase()) : 'part');
}

export const documentFile = stem => `${stem}.isoform.json`;
export const stepFile = stem => `${stem}.step`;
export const dxfFile = (stem, source) => `${stem}-${safe(source.sketch ?? source.face)}.dxf`;

/**
 * How Save writes: to the file opened or saved before ('handle'), to a file the user picks with the File System
 * Access API ('picker', Edge and Chrome), else as a download ('download').
 */
export const saveMode = ({ handle = null, picker = false }) => handle ? 'handle' : picker ? 'picker' : 'download';

/**
 * What a DXF download is of: the selected tree item when it is a sketch ({sketch: id}), else the one face selected
 * ({face: name}, the kernel says whether it is flat); null otherwise. doc is the committed document, parsed.
 */
export function dxfSource({ feature, selection }, doc) {
  if (feature && doc?.features?.some(f => f.id === feature && f.type === 'sketch')) return { sketch: feature };
  return selection?.length === 1 ? { face: selection[0] } : null;
}

/** The status line after an export: what was written, or why not (naming the faces STEP could not write exactly). */
export function exported(kind, file, reply) {
  if (reply.unsupported?.length) return { text: `No ${kind}: no exact form for ${reply.unsupported.join(', ')}`, bad: true };
  if (reply.error) return { text: `No ${kind}: ${reply.error}`, bad: true };
  const approximated = reply.approximated ? ` (${reply.approximated} curve${reply.approximated > 1 ? 's' : ''} as lines)` : '';
  return { text: `Downloaded ${file}${approximated}`, bad: false };
}
