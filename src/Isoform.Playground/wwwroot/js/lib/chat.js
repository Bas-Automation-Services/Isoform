// The chat with Claude (panels/chat.js): the conversation, kept in the page and sent whole each turn, and the
// connection to the native server's /chat (src/Isoform.Server/ChatSocket.cs). Pure, so Node tests drive it with fakes.
//
//   const chat = createChat({ send, apply, changed });
//   await chat.say(text, document)
//     Sends the conversation and the committed document; the server answers with Claude's text, its edits in order and
//     the hash they give. The edits go in through apply(edits, label): one history entry per turn (lib/commit.js).
//     chat.log is what the panel shows: {role: 'user' | 'assistant' | 'error', text, edits?, hash?, problem?}.
//
//   createSender(connect) → send(args, onProgress)
//     A turn over one WebSocket, opened on the first turn: connect() resolves to a worker-like socket
//     (kernel-client.js connectKernel) or null. Progress frames {progress: id, tool, ok, calls, edits} go to onProgress.
import { short } from './history.js';

/** The history label of a turn's edits: what the user asked. */
export const label = text => `chat: ${text.length > 48 ? text.slice(0, 47) + '…' : text}`;

/**
 * send(args, onProgress) resolves to the server's result {text, edits, hash, stop, toolCalls, error} and rejects with
 * its error; apply(edits, label) resolves like commits.applyEdits does for window.isoform: {hash}, {error}, or null.
 * changed() is called whenever log, busy or progress change.
 */
export function createChat({ send, apply, changed = () => { } }) {
  const chat = { messages: [], log: [], busy: false, progress: null, say };

  async function say(text, document) {
    text = text.trim();
    if (!text || chat.busy) return;
    chat.log.push({ role: 'user', text });
    chat.busy = true;
    changed();
    try {
      const asked = [...chat.messages, { role: 'user', text }];
      const r = await send({ document, messages: asked }, p => { chat.progress = p; changed(); });
      // The API takes no empty turn: an answer without text still has to say something.
      chat.messages = [...asked, { role: 'assistant', text: r.text || '(no answer)' }];
      const entry = { role: 'assistant', text: r.text, edits: r.edits.length, hash: r.hash, problem: r.error ?? null };
      if (r.edits.length) {
        const applied = await apply(r.edits, label(text));
        if (applied?.error) entry.problem = `the edits did not apply here: ${applied.error}`;
        else if (applied && applied.hash !== r.hash) entry.problem = `the document here came out ${short(applied.hash)}, not the agent's ${short(r.hash)}: it changed during the turn`;
      }
      chat.log.push(entry);
    } catch (e) {
      chat.log.push({ role: 'error', text: e.message });
    } finally {
      chat.busy = false;
      chat.progress = null;
      changed();
    }
  }

  return chat;
}

export function createSender(connect) {
  let socket = null;
  let next = 0;
  const pending = new Map(); // id → {resolve, reject, onProgress}

  async function open() {
    socket = await connect();
    if (!socket) throw new Error('the chat could not reach the server');
    socket.onmessage = ({ data }) => {
      if (data.progress != null) return pending.get(data.progress)?.onProgress(data);
      const p = pending.get(data.id);
      if (!p) return;
      pending.delete(data.id);
      data.ok ? p.resolve(data.result) : p.reject(new Error(data.error));
    };
    // A closed connection fails what waits on it; the next turn connects again.
    socket.onerror = e => {
      socket = null;
      for (const p of pending.values()) p.reject(new Error(e?.message ?? 'the chat connection closed'));
      pending.clear();
    };
  }

  return async (args, onProgress = () => { }) => {
    if (!socket) await open();
    const id = ++next;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject, onProgress });
      socket.postMessage({ id, op: 'chat', args });
    });
  };
}
