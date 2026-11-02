import { once } from 'node:events';
import { createServer as createHttpServer } from 'node:http';
import { chromium, type Browser, type Request as BrowserRequest } from '@playwright/test';
import { afterEach, expect, it, vi } from 'vitest';
import { createServer, type Plugin } from 'vite';
import { createPayPlannerViteConfig } from './vite.config';

declare global {
  interface Window {
    __restoredCspBlocked: Array<{ directive: string; uri: string }>;
    __restoredProbe: {
      prepare: () => Promise<void>;
      request: () => Promise<Response>;
      download: () => Promise<void>;
      photo: (url: string) => string | undefined;
    };
    __restoredDownloadState?: { activeBefore: boolean; activeAfter: boolean; elapsed: number; clicked: boolean; origin: string };
  }
}

const ID = 'cefe9b7e3bb64c51b3c80a9e96bb57ea';
afterEach(() => vi.unstubAllEnvs());

async function launchBrowser(): Promise<Browser> {
  // Версия Chromium должна соответствовать Playwright; установленные системные браузеры остаются запасным вариантом.
  for (const channel of [undefined, 'msedge', 'chrome']) {
    try { return await chromium.launch({ headless: true, channel }); } catch { /* Try installed alternatives, never download a browser. */ }
  }
  throw new Error('Для браузерного теста нужен установленный Chromium/Edge/Chrome.');
}

