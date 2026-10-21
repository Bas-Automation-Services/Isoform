// The chat panel: the user types, Claude answers with edits to the document (lib/chat.js). The model runs in the
// native server, which holds the API key; without the server (WebAssembly) the panel says so and stays inert. A turn's
// edits are committed as one history entry with origin 'agent', so the agent view, history and undo show them.
import { createChat, createSender } from '../lib/chat.js';
import { short } from '../lib/history.js';

const el = (tag, className, text) => {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text != null) e.textContent = text;
  return e;
};

/**
 * root: an empty element. connect() opens the chat's connection (kernel-client.js connectKernel on /chat), or is null
 * without the native server. applyEdits(edits, label) commits a turn's edits. Returns the chat (lib/chat.js).
 */
export function createChatPanel(store, root, { connect, applyEdits }) {
  root.classList.add('chat');
  const head = el('div', 'chat-head');
  head.append(el('b', null, 'Chat'), el('span', 'muted', connect ? 'Claude edits the document' : 'needs the native server'));
  const log = el('div', 'chat-log');
  const status = el('div', 'chat-status muted');
  const input = el('textarea');
  input.rows = 2;
  const button = el('button', null, 'Send');
  const form = el('div', 'chat-form');
  form.append(input, button);
  root.replaceChildren(head, log, status, form);

  if (!connect) {
    input.disabled = button.disabled = true;
    input.placeholder = 'Chat runs in the native server: dotnet run --project src/Isoform.Server, with ANTHROPIC_API_KEY set';
    return null;
  }
  input.placeholder = 'Ask for a change: "make the plate 120 long" (Enter sends, Shift+Enter for a new line)';

  const chat = createChat({ send: createSender(connect), apply: applyEdits, changed: render });

  function entry(e) {
    const item = el('div', `chat-${e.role}`);
    if (e.text) item.append(el('div', 'chat-text', e.text));
    if (e.role === 'assistant') {
      const meta = e.edits ? `${e.edits} edit${e.edits > 1 ? 's' : ''} · hash ${short(e.hash)}` : 'no edits';
      item.append(el('div', 'chat-meta muted', meta));
    }
    if (e.problem) item.append(el('div', 'chat-problem', e.problem));
    return item;
  }

  function render() {
    log.replaceChildren(...chat.log.map(entry));
    log.scrollTop = log.scrollHeight;
    const p = chat.progress;
    status.textContent = !chat.busy ? '' : p ? `${p.tool} ${p.ok ? '✓' : '✗'} · ${p.calls} tool call${p.calls > 1 ? 's' : ''} · ${p.edits} edit${p.edits === 1 ? '' : 's'} so far` : 'Claude is thinking…';
    input.disabled = button.disabled = chat.busy;
  }

  function send() {
    const text = input.value;
    if (!text.trim() || chat.busy) return;
    input.value = '';
    // The committed document, which the edits will be applied to (lib/commit.js).
    chat.say(text, store.state.result?.text ?? store.state.text).then(() => input.focus());
  }
  button.onclick = send;
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  });
  return chat;
}
