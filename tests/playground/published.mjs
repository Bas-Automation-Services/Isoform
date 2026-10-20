// The published playground: where its files are, and its kernel loaded straight into Node.
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

/** The published wwwroot: the argument, else ISOFORM_PLAYGROUND, else where the README's publish command puts it. */
export function publishedRoot(path = process.env.ISOFORM_PLAYGROUND) {
  let root = resolve(path ?? join(repo, 'out', 'playground', 'wwwroot'));
  // Accept the publish folder itself too, not only its wwwroot.
  if (!existsSync(join(root, '_framework')) && existsSync(join(root, 'wwwroot'))) root = join(root, 'wwwroot');
  if (!existsSync(join(root, '_framework', 'dotnet.js')))
    throw new Error(`No published playground in ${root}: run dotnet publish src/Isoform.Playground -c Release -o out/playground`);
  return root;
}

/** The kernel's JS exports (Regenerate, Positions, Examples, ...), as the page's worker sees them. */
export async function loadKernel(root = publishedRoot()) {
  const { dotnet } = await import(pathToFileURL(join(root, '_framework', 'dotnet.js')).href);
  const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
  const kernel = (await getAssemblyExports(getConfig().mainAssemblyName)).Isoform.Playground.Playground;
  await runMain();
  return kernel;
}
