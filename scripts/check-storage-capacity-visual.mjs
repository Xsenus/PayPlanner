import assert from 'node:assert/strict';
import { mkdir, mkdtemp, lstat, open, rm } from 'node:fs/promises';
import { writeSync } from 'node:fs';
import { resolve, basename, dirname, isAbsolute, join } from 'node:path';
import { tmpdir } from 'node:os';
import { chromium, expect } from '@playwright/test';
import { createServer } from 'vite';

const port = Number(process.env.STORAGE_CAPACITY_BROWSER_PORT ?? 5197);
assert.ok(Number.isSafeInteger(port) && port > 0 && port < 65536);
const origin = `http://127.0.0.1:${port}`;
const liveApi = process.env.STORAGE_CAPACITY_API ? new URL(process.env.STORAGE_CAPACITY_API) : null;
const liveToken = process.env.STORAGE_CAPACITY_TOKEN;
const writerRoot = process.env.STORAGE_CAPACITY_WRITER_ROOT;
const releasedMarkers = process.env.STORAGE_CAPACITY_MARKERS === 'true';
const configuredRoot = process.env.STORAGE_CAPACITY_ROOT;
if (configuredRoot !== undefined) {
  assert.ok(isAbsolute(configuredRoot), 'The browser root must be an owned absolute path.');
  assert.match(basename(configuredRoot), /^pp_task_upload_browser_[0-9a-f]{32}$/);
}
if (releasedMarkers) { assert.ok(liveApi); assert.equal(writerRoot, undefined); }
if (writerRoot) {
  assert.ok(liveApi, 'Writer recovery requires an owned backend.');
  assert.match(basename(writerRoot), /^pp_task_upload_browser_[0-9a-f]{32}$/);
  for (const path of [writerRoot, join(writerRoot, 'objects'), join(writerRoot, 'objects', '.task-uploads')]) {
    const attributes = await lstat(path);
    assert.ok(attributes.isDirectory() && !attributes.isSymbolicLink());
  }
}
if (liveApi) {
  assert.equal(liveApi.protocol, 'http:');
  assert.equal(liveApi.hostname, '127.0.0.1');
  assert.ok(liveToken, 'An owned-runtime token is required for the live browser check.');
}
const browserRoot = resolve(configuredRoot ?? await mkdtemp(join(tmpdir(), 'pp_storage_capacity_browser_')));
const rootAttributes = await lstat(browserRoot);
assert.ok(rootAttributes.isDirectory() && !rootAttributes.isSymbolicLink());
// Keep screenshots after the backend removes its owned temporary runtime.
const screenshotsDirectory = resolve('.codex-build', `storage-capacity-${basename(browserRoot)}`);
process.env.VITE_API_URL = liveApi ? new URL('/api', liveApi).href : `${origin}/api`;
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
let vite;
let browser;
const browserErrors = [];
const externalRequests = [];
const phaseStartedAt = performance.now();
const phaseNames = new Set([
  'module-ready', 'create-server', 'vite-listen', 'entry-transform',
  'launch-msedge', 'launch-chrome', 'launch-chromium', 'browser-context', 'new-page',
  'fixture-navigation', 'preview-assertions', 'screenshots-directory',
  'viewport-1280', 'screenshot-1280', 'viewport-390', 'screenshot-390',
  'writer-create', 'apply-click', 'apply-response', 'apply-json', 'apply-view',
  'final-assertions', 'browser-close', 'vite-close', 'done',
]);
let phase = 'module-ready';
let phaseEvents = 0;
let screenshots = 0;
function recordPhase(event = 'enter', error = null) {
  try {
    if (phaseEvents >= 64 || !phaseNames.has(phase)
      || !['enter', 'failed', 'attempt-failed'].includes(event)) return;
    const values = [Math.round(performance.now() - phaseStartedAt), Math.round(process.uptime() * 1000),
      browserErrors.length, externalRequests.length, screenshots];
    if (!values.every(value => Number.isSafeInteger(value) && value >= 0 && value <= 999_999_999)) return;
    const name = error instanceof Error ? error.name : null;
    const errorKind = error === null ? 'none' : name === 'TimeoutError' ? 'timeout'
      : name === 'AssertionError' ? 'assertion' : name === null ? 'unknown' : 'error';
    const marker = { Phase: phase, Event: event, ElapsedMs: values[0], ProcessUptimeMs: values[1],
      BrowserErrors: values[2], OutsideRequests: values[3], Screenshots: values[4], ErrorKind: errorKind };
    phaseEvents++;
    writeSync(1, `PP_STORAGE_CAPACITY_BROWSER_PHASE ${JSON.stringify(marker)}\n`);
  } catch { /* Optional diagnostics must never replace the scenario failure. */ }
}
function enterPhase(next) { phase = next; recordPhase(); }
recordPhase();

