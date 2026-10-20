// The kernel in a worker, so the page stays responsive while it regenerates. Requests are {id, op, args}, replies
// {id, ok, result|error, ms}; typed arrays in a result are transferred, not copied. The page may start a pool: then
// each worker meshes its share of the regions, the helpers send what they meshed to the primary (index 0) through a
// message port, and the primary imports it and regenerates from its cache. Only the primary's reply carries a result.
import { dotnet } from '../_framework/dotnet.js';

// Listening starts before the runtime has loaded, so no request is lost; each one waits for the load.
const loading = (async () => {
  const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
  const kernel = (await getAssemblyExports(getConfig().mainAssemblyName)).Isoform.Playground.Playground;
  await runMain();
  return kernel;
})();
let kernel = null;

function take(view, Type) {
  const copy = new Type(view.slice());
  view.dispose();
  return copy;
}

let index = 0, count = 1, primary = null;
const arrived = new Map(); // request id → region results from the helpers
let waiting = null;        // the primary's regeneration, until every helper's share has arrived

function arrive({ data }) {
  if (!arrived.has(data.id)) arrived.set(data.id, []);
  arrived.get(data.id).push(data.regions);
  gathered();
}

function gathered() {
  if (!waiting || (arrived.get(waiting.id)?.length ?? 0) < count - 1) return;
  const { id, done } = waiting;
  waiting = null;
  for (const regions of arrived.get(id) ?? []) kernel.Import(regions);
  for (const key of arrived.keys()) if (key <= id) arrived.delete(key);
  done();
}

function regenerate({ document, cell, chord }) {
  const info = JSON.parse(kernel.Regenerate(document, cell, chord));
  if (info.error) return { info };
  return {
    info,
    positions: new Float32Array(take(kernel.Positions(), Float64Array)),
    faces: take(kernel.Faces(), Int32Array),
    sharp: new Float32Array(take(kernel.Lines(), Float64Array)),
    smooth: new Float32Array(take(kernel.SmoothLines(), Float64Array)),
  };
}

const ops = {
  examples: () => JSON.parse(kernel.Examples()),

  pool(args) {
    ({ index, count } = args);
    if (index === 0) for (const port of args.ports) port.onmessage = arrive;
    else primary = args.ports[0];
  },

  // Sent to the whole pool. One regeneration at a time: the page's latest-wins channel guarantees it.
  async regenerate(args, id) {
    if (count === 1) return regenerate(args);
    if (index > 0) {
      const regions = take(kernel.Share(args.document, args.cell, args.chord, index, count), Uint8Array);
      kernel.Sweep();
      primary.postMessage({ id, regions }, [regions.buffer]);
      return null;
    }
    kernel.Share(args.document, args.cell, args.chord, 0, count).dispose();
    await new Promise(done => { waiting = { id, done }; gathered(); });
    return regenerate(args);
  },
};

onmessage = async ({ data: { id, op, args } }) => {
  const start = performance.now();
  try {
    kernel ??= await loading;
    const result = await ops[op](args, id);
    const transfer = Object.values(result ?? {}).filter(v => ArrayBuffer.isView(v)).map(v => v.buffer);
    postMessage({ id, ok: true, result, ms: performance.now() - start }, transfer);
  } catch (e) {
    postMessage({ id, ok: false, error: String(e?.message ?? e), ms: performance.now() - start });
  }
};
