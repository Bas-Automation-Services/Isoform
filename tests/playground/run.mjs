// The whole playground check in one command: optionally publish, then the front end's unit tests and the kernel tests
// (wasm, and native through the server) in Node, then the browser run. --server runs the browser run against the
// native kernel server instead of the static files and WebAssembly.
// node tests/playground/run.mjs [published wwwroot] [--publish] [--server]
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { publishedRoot, repo } from './published.mjs';
import { startServer } from './server.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const run = (command, argv, env) => spawnSync(command, argv, { cwd: repo, stdio: 'inherit', env: { ...process.env, ...env } }).status === 0;

if (args.includes('--publish') && !run('dotnet', ['publish', 'src/Isoform.Playground', '-c', 'Release', '-o', join('out', 'playground')])) {
  console.error('dotnet publish failed');
  process.exit(1);
}
const root = publishedRoot(args.find(a => !a.startsWith('--')));
console.log(`Testing the playground in ${root}\n`);
const server = await startServer(root);
console.log(`The native kernel server at ${server.url}\n`);

const units = run(process.execPath, ['--test', ...['kernel-client', 'store', 'diagnose', 'references', 'diff', 'history', 'dragmath', 'features', 'preview', 'share', 'files'].map(n => join(here, `${n}.test.mjs`))]);
console.log();
const kernel = run(process.execPath, ['--test', join(here, 'kernel.test.mjs'), join(here, 'server.test.mjs')], { ISOFORM_PLAYGROUND: root, ISOFORM_SERVER: server.url });
console.log();
const browser = run(process.execPath, [join(here, 'e2e.mjs'), root], args.includes('--server') ? { ISOFORM_SERVER: server.url } : {});
server.close();
const verdict = ok => ok ? 'passed' : 'FAILED';
console.log(`\nunit tests ${verdict(units)}, kernel tests ${verdict(kernel)}, browser run (${args.includes('--server') ? 'native' : 'wasm'}) ${verdict(browser)}`);
process.exitCode = units && kernel && browser ? 0 : 1;
