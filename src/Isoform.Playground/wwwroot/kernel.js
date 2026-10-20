// The kernel in a worker, so the page stays responsive while it regenerates. The page may start several: then each
// meshes its share of the regions, the helpers send what they meshed to the first worker through a message port,
// and the first imports it and regenerates from its cache.
import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
const kernel = (await getAssemblyExports(getConfig().mainAssemblyName)).Isoform.Playground.Playground;
await runMain();

function take(view, Type) {
  const copy = new Type(view.slice());
  view.dispose();
  return copy;
}

let index = 0, count = 1, coordinator = null;
const arrived = new Map(); // regeneration id → region results from the helpers
let current = null;        // the regeneration the coordinator is working on

function regenerate(data) {
  const info = JSON.parse(kernel.Regenerate(data.document, data.cell, data.chord));
  if (info.error) {
    postMessage({ type: 'result', id: data.id, info });
    return;
  }
  const positions = new Float32Array(take(kernel.Positions(), Float64Array));
  const faces = take(kernel.Faces(), Int32Array);
  const sharp = new Float32Array(take(kernel.Lines(), Float64Array));
  const smooth = new Float32Array(take(kernel.SmoothLines(), Float64Array));
  postMessage({ type: 'result', id: data.id, info, positions, faces, sharp, smooth }, [positions.buffer, faces.buffer, sharp.buffer, smooth.buffer]);
}

// The coordinator regenerates once its own share is meshed and every helper's has arrived.
function finish() {
  const results = current && arrived.get(current.id);
  if (!current?.shared || (results?.length ?? 0) < count - 1) return;
  const data = current;
  current = null;
  for (const id of arrived.keys()) if (id <= data.id) arrived.delete(id);
  for (const result of results ?? []) kernel.Import(result.regions);
  regenerate(data);
}

function arrive({ data }) {
  if (!arrived.has(data.id)) arrived.set(data.id, []);
  arrived.get(data.id).push(data);
  finish();
}

onmessage = ({ data }) => {
  if (data.type === 'pool') {
    ({ index, count } = data);
    if (index === 0) for (const port of data.ports) port.onmessage = arrive;
    else coordinator = data.ports[0];
    return;
  }
  if (count === 1) {
    regenerate(data);
  } else if (index > 0) {
    const regions = take(kernel.Share(data.document, data.cell, data.chord, index, count), Uint8Array);
    kernel.Sweep();
    coordinator.postMessage({ id: data.id, regions }, [regions.buffer]);
  } else {
    current = data;
    kernel.Share(data.document, data.cell, data.chord, 0, count).dispose();
    current.shared = true;
    finish();
  }
};

postMessage({ type: 'ready', examples: kernel.Examples() });
