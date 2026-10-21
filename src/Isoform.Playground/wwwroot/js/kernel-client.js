// The page's side of the worker protocol: requests {id, op, args}, replies {id, ok, result|error, ms}. Pure: the
// workers are anything with postMessage and onmessage, so the tests drive it with fakes, and connectKernel makes one of
// a connection to the native kernel server.

/** Ops that go to every worker of the pool (each meshes its share); the others go to the primary only. */
const fanOut = new Set(['regenerate']);

/**
 * A client for a pool of kernel workers; workers[0] is the primary. call() and latest() resolve to the reply's
 * {result, ms} and reject with the worker's error.
 */
export function createClient(workers) {
  let next = 0;
  const pending = new Map(); // id → { from, resolve, reject }: from is the worker whose reply counts
  const lanes = new Map();   // channel → { busy, queued }

  workers.forEach(worker => {
    worker.onmessage = ({ data }) => {
      const p = pending.get(data.id);
      // A helper's reply to a fanned-out request only matters when it failed: the primary would wait forever.
      if (!p || (worker !== p.from && data.ok)) return;
      pending.delete(data.id);
      data.ok ? p.resolve({ result: data.result, ms: data.ms }) : p.reject(new Error(data.error));
    };
  });

  function post(targets, op, args, transfer = []) {
    const id = ++next;
    return new Promise((resolve, reject) => {
      pending.set(id, { from: targets[0], resolve, reject });
      for (const worker of targets) worker.postMessage({ id, op, args }, transfer);
    });
  }

  const call = (op, args) => post(fanOut.has(op) ? workers : [workers[0]], op, args);

  // Sends the next queued request of a lane once the one in flight has replied.
  function pump(lane) {
    if (lane.busy || !lane.queued) return;
    const { op, args, resolve, reject } = lane.queued;
    lane.queued = null;
    lane.busy = true;
    call(op, args)
      .then(reply => resolve(lane.queued ? null : reply), reject) // overtaken by a newer request: stale
      .finally(() => { lane.busy = false; pump(lane); });
  }

  return {
    size: workers.length,
    call,

    /**
     * One request in flight per channel. A request made meanwhile waits and replaces any request already waiting;
     * the replaced one, and a reply that arrives while a newer request waits, resolve to null.
     */
    latest(channel, op, args) {
      if (!lanes.has(channel)) lanes.set(channel, { busy: false, queued: null });
      const lane = lanes.get(channel);
      return new Promise((resolve, reject) => {
        lane.queued?.resolve(null);
        lane.queued = { op, args, resolve, reject };
        pump(lane);
      });
    },

    /** Links the pool: a message port from each helper to the primary. Resolves once every worker has its kernel. */
    start() {
      const channels = workers.slice(1).map(() => new MessageChannel());
      const primaryPorts = channels.map(c => c.port1);
      return Promise.all([
        post([workers[0]], 'pool', { index: 0, count: workers.length, ports: primaryPorts }, primaryPorts),
        ...channels.map((c, i) => post([workers[i + 1]], 'pool', { index: i + 1, count: workers.length, ports: [c.port2] }, [c.port2])),
      ]);
    },
  };
}

/**
 * The native kernel (src/Isoform.Server) over a WebSocket, as a worker that createClient takes: postMessage sends a
 * request, onmessage gets the replies, onerror hears that the connection closed. Each connection is a kernel session of
 * its own. Resolves to null when nothing answers at url within timeout ms, for the page to use its workers instead.
 */
export function connectKernel(url, timeout = 2000) {
  return new Promise(resolve => {
    const socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    const worker = {
      onmessage: null,
      onerror: null,
      postMessage: message => socket.send(JSON.stringify(message)),
      terminate: () => socket.close(),
    };
    const give = value => { clearTimeout(timer); resolve(value); };
    const timer = setTimeout(() => { socket.close(); give(null); }, timeout);
    socket.onopen = () => give(worker);
    socket.onerror = () => give(null);
    socket.onclose = () => worker.onerror?.({ message: 'the native kernel closed the connection' });
    socket.onmessage = ({ data }) => worker.onmessage?.({ data: typeof data === 'string' ? JSON.parse(data) : unpack(data) });
  });
}

// A regeneration's mesh buffers in the server's binary reply, in its order, with the types the worker gives them.
const meshBuffers = [['positions', Float32Array], ['faces', Int32Array], ['sharp', Float32Array], ['smooth', Float32Array],
  ['sharpEdges', Int32Array], ['smoothEdges', Int32Array]];

/**
 * A binary reply of the server (KernelSocket.Pack) as the worker's reply: the header's length (int32), the header
 * (JSON, padded to four bytes), then each buffer of the result as many elements as the header's lengths say.
 */
export function unpack(buffer) {
  const length = new DataView(buffer).getInt32(0, true);
  const { lengths, ...reply } = JSON.parse(new TextDecoder().decode(new Uint8Array(buffer, 4, length)));
  let at = 4 + length;
  meshBuffers.forEach(([name, Type], i) => {
    reply.result[name] = new Type(buffer, at, lengths[i]);
    at += 4 * lengths[i];
  });
  return reply;
}

/**
 * The page's model of a regeneration: result v2 as the kernel wrote it (src/Isoform.Interactive/README.md), plus
 * the cell size it was made at (cell, set by the worker), the worker's round trip in ms, the mesh buffers, and the body
 * of each face. A preview result (J2) also has preview: true. Faces are indexed as in
 * mesh.faceOfTriangle; a face is its list of source names, the first being the name a document refers to it by.
 * A failure is {error, failedEdit?, ms}, with error set.
 */
export function adaptResult({ info, positions, faces, sharp, smooth, sharpEdges, smoothEdges }, ms) {
  if (info.error) return { ...info, ms };
  const bodyOfFace = [];
  info.bodies.forEach(b => b.faces.forEach(f => { bodyOfFace[f] = b.name; }));
  return { ...info, error: null, ms, bodyOfFace, mesh: { positions, faceOfTriangle: faces, sharp, smooth, sharpEdges, smoothEdges } };
}
