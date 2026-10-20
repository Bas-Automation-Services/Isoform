// The kernel in a worker, so the page stays responsive while it regenerates.
import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
const kernel = (await getAssemblyExports(getConfig().mainAssemblyName)).Isoform.Playground.Playground;
await runMain();

function take(view, Type) {
  const copy = new Type(view.slice());
  view.dispose();
  return copy;
}

onmessage = ({ data }) => {
  const info = JSON.parse(kernel.Regenerate(data.document, data.cell, data.chord));
  if (info.error) {
    postMessage({ type: 'result', info });
    return;
  }
  const positions = new Float32Array(take(kernel.Positions(), Float64Array));
  const faces = take(kernel.Faces(), Int32Array);
  const sharp = new Float32Array(take(kernel.Lines(), Float64Array));
  const smooth = new Float32Array(take(kernel.SmoothLines(), Float64Array));
  postMessage({ type: 'result', info, positions, faces, sharp, smooth }, [positions.buffer, faces.buffer, sharp.buffer, smooth.buffer]);
};

postMessage({ type: 'ready', examples: kernel.Examples() });
