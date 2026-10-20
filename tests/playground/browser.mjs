// Headless Edge or Chrome driven over the DevTools protocol, with the few page helpers the browser tests need.
// Node 22+ for the global WebSocket; no packages.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const installed = {
  win32: [
    `${process.env['ProgramFiles(x86)']}\\Microsoft\\Edge\\Application\\msedge.exe`,
    `${process.env.ProgramFiles}\\Microsoft\\Edge\\Application\\msedge.exe`,
    `${process.env.ProgramFiles}\\Google\\Chrome\\Application\\chrome.exe`,
    `${process.env.LOCALAPPDATA}\\Google\\Chrome\\Application\\chrome.exe`,
  ],
  darwin: ['/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'],
  linux: ['/usr/bin/microsoft-edge', '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser'],
};

/** ISOFORM_BROWSER, else the first Edge or Chrome in the standard install locations. */
export function findBrowser() {
  const path = process.env.ISOFORM_BROWSER ?? installed[process.platform]?.find(p => existsSync(p));
  if (!path) throw new Error('No Edge or Chrome found: set ISOFORM_BROWSER to its executable.');
  return path;
}

/** One DevTools connection; commands to a page carry its session id (flat sessions). */
class Connection {
  #next = 0;
  #calls = new Map();
  listeners = new Set();

  constructor(ws) {
    this.ws = ws;
    ws.onmessage = ({ data }) => {
      const m = JSON.parse(data);
      const call = this.#calls.get(m.id);
      if (!call) return this.listeners.forEach(l => l(m));
      this.#calls.delete(m.id);
      m.error ? call.reject(new Error(`${call.method}: ${m.error.message}`)) : call.resolve(m.result);
    };
  }

  send(method, params = {}, sessionId) {
    const id = ++this.#next;
    this.ws.send(JSON.stringify({ id, method, params, sessionId }));
    return new Promise((resolve, reject) => this.#calls.set(id, { resolve, reject, method }));
  }
}

/** Starts the browser; resolves to { newPage(), close() }. */
export async function launch({ width = 1500, height = 900 } = {}) {
  const profile = mkdtempSync(join(tmpdir(), 'isoform-browser-'));
  // SwiftShader gives WebGL without a GPU; port 0 lets the browser pick a free port and write it to the profile.
  const proc = spawn(findBrowser(), [
    '--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
    '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--no-first-run', '--no-default-browser-check',
    `--window-size=${width},${height}`, 'about:blank',
  ], { stdio: 'ignore' });
  const exited = new Promise(done => proc.once('exit', done));
  const portFile = join(profile, 'DevToolsActivePort');
  let lines = [];
  for (let i = 0; lines.length < 2; i++) {
    if (i > 300) throw new Error('The browser did not start its DevTools server.');
    await sleep(100);
    if (existsSync(portFile)) lines = readFileSync(portFile, 'utf8').split('\n').filter(Boolean);
  }
  const ws = new WebSocket(`ws://127.0.0.1:${lines[0]}${lines[1]}`);
  await new Promise((open, fail) => { ws.onopen = open; ws.onerror = fail; });
  const connection = new Connection(ws);

  return {
    async newPage() {
      const { targetId } = await connection.send('Target.createTarget', { url: 'about:blank' });
      const { sessionId } = await connection.send('Target.attachToTarget', { targetId, flatten: true });
      const page = new Page(connection, sessionId);
      await page.send('Page.enable');
      await page.send('Runtime.enable');
      await page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
      return page;
    },
    async close() {
      await connection.send('Browser.close').catch(() => { });
      await Promise.race([exited, sleep(5000)]);
      proc.kill();
      rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
    },
  };
}

/**
 * A tab. Coordinates are CSS pixels in the viewport. Expressions run in the page and may return promises;
 * results come back by value, so return plain data (e.g. `window.isoform.handles()`).
 */
export class Page {
  /** Uncaught exceptions and console errors since the last open(). */
  errors = [];

