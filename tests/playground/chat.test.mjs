// The chat's conversation and connection (wwwroot/js/lib/chat.js), with a fake server. node --test
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createChat, createSender, label } from '../../src/Isoform.Playground/wwwroot/js/lib/chat.js';

const edit = { set: 'plate.size.0', value: 120 };

test('a turn sends the conversation and the document, and commits its edits as one entry', async () => {
  const sent = [], applied = [];
  const chat = createChat({
    send: async args => { sent.push(structuredClone(args)); return { text: 'Done.', edits: [edit], hash: 'h1', stop: 'end_turn', toolCalls: 1 }; },
    apply: async (edits, l) => { applied.push({ edits, label: l }); return { hash: 'h1' }; },
  });
  await chat.say('  make the plate 120 long ', 'DOC');
  await chat.say('and 60 wide', 'DOC2');

  assert.deepEqual(sent[0], { document: 'DOC', messages: [{ role: 'user', text: 'make the plate 120 long' }] });
  assert.deepEqual(sent[1].messages.map(m => m.role), ['user', 'assistant', 'user']);
  assert.equal(sent[1].messages[1].text, 'Done.');
  assert.equal(sent[1].document, 'DOC2');
  assert.deepEqual(applied[0], { edits: [edit], label: 'chat: make the plate 120 long' });
  assert.deepEqual(chat.log[1], { role: 'assistant', text: 'Done.', edits: 1, hash: 'h1', problem: null });
  assert.equal(chat.busy, false);
});

test('an answer without edits commits nothing; a failed turn shows its error and is not resent', async () => {
  let fail = true;
  const sent = [];
  const chat = createChat({
    send: async args => {
      sent.push(args);
      if (fail) throw new Error('the server has no ANTHROPIC_API_KEY');
      return { text: '', edits: [], hash: 'h0', stop: 'end_turn', toolCalls: 0 };
    },
    apply: async () => assert.fail('nothing to apply'),
  });
  await chat.say('hello', 'DOC');
  assert.deepEqual(chat.log.at(-1), { role: 'error', text: 'the server has no ANTHROPIC_API_KEY' });
  fail = false;
  await chat.say('hello again', 'DOC');
  assert.deepEqual(sent[1].messages, [{ role: 'user', text: 'hello again' }]);
  // The API takes no empty turn.
  assert.deepEqual(chat.messages[1], { role: 'assistant', text: '(no answer)' });
});

test('a hash that differs from the agent\'s, or edits that do not apply, are shown', async () => {
  let outcome = { hash: 'other' };
  const chat = createChat({
    send: async () => ({ text: 'Done.', edits: [edit], hash: 'h1', stop: 'end_turn', toolCalls: 1 }),
    apply: async () => outcome,
  });
  await chat.say('go', 'DOC');
  assert.match(chat.log.at(-1).problem, /changed during the turn/);
  outcome = { error: "'plate' is not a feature id" };
  await chat.say('go', 'DOC');
  assert.match(chat.log.at(-1).problem, /did not apply here: 'plate'/);
});

test('one turn at a time', async () => {
  let release;
  const chat = createChat({ send: () => new Promise(r => { release = r; }), apply: async () => null });
  const first = chat.say('one', 'DOC');
  assert.equal(chat.busy, true);
  await chat.say('two', 'DOC');
  assert.equal(chat.log.length, 1);
  release({ text: 'ok', edits: [], hash: 'h', stop: 'end_turn', toolCalls: 0 });
  await first;
  assert.equal(chat.busy, false);
});

test('labels are short', () => {
  assert.equal(label('make it 120'), 'chat: make it 120');
  assert.equal(label('x'.repeat(60)).length, 'chat: '.length + 48);
});

/** A socket like connectKernel's: postMessage out, onmessage/onerror in. */
function fakeSocket() {
  const socket = { sent: [], onmessage: null, onerror: null, postMessage: m => socket.sent.push(m) };
  socket.reply = data => socket.onmessage({ data });
  return socket;
}

test('the sender connects once, hands progress on, and resolves with the result', async () => {
  const socket = fakeSocket();
  let connects = 0;
  const send = createSender(async () => { connects++; return socket; });
  const progress = [];
  const turn = send({ document: 'DOC', messages: [] }, p => progress.push(p.tool));
  await new Promise(r => setTimeout(r));
  const { id } = socket.sent[0];
  assert.equal(socket.sent[0].op, 'chat');
  socket.reply({ progress: id, tool: 'apply_edits', ok: true, calls: 1, edits: 1 });
  socket.reply({ id, ok: true, result: { text: 'Done.' } });
  assert.deepEqual(await turn, { text: 'Done.' });
  assert.deepEqual(progress, ['apply_edits']);

  const failing = send({ document: 'DOC', messages: [] });
  await new Promise(r => setTimeout(r));
  socket.reply({ id: socket.sent[1].id, ok: false, error: 'the server has no ANTHROPIC_API_KEY' });
  await assert.rejects(failing, /ANTHROPIC_API_KEY/);
  assert.equal(connects, 1);
});

test('a closed connection fails the turn, and the next turn connects again', async () => {
  const sockets = [fakeSocket(), fakeSocket()];
  let n = 0;
  const send = createSender(async () => sockets[n++]);
  const turn = send({ document: 'DOC', messages: [] });
  await new Promise(r => setTimeout(r));
  sockets[0].onerror({ message: 'the native kernel closed the connection' });
  await assert.rejects(turn, /closed/);
  send({ document: 'DOC', messages: [] });
  await new Promise(r => setTimeout(r));
  assert.equal(sockets[1].sent.length, 1);

  await assert.rejects(createSender(async () => null)({}), /could not reach the server/);
});
