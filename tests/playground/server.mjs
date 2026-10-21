// The native kernel server (src/Isoform.Server) for the tests: built, then started on a free port.
import { spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { repo } from './published.mjs';

/** Builds the server and starts it serving root; resolves to { url, kernel, close } (kernel: its WebSocket URL). */
export async function startServer(root) {
  if (spawnSync('dotnet', ['build', 'src/Isoform.Server', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: repo, stdio: 'inherit' }).status !== 0)
    throw new Error('dotnet build src/Isoform.Server failed');
  // The program itself rather than dotnet run, which would leave it running when killed.
  const server = spawn('dotnet', [join(repo, 'src', 'Isoform.Server', 'bin', 'Release', 'net10.0', 'Isoform.Server.dll'), '--port', '0', '--root', root],
    { cwd: repo, stdio: ['ignore', 'pipe', 'inherit'] });
  const url = await new Promise((resolve, reject) => {
    let out = '';
    server.stdout.on('data', d => {
      out += d;
      const m = out.match(/ at (http:\S+\/) /);
      if (m) resolve(m[1]);
    });
    server.on('exit', code => reject(new Error(`the server stopped (${code}): ${out}`)));
  });
  return { url, kernel: `${url.replace(/^http/, 'ws')}kernel`, close: () => server.kill() };
}
