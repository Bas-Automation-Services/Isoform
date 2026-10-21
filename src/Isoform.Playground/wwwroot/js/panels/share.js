// The Share button and opening a share link (lib/share.js). Share puts the committed document's canonical text in a
// #doc= link, copies it and shows it in the address bar; a page opened on such a link loads that document instead of
// an example and says whether it regenerated to the hash the link promised.
import { linkTo, readFragment, sameHash, unpack } from '../lib/share.js';
import { short } from '../lib/history.js';
import { pretty } from './editor.js';

/** button: the Share button; status: where the hash and the outcome show; examplesBox: the example picker. */
export function createShare(store, { button, status, examplesBox, regenerate }) {
  let promised = null; // the hash the opened link promised, until the document is edited

  const say = (text, kind = '') => {
    status.textContent = text;
    status.className = `share-status ${kind}`;
  };

  async function link() {
    const { result } = store.state;
    return result ? linkTo(location.href, result.text, result.hash) : null;
  }

  button.onclick = async () => {
    const url = await link();
    if (!url) return;
    promised = null;
    // The address bar holds the link too, for when the clipboard is not allowed.
    history.replaceState(null, '', url);
    const copied = await navigator.clipboard?.writeText(url).then(() => true, () => false);
    say(`${copied ? 'Link copied' : 'Link in the address bar'} · hash ${short(store.state.hash)}`);
  };

  /** Loads the document of a #doc= fragment into the store; false when there is none or it does not read. */
  async function open(fragment = location.hash) {
    const read = readFragment(fragment);
    if (!read) return false;
    let text;
    try { text = pretty(JSON.parse(await unpack(read.packed))); } catch {
      say('The share link does not hold a document', 'bad');
      return false;
    }
    promised = read.hash;
    // Off the address bar, so a reload opens what is edited from here on (kept in local storage), not the link again.
    history.replaceState(null, '', location.pathname + location.search);
    let option = [...examplesBox.options].find(o => o.value === '' && o.dataset.shared);
    if (!option) {
      option = new Option('(shared link)', '');
      option.dataset.shared = '1';
      examplesBox.add(option, 0);
    }
    option.selected = true;
    store.set({ text, selection: [] });
    return true;
  }

  // A link pasted into the address bar of an open page changes only the fragment.
  addEventListener('hashchange', async () => { if (await open()) regenerate({ fit: true }); });

  store.subscribe((s, before) => {
    button.disabled = !s.result;
    if (s.hash === before.hash) return;
    if (!promised) return say(''); // an edit since: what was said is about another document
    const same = sameHash(s.hash, promised);
    say(same ? `Shared document · hash ${short(s.hash)}, identical to the link` : `Hash ${short(s.hash)} differs from the link's ${promised.slice(0, 8)}…`, same ? 'good' : 'bad');
    promised = null;
  });
  button.disabled = true;

  return { link, open, status: () => status.textContent };
}
