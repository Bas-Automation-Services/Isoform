// Lights the editor lines a commit changed (an applied edit list, undo, redo), so a change made elsewhere is seen in
// the text. A textarea cannot style lines, so a layer of bars sits over it and scrolls with it.
import { changedLines, lineDiff } from '../lib/diff.js';

/** Wraps the editor; returns flash(before, after), which lights the lines of after that differ and returns them. */
export function createFlash(editor) {
  const wrap = document.createElement('div');
  wrap.className = 'editor-wrap';
  editor.before(wrap);
  const layer = document.createElement('div');
  layer.className = 'editor-flash';
  wrap.append(editor, layer);
  const follow = () => { layer.style.transform = `translate(${-editor.scrollLeft}px, ${-editor.scrollTop}px)`; };
  editor.addEventListener('scroll', follow);

  return (before, after) => {
    const lines = changedLines(lineDiff(before, after));
    if (!lines.length) return lines;
    const style = getComputedStyle(editor);
    const height = parseFloat(style.lineHeight);
    const top = parseFloat(style.paddingTop) + parseFloat(style.borderTopWidth);
    // Bring the first changed line into view when it is out of sight.
    const y = lines[0] * height;
    if (y < editor.scrollTop || y + height > editor.scrollTop + editor.clientHeight) editor.scrollTop = Math.max(0, y - editor.clientHeight / 3);
    follow();
    layer.replaceChildren(...lines.map(i => {
      const bar = document.createElement('div');
      bar.style.top = `${top + i * height}px`;
      bar.style.height = `${height}px`;
      return bar;
    }));
    // Restart the fade when two commits come close together.
    layer.classList.remove('on');
    void layer.offsetWidth;
    layer.classList.add('on');
    return lines;
  };
}