try {
  enterPhase('create-server');
  vite = await createServer({
    envDir: resolve('scripts'),
    cacheDir: join(browserRoot, 'vite-storage-capacity-cache'),
    // The default bundled config loader writes outside cacheDir to node_modules/.vite-temp.
    configLoader: 'runner',
    server: { host: '127.0.0.1', port, strictPort: true },
    logLevel: 'error',
  });
  enterPhase('vite-listen');
  await vite.listen();
  // Prepare this HTML fixture's entry, not the unused full application.
  // Keep that work inside the existing parent scenario budget.
  enterPhase('entry-transform');
  const entry = await vite.transformRequest('/src/components/Settings/StorageCapacityRecoverySettings.visual.tsx');
  assert.ok(entry?.code, 'The synthetic storage-capacity UI entry module must compile successfully.');
  for (const channel of ['msedge', 'chrome', undefined]) {
    enterPhase(`launch-${channel ?? 'chromium'}`);
    try { browser = await chromium.launch({ headless: true, channel,
      ...(process.env.PAYPLANNER_TEST_BROWSER_PATH ? { executablePath: process.env.PAYPLANNER_TEST_BROWSER_PATH } : {}) }); break; }
    catch (error) { recordPhase('attempt-failed', error); /* Do not download a browser. */ }
  }
  assert.ok(browser, 'An installed Chromium browser is required.');
  enterPhase('browser-context');
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, timezoneId: 'Asia/Novosibirsk' });
  enterPhase('new-page');
  const page = await context.newPage();
  if (liveToken) await page.addInitScript(token => localStorage.setItem('pp_token', token), liveToken);
  page.on('pageerror', error => browserErrors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') browserErrors.push(message.text()); });
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== origin && url.origin !== liveApi?.origin) {
      externalRequests.push(url.origin);
      await route.abort();
      return;
    }
    if (url.pathname.startsWith('/api/user-settings/')) {
      await route.fulfill({ json: { value: 'Asia/Novosibirsk' } });
      return;
    }
    if (liveApi && url.pathname === '/api/auth/refresh') {
      await route.fulfill({ json: { token: liveToken } });
      return;
    }
    if (liveApi) {
      await route.continue();
      return;
    }
    if (url.pathname === '/api/storage/health') {
      await route.fulfill({ json: {
        status: 'degraded', pendingCapacityReservations: 4,
        oldestCapacityReservationAt: '2026-09-29T12:00:00Z',
        pendingStageCapacityReservations: 3,
        oldestStageCapacityReservationAt: '2026-09-29T12:00:00Z',
      } });
      return;
    }
    if (url.pathname === '/api/storage/capacity/stages/reconcile') {
      await route.fulfill({ json: {
        inspected: 3, eligible: 1, settled: 0, retained: 3,
        hasMore: false, nextAfterKey: null,
        items: [
          { key: `stage:${'a'.repeat(64)}`, status: 'eligible', relativePath: '.task-uploads/complete.tmp',
            expectedBytes: 4096, observedBytes: 4096, updatedAt: '2026-09-29T12:00:00Z' },
          { key: `stage:${'b'.repeat(64)}`, status: 'incompleteFile', relativePath: '.task-uploads/partial.tmp',
            expectedBytes: 4096, observedBytes: 1024, updatedAt: '2026-09-29T12:00:00Z' },
          { key: `stage:${'c'.repeat(64)}`, status: 'invalidAuthority',
            expectedBytes: 4096, updatedAt: '2026-09-29T12:00:00Z' },
        ],
      } });
      return;
    }
    await route.continue();
  });
  enterPhase('fixture-navigation');
  const apiOrigin = liveApi?.origin ?? origin;
  const previewPath = `/api/storage/capacity/stages/${releasedMarkers ? 'markers/' : ''}reconcile`;
  const initialReads = [page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.origin === apiOrigin && url.pathname === previewPath
      && url.searchParams.get('apply') === 'false' && response.request().method() === 'POST';
  })];
  if (!releasedMarkers) {
    initialReads.push(page.waitForResponse(response =>
      response.url() === `${apiOrigin}/api/storage/health` && response.request().method() === 'GET'));
  }
  const [, initialResponses] = await Promise.all([
    page.goto(`${origin}/scripts/fixtures/storage-capacity-visual.html${releasedMarkers ? '?markers=true' : ''}`, { waitUntil: 'domcontentloaded' }),
    Promise.all(initialReads),
  ]);
  for (const response of initialResponses) {
    assert.equal(response.status(), 200, 'The storage preview must load successfully.');
    assert.equal(await response.finished(), null, 'The storage preview response must be complete.');
  }
  enterPhase('preview-assertions');
  if (!releasedMarkers) await expect(page.getByText('Файл неполный — резерв сохранён')).toBeVisible();
  if (releasedMarkers) {
    await expect(page.getByText('Резерв освобождён — служебный маркер можно удалить')).toBeVisible();
    await expect(page.getByText('Запись или подлинность маркера не подтверждены — маркер сохранён')).toHaveCount(2);
    await expect(page.getByRole('button', { name: 'Очистить маркеры (1)' })).toBeEnabled();
  } else if (writerRoot) {
    await expect(page.getByText('Запись файла может продолжаться — резерв сохранён')).toBeVisible();
    await expect(page.getByText('Файл отсутствует, запись остановлена — резерв можно освободить')).toHaveCount(2);
    await expect(page.getByRole('button', { name: 'Подтвердить восстановление (2)' })).toBeEnabled();
    await expect(page.getByText('Проверено: 4. Можно восстановить резервов: 2. Без изменений: 4.')).toBeVisible();
  } else {
    await expect(page.getByText('Путь требует разбора — резерв сохранён')).toBeVisible();
    await expect(page.getByText('Полный файл — можно подтвердить')).toBeVisible();
  }
  enterPhase('screenshots-directory');
  await mkdir(screenshotsDirectory, { recursive: true });
  for (const width of [1280, 390]) {
    enterPhase(`viewport-${width}`);
    await page.setViewportSize({ width, height: 900 });
    if (releasedMarkers) await expect(page.getByRole('heading', { name: 'Очистка служебных маркеров' })).toBeVisible();
    else await expect(page.getByText('Из них для временных файлов:')).toBeVisible();
    const horizontalOverflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    assert.equal(horizontalOverflow, false, `Horizontal overflow at ${width}px.`);
    enterPhase(`screenshot-${width}`);
    await page.screenshot({ path: join(screenshotsDirectory, `storage-capacity-${releasedMarkers ? 'released-markers-' : writerRoot ? 'writer-' : ''}${width}.png`), fullPage: true });
    screenshots++;
  }
  if (liveApi) {
    if (writerRoot) {
      enterPhase('writer-create');
      const file = await open(join(writerRoot, 'objects', '.task-uploads', 'changed.tmp'), 'wx');
      try { await file.writeFile(Buffer.from([3, 4])); await file.sync(); }
      finally { await file.close(); }
    }
    const appliedResponse = page.waitForResponse(response =>
      new URL(response.url()).pathname === `/api/storage/capacity/stages/${releasedMarkers ? 'markers/' : ''}reconcile`
      && new URL(response.url()).searchParams.get('apply') === 'true');
    enterPhase('apply-click');
    await page.getByRole('button', { name: releasedMarkers ? /Очистить маркеры/ : /Подтвердить восстановление/ }).click();
    enterPhase('apply-response');
    const applied = await appliedResponse;
    assert.equal(applied.status(), 200);
    if (releasedMarkers) {
      enterPhase('apply-json');
      const report = await applied.json();
      assert.equal(report.removed, 1);
      assert.equal(report.items.filter(item => item.status === 'releasedMarkerRemoved').length, 1);
      assert.equal(report.items.filter(item => item.status === 'markerNotProvenStopped').length, 2);
      enterPhase('apply-view');
      await expect(page.getByText('Служебного маркера нет')).toBeVisible();
      await expect(page.getByText('Резерв освобождён — служебный маркер можно удалить')).toHaveCount(0);
      await expect(page.getByRole('button', { name: /Очистить маркеры/ })).toBeDisabled();
    } else if (writerRoot) {
      enterPhase('apply-json');
      const report = await applied.json();
      assert.equal(report.settled, 1);
      assert.equal(report.items.filter(item => item.status === 'releasedMissingStage').length, 1);
      assert.equal(report.items.filter(item => item.status === 'writerNotProvenStopped').length, 1);
      assert.equal(report.items.filter(item => item.status === 'incompleteFile').length, 2);
      enterPhase('apply-view');
      await expect(page.getByText('Файл отсутствует, запись остановлена — резерв можно освободить')).toHaveCount(0);
      await expect(page.getByText('Файл неполный — резерв сохранён')).toHaveCount(2);
      await expect(page.getByText('Запись файла может продолжаться — резерв сохранён')).toBeVisible();
      await expect(page.getByRole('button', { name: /Подтвердить восстановление/ })).toBeDisabled();
    }
    if (!releasedMarkers) {
      enterPhase('apply-view');
      await expect(page.getByText('Полный файл — можно подтвердить')).toHaveCount(0, { timeout: 15_000 });
      await expect.poll(async () => page.locator('section').first().innerText(), { timeout: 15_000 })
        .toContain(`Ожидающих резервов всех загрузок: ${writerRoot ? 3 : 2}`);
    }
  }
  enterPhase('final-assertions');
  assert.deepEqual(browserErrors, []);
  assert.deepEqual(externalRequests, []);
  console.log(`Storage capacity screenshots: ${screenshotsDirectory}`);
  console.log(`Storage capacity ${liveApi ? 'owned backend' : 'visual'} browser check passed at 1280px and 390px; no console errors or external requests.`);
} catch (error) {
  recordPhase('failed', error);
  throw error;
} finally {
  try {
    if (browser) { enterPhase('browser-close'); await browser.close(); }
  } finally {
    try {
      if (vite) { enterPhase('vite-close'); await vite.close(); }
    } finally {
      if (configuredRoot === undefined) {
        assert.ok(isAbsolute(browserRoot));
        assert.equal(dirname(browserRoot), resolve(tmpdir()));
        assert.match(basename(browserRoot), /^pp_storage_capacity_browser_[A-Za-z0-9]{6}$/);
        const currentRoot = await lstat(browserRoot);
        assert.ok(currentRoot.isDirectory() && !currentRoot.isSymbolicLink());
        assert.equal(currentRoot.dev, rootAttributes.dev);
        assert.equal(currentRoot.ino, rootAttributes.ino);
        await rm(browserRoot, { recursive: true });
      }
    }
  }
}
enterPhase('done');
