// A static file server for the published playground, so the tests need nothing but Node.
// Run on its own to look at the page: node tests/playground/serve.mjs [wwwroot] [port]
import { createReadStream, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { publishedRoot } from './published.mjs';

// Browsers refuse module scripts and streaming wasm compilation without the right types.
const types = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.wasm': 'application/wasm', '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.txt': 'text/plain',
};

/** Serves root on 127.0.0.1 (port 0 picks a free one); resolves to { url, close }. */
export function serve(root, port = 0) {
  const server = createServer((req, res) => {
    const path = normalize(join(root, decodeURIComponent(new URL(req.url, 'http://x').pathname)));
    let file = path;
    try {
      if (!path.startsWith(root + sep) && path !== root) throw new Error('outside root');
      if (statSync(file).isDirectory()) file = join(file, 'index.html');
      statSync(file);
    } catch {
      res.writeHead(404).end();
      return;
    }
    // no-store: a fresh publish is always what the browser loads.
    res.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    createReadStream(file).pipe(res);
  });
  return new Promise(done => server.listen(port, '127.0.0.1', () => done({
    url: `http://127.0.0.1:${server.address().port}/`,
    close: () => new Promise(closed => server.close(closed)),
  })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const site = await serve(publishedRoot(process.argv[2]), +(process.argv[3] ?? 8137));
  console.log(`Serving the playground at ${site.url}`);
}
