// Share links: the document itself in the URL fragment, so a link needs no server and opens offline. Pure (browser
// built-ins only), so it is tested in Node.
//
//   #doc=<base64url of the deflate-raw of the canonical text, UTF-8>&hash=<the first 16 hex digits of its hash>
//
// The fragment never reaches a server. The hash lets the receiver check that the document that opened is the one sent:
// the canonical text regenerates to the same hash on every machine.

async function through(bytes, stream) {
  return new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(stream)).arrayBuffer());
}

/** base64url without padding: only A–Z a–z 0–9 - _, nothing a URL or a chat client escapes. */
export function base64url(bytes) {
  let binary = '';
  // In chunks: String.fromCharCode with a whole long document as arguments overflows the stack.
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}

export function fromBase64url(text) {
  const binary = atob(text.replaceAll('-', '+').replaceAll('_', '/'));
  return Uint8Array.from(binary, c => c.charCodeAt(0));
}

/** The text, compressed and URL-safe. */
export const pack = async text => base64url(await through(new TextEncoder().encode(text), new CompressionStream('deflate-raw')));

/** The text a pack came from; throws if it is not one. */
export const unpack = async packed => new TextDecoder().decode(await through(fromBase64url(packed), new DecompressionStream('deflate-raw')));

export const hashLength = 16;

/** The link to a document: base (a page URL; its query and fragment are dropped) with the packed text and hash. */
export async function linkTo(base, text, hash) {
  const url = new URL(base);
  url.search = '';
  url.hash = `doc=${await pack(text)}` + (hash ? `&hash=${hash.slice(0, hashLength)}` : '');
  return url.href;
}

/** {packed, hash} from a fragment ("#doc=…&hash=…"), or null when it carries no document. */
export function readFragment(fragment) {
  const params = new URLSearchParams(fragment.replace(/^#/, ''));
  const packed = params.get('doc');
  return packed ? { packed, hash: params.get('hash') } : null;
}

/** Whether a full hash is the one a link promised (its first hashLength digits). */
export const sameHash = (hash, promised) => !!hash && !!promised && hash.slice(0, hashLength) === promised;