  constructor(connection, sessionId) {
    this.connection = connection;
    this.sessionId = sessionId;
    connection.listeners.add(m => {
      if (m.sessionId !== sessionId) return;
      if (m.method === 'Runtime.exceptionThrown') {
        const d = m.params.exceptionDetails;
        this.errors.push(d.exception?.description ?? d.text);
      }
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error')
        this.errors.push(m.params.args.map(a => a.value ?? a.description).join(' '));
    });
  }

  send(method, params) {
    return this.connection.send(method, params, this.sessionId);
  }

  /** The next event of this page with the given method. */
  event(method) {
    return new Promise(resolve => {
      const l = m => {
        if (m.sessionId !== this.sessionId || m.method !== method) return;
        this.connection.listeners.delete(l);
        resolve(m.params);
      };
      this.connection.listeners.add(l);
    });
  }

  async open(url) {
    this.errors = [];
    const loaded = this.event('Page.loadEventFired');
    const { errorText } = await this.send('Page.navigate', { url });
    if (errorText) throw new Error(`${url}: ${errorText}`);
    await loaded;
  }

  async evaluate(expression) {
    const { result, exceptionDetails } = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
    return result.value;
  }

  /**
   * Polls until the expression is truthy and returns its value. Real time, not virtual time: headless virtual time
   * does not advance while the kernel's worker computes.
   */
  async waitFor(expression, { timeout = 60000, interval = 100 } = {}) {
    const end = Date.now() + timeout;
    let last;
    while (Date.now() < end) {
      try {
        last = await this.evaluate(expression);
        if (last) return last;
      } catch (e) {
        last = e.message; // e.g. evaluated while the page navigates: try again
      }
      await sleep(interval);
    }
    throw new Error(`Timed out after ${timeout} ms waiting for ${expression} (last: ${JSON.stringify(last)})`);
  }

  /** Waits for n animation frames, so the canvas shows what the page last set up. */
  frames(n = 2) {
    return this.evaluate(`new Promise(done => { let n = ${n}; const f = () => --n ? requestAnimationFrame(f) : done(true); requestAnimationFrame(f); })`);
  }

  /** The centre of the first element matching the selector, as [x, y]. */
  center(selector) {
    return this.evaluate(`(r => r && [r.x + r.width / 2, r.y + r.height / 2])(document.querySelector(${JSON.stringify(selector)})?.getBoundingClientRect())`);
  }

  /** One mouse event; type is mousePressed, mouseReleased, mouseMoved or mouseWheel. */
  mouse(type, [x, y], { button = 'left', buttons = 0, clickCount = 1, modifiers = 0, deltaX = 0, deltaY = 0 } = {}) {
    return this.send('Input.dispatchMouseEvent', { type, x, y, button, buttons, clickCount, modifiers, deltaX, deltaY });
  }

  /** modifiers: 1 alt, 2 ctrl, 4 meta, 8 shift. */
  async click(point, { button = 'left', modifiers = 0 } = {}) {
    const buttons = { left: 1, right: 2, middle: 4 }[button];
    await this.mouse('mouseMoved', point, { modifiers });
    await this.mouse('mousePressed', point, { button, buttons, modifiers });
    await this.mouse('mouseReleased', point, { button, modifiers });
  }

  /** Press at from, move to to in steps (so pointermove handlers see a drag, not a jump), release. */
  async drag(from, to, { steps = 10, button = 'left', modifiers = 0 } = {}) {
    const buttons = { left: 1, right: 2, middle: 4 }[button];
    await this.mouse('mouseMoved', from, { modifiers });
    await this.mouse('mousePressed', from, { button, buttons, modifiers });
    for (let i = 1; i <= steps; i++) {
      const t = i / steps;
      await this.mouse('mouseMoved', [from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t], { button, buttons, modifiers });
    }
    await this.mouse('mouseReleased', to, { button, modifiers });
  }

  /** Saves a PNG of the viewport. */
  async screenshot(file) {
    const { data } = await this.send('Page.captureScreenshot', { format: 'png' });
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, Buffer.from(data, 'base64'));
    return file;
  }
}