async function qualifyNativeBrowserFixture(coldTransformDelayMs = 0, coldClientTransformDelayMs = 0, coldTransitiveTransformDelayMs = 0) {
  let identity = ID;
  let uiOrigin = '';
  const requests: Array<{ path: string; marker?: string }> = [];
  const api = createHttpServer((request, response) => {
    response.setHeader('Access-Control-Allow-Origin', uiOrigin);
    response.setHeader('Access-Control-Allow-Headers', 'content-type,x-payplanner-restored-instance');
    response.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
    if (request.method === 'OPTIONS') { response.writeHead(204).end(); return; }
    const url = new URL(request.url!, 'http://127.0.0.1');
    if (url.pathname === '/api/restored-runtime/identity') {
      requests.push({ path: url.pathname });
      response.setHeader('Content-Type', 'application/json');
      response.end(JSON.stringify({ isolated: true, instanceId: identity }));
      return;
    }
    const marker = request.headers['x-payplanner-restored-instance'] as string | undefined;
    requests.push({ path: url.pathname, marker });
    if (url.pathname === '/user-photos/fixture.png' && url.searchParams.get('pp_restored_instance') === ID) {
      response.setHeader('Content-Type', 'image/png');
      response.end(Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=', 'base64'));
    } else if (marker !== ID) {
      response.writeHead(409).end();
    } else if (url.pathname === '/api/file') {
      response.setHeader('Content-Type', 'text/plain');
      response.end('restored fixture document');
    } else {
      response.setHeader('Content-Type', 'application/json');
      response.end(JSON.stringify({ ok: true }));
    }
  });
  api.listen(0, '127.0.0.1');
  await once(api, 'listening');
  const apiAddress = api.address();
  if (!apiAddress || typeof apiAddress === 'string') throw new Error('No fixture API port');
  const apiUrl = `http://127.0.0.1:${apiAddress.port}/api`;
  const reservation = createHttpServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const uiAddress = reservation.address();
  if (!uiAddress || typeof uiAddress === 'string') throw new Error('No fixture UI port');
  const uiPort = uiAddress.port;
  await new Promise<void>((resolve, reject) => reservation.close(error => error ? reject(error) : resolve()));
  uiOrigin = `http://127.0.0.1:${uiPort}`;
  const env = { VITE_RESTORED_RUNTIME_ISOLATION: 'true', VITE_RESTORED_RUNTIME_INSTANCE_ID: ID, VITE_API_URL: apiUrl };
  Object.entries(env).forEach(([key, value]) => vi.stubEnv(key, value));
  let browser: Browser | undefined;
  let server: Awaited<ReturnType<typeof createServer>> | undefined;
  try {
    const config = createPayPlannerViteConfig(env);
    let coldTransformFinished = coldTransformDelayMs === 0;
    let coldClientTransformFinished = coldClientTransformDelayMs === 0;
    const transitivePaths = ['/src/services/apiBaseUrl.ts', '/src/utils/restoredRuntimeSafety.ts', '/src/services/restoredRuntimeFetch.ts'];
    let coldTransitiveArmed = false;
    const coldTransitiveStarted = new Set<string>();
    const coldTransitiveFinished = new Set<string>();
    const probeModuleId = '\0payplanner-restored-browser-probe';
    const probeHtml = '<!doctype html><head></head><body><button id="fixture-download" type="button">Скачать файл</button><script type="module" src="/__probe.js"></script></body>';
    const fixturePlugin: Plugin = { name: 'restored-browser-fixture',
      resolveId(id) { return id === '/__probe.js' ? probeModuleId : null; },
      load(id) {
        if (id === probeModuleId) return `
          import { prepareRestoredBrowserSession } from '/src/services/restoredBrowserSession.ts';
          import { apiFetch } from '/src/services/apiTransport.ts';
          import { restoredPhotoUrl } from '/src/services/restoredRealtime.ts';
          window.__restoredCspBlocked = [];
          document.addEventListener('securitypolicyviolation', e => window.__restoredCspBlocked.push({ directive: e.effectiveDirective, uri: e.blockedURI }));
          window.__restoredProbe = {
            prepare: prepareRestoredBrowserSession,
            request: () => apiFetch(${JSON.stringify(`${apiUrl}/probe`)}, { method: 'POST', body: 'fixture' }),
            photo: restoredPhotoUrl,
            download: async () => {
              const started = performance.now();
              const activeBefore = navigator.userActivation.isActive;
              const response = await apiFetch(${JSON.stringify(`${apiUrl}/file`)});
              const url = URL.createObjectURL(await response.blob());
              const link = document.createElement('a'); link.href = url; link.download = 'fixture.txt';
              document.body.appendChild(link);
              window.__restoredDownloadState = { activeBefore, activeAfter: navigator.userActivation.isActive, elapsed: performance.now() - started, clicked: false, origin: location.origin };
              link.click();
              window.__restoredDownloadState.clicked = true;
              link.remove();
              setTimeout(() => URL.revokeObjectURL(url), 1000);
            }
          };
          document.getElementById('fixture-download').addEventListener('click', () => void window.__restoredProbe.download());
        `;
        return null;
      },
      async transform(_code, id) {
        if (!coldClientTransformFinished && coldClientTransformDelayMs > 0 && id.replaceAll('\\', '/').split('?')[0].includes('/vite/dist/client/client.mjs')) {
          await new Promise(resolve => setTimeout(resolve, coldClientTransformDelayMs));
          coldClientTransformFinished = true;
        }
        if (!coldTransformFinished && coldTransformDelayMs > 0 && id.replaceAll('\\', '/').split('?')[0].endsWith('/src/services/restoredBrowserSession.ts')) {
          await new Promise(resolve => setTimeout(resolve, coldTransformDelayMs));
          coldTransformFinished = true;
        }
        if (coldTransitiveArmed && coldTransitiveTransformDelayMs > 0) {
          const path = transitivePaths.find(path => id.replaceAll('\\', '/').split('?')[0].endsWith(path));
          if (path && !coldTransitiveStarted.has(path)) {
            coldTransitiveStarted.add(path);
            await new Promise(resolve => setTimeout(resolve, coldTransitiveTransformDelayMs));
            coldTransitiveFinished.add(path);
          }
        }
        return null;
      }, configureServer(vite) {
      vite.middlewares.use((request, response, next) => {
      if (request.url === '/__probe-worker.js') {
        response.setHeader('Content-Type', 'application/javascript');
        response.end("self.addEventListener('install', () => self.skipWaiting()); self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));");
      } else if (request.url === '/__probe.html') {
        response.setHeader('Content-Type', 'text/html; charset=utf-8');
        response.setHeader('Content-Security-Policy', String(vite.config.server.headers?.['Content-Security-Policy']));
        response.end(preparedHtml);
      } else next();
      });
    } };
    server = await createServer({ ...config, plugins: [...(config.plugins ?? []), fixturePlugin], configFile: false, server: { ...config.server, port: uiPort, watch: null } });
    await server.listen();
    const preparedHtml = await server.transformIndexHtml('/__probe.html', probeHtml);
    // Compile the fixture's actual imports before starting the navigation clock,
    // as the task-browser fixture does. Preparation remains inside the existing
    // 90-second scenario; every browser action still has its 10-second deadline.
    for (const entry of ['/__probe.js', '/src/services/restoredBrowserSession.ts', '/src/services/apiTransport.ts', '/src/services/restoredRealtime.ts', '/@vite/client', '/@react-refresh']) {
      const transformed = await server.transformRequest(entry);
      expect(transformed?.code).toBeTruthy();
    }
    await server.waitForRequestsIdle();
    const optimizer = server.environments.client.depsOptimizer;
    if (!optimizer) throw new Error('The fixture dependency optimizer is unavailable');
    await optimizer.scanProcessing;
    await Promise.all(optimizer.metadata.depInfoList.map(dependency => dependency.processing));
    if (coldTransitiveTransformDelayMs > 0) {
      server.environments.client.moduleGraph.invalidateAll();
      coldTransitiveArmed = true;
    }
    // The optimizer can invalidate transforms after its processing promises
    // settle. waitForRequestsIdle only tracks the initial static crawl, so
    // explicitly compile the real current import graph after that boundary.
    const client = server.environments.client;
    const preparedModules = new Set<string>();
    let pendingModules = ['/__probe.js', '/src/services/restoredBrowserSession.ts', '/src/services/apiTransport.ts', '/src/services/restoredRealtime.ts', '/@vite/client', '/@react-refresh'];
    while (pendingModules.length) {
      const batch = [...new Set(pendingModules)].filter(url => !preparedModules.has(url));
      for (const url of batch) preparedModules.add(url);
      if (preparedModules.size > 128) throw new Error('Fixture import graph exceeds its reviewed preparation bound');
      const transformed = await Promise.all(batch.map(url => client.transformRequest(url)));
      for (const result of transformed) expect(result?.code).toBeTruthy();
      const modules = await Promise.all(batch.map(url => client.moduleGraph.getModuleByUrl(url)));
      pendingModules = modules.flatMap(module => {
        if (!module) throw new Error('Prepared fixture module is missing from its actual graph');
        return [...module.importedModules].map(imported => imported.url);
      });
    }
    for (const url of preparedModules) {
      const module = await client.moduleGraph.getModuleByUrl(url);
      expect(module?.transformResult?.code).toBeTruthy();
    }
    expect([...coldTransitiveFinished].sort()).toEqual(coldTransitiveTransformDelayMs > 0 ? [...transitivePaths].sort() : []);
    expect(coldTransformFinished).toBe(true);
    expect(coldClientTransformFinished).toBe(true);
    browser = await launchBrowser();
    const outside: string[] = [];
    const context = await browser.newContext({ acceptDownloads: true });
    await context.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.hostname !== '127.0.0.1') { outside.push(url.origin); return route.abort(); }
      return route.continue();
    });
    const page = await context.newPage();
    page.setDefaultTimeout(10_000);
    const errors: string[] = [];
    const consoleErrors: string[] = [];
    let hmrConnected = false;
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
    page.on('websocket', socket => socket.on('framereceived', frame => {
      if (new URL(socket.url()).origin === uiOrigin.replace('http:', 'ws:')
        && frame.payload.toString().includes('"type":"connected"')) hmrConnected = true;
    }));
    const pendingNavigation = new Map<BrowserRequest, string>();
    const trackNavigation = (request: BrowserRequest) => pendingNavigation.set(request, new URL(request.url()).pathname.slice(0, 160));
    const finishNavigation = (request: BrowserRequest) => pendingNavigation.delete(request);
    page.on('request', trackNavigation);
    page.on('requestfinished', finishNavigation);
    page.on('requestfailed', finishNavigation);
    try {
      await page.goto(`${uiOrigin}/__probe.html`);
    } catch (cause) {
      const diagnostics = { browserVersion: browser.version(), pendingPaths: [...pendingNavigation.values()].slice(0, 16),
        errors: errors.slice(0, 8), consoleErrors: consoleErrors.slice(0, 8) };
      throw new Error(`Native fixture navigation failed: ${JSON.stringify(diagnostics)}`, { cause });
    } finally {
      page.off('request', trackNavigation);
      page.off('requestfinished', finishNavigation);
      page.off('requestfailed', finishNavigation);
    }
    await page.waitForFunction(() => Boolean(window.__restoredProbe));
    await vi.waitFor(() => expect(hmrConnected).toBe(true), { timeout: 5000 });
    await page.evaluate(() => window.__restoredProbe.prepare());
    expect(await page.evaluate(() => localStorage.getItem('pp_restored_browser_instance'))).toBe(ID);
    await page.evaluate(async () => {
      const responses = await Promise.all([window.__restoredProbe.request(), window.__restoredProbe.request()]);
      if (responses.some(response => !response.ok)) throw new Error('Probe request rejected');
    });
    expect(requests.filter(request => request.path.endsWith('/identity'))).toHaveLength(1);
    expect(requests.filter(request => request.path.endsWith('/probe'))).toEqual([
      { path: '/api/probe', marker: ID }, { path: '/api/probe', marker: ID },
    ]);
    expect(await page.evaluate(() => new Promise<boolean>(resolve => {
      const image = new Image(); image.onload = () => resolve(image.naturalWidth === 1); image.onerror = () => resolve(false);
      image.src = window.__restoredProbe.photo('/user-photos/fixture.png')!;
    }))).toBe(true);
    const downloading = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Скачать файл', exact: true }).click();
    let downloaded;
    try {
      downloaded = await downloading;
    } catch (cause) {
      const diagnostics = { requests, outside, errors, consoleErrors,
        browserVersion: browser.version(),
        blocked: await page.evaluate(() => window.__restoredCspBlocked),
        downloadState: await page.evaluate(() => window.__restoredDownloadState),
      };
      throw new Error(`Native fixture download failed: ${JSON.stringify(diagnostics)}`, { cause });
    }
    expect(downloaded.suggestedFilename()).toBe('fixture.txt');
    expect(await downloaded.failure()).toBeNull();
    const stream = await downloaded.createReadStream();
    if (!stream) throw new Error('Download stream missing');
    let contents = '';
    for await (const chunk of stream) contents += chunk.toString();
    expect(contents).toBe('restored fixture document');
    expect(consoleErrors).toEqual([]);
    expect(await page.evaluate(async () => {
      try { await fetch('https://fixture.invalid/blocked'); return false; } catch { return true; }
    })).toBe(true);
    expect(await page.evaluate(() => new Promise<boolean>(resolve => {
      const image = new Image(); image.onload = () => resolve(false); image.onerror = () => resolve(true);
      image.src = 'https://fixture.invalid/blocked.png';
    }))).toBe(true);
    await page.waitForFunction(() => window.__restoredCspBlocked.some(event => event.directive === 'connect-src')
      && window.__restoredCspBlocked.some(event => event.directive === 'img-src'));
    expect(outside).toEqual([]);
    expect(errors).toEqual([]);
    await context.close();

    for (const dirty of ['storage', 'cache', 'worker', 'database']) {
      const isolatedContext = await browser.newContext();
      const dirtyPage = await isolatedContext.newPage();
      dirtyPage.setDefaultTimeout(10_000);
      await dirtyPage.goto(`${uiOrigin}/__probe.html`);
      await dirtyPage.waitForFunction(() => Boolean(window.__restoredProbe));
      await dirtyPage.evaluate(async kind => {
        if (kind === 'storage') localStorage.setItem('other-app', 'fixture');
        if (kind === 'cache') await caches.open('other-app-cache');
        if (kind === 'worker') { await navigator.serviceWorker.register('/__probe-worker.js'); await navigator.serviceWorker.ready; }
        if (kind === 'database') await new Promise<void>((resolve, reject) => {
          const request = indexedDB.open('other-app-db', 1);
          request.onsuccess = () => { request.result.close(); resolve(); };
          request.onerror = () => reject(request.error);
        });
      }, dirty);
      expect(await dirtyPage.evaluate(async () => {
        try { await window.__restoredProbe.prepare(); return false; } catch { return true; }
      })).toBe(true);
      expect(await dirtyPage.evaluate(() => localStorage.getItem('pp_restored_browser_instance'))).toBeNull();
      if (dirty === 'storage') expect(await dirtyPage.evaluate(() => localStorage.getItem('other-app'))).toBe('fixture');
      if (dirty === 'cache') expect(await dirtyPage.evaluate(() => caches.keys())).toContain('other-app-cache');
      if (dirty === 'worker') expect(await dirtyPage.evaluate(async () => (await navigator.serviceWorker.getRegistrations()).length)).toBe(1);
      if (dirty === 'database') expect(await dirtyPage.evaluate(async () => (await indexedDB.databases()).map(db => db.name))).toContain('other-app-db');
      await isolatedContext.close();
    }
    identity = '00000000000000000000000000000000';
    const mismatchContext = await browser.newContext();
    const mismatchPage = await mismatchContext.newPage();
    mismatchPage.setDefaultTimeout(10_000);
    await mismatchPage.goto(`${uiOrigin}/__probe.html`);
    await mismatchPage.waitForFunction(() => Boolean(window.__restoredProbe));
    const writesBefore = requests.filter(request => request.path.endsWith('/probe')).length;
    expect(await mismatchPage.evaluate(async () => {
      try { await window.__restoredProbe.request(); return false; } catch { return true; }
    })).toBe(true);
    expect(requests.filter(request => request.path.endsWith('/probe'))).toHaveLength(writesBefore);
    await mismatchContext.close();
  } finally {
    await browser?.close();
    await server?.close();
    await new Promise<void>((resolve, reject) => api.close(error => error ? reject(error) : resolve()));
  }
}

it('qualifies native browser CSP, probe transport and clean-profile barriers against loopback fixtures',
  () => qualifyNativeBrowserFixture(), 90_000);

it('prepares cold fixture imports before navigation while retaining all native browser barriers',
  () => qualifyNativeBrowserFixture(11_000), 90_000);


it('prepares the real cold HMR client before navigation while retaining all native browser barriers',
  () => qualifyNativeBrowserFixture(0, 11_000), 90_000);

it('prepares cold transitive imports after cache invalidation before navigation with all native barriers',
  () => qualifyNativeBrowserFixture(0, 0, 11_000), 90_000);
