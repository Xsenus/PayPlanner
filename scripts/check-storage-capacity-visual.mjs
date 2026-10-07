import assert from 'node:assert/strict';
import { mkdir, lstat, open } from 'node:fs/promises';
import { resolve, basename, join } from 'node:path';
import { chromium, expect } from '@playwright/test';
import { createServer } from 'vite';

const port = Number(process.env.STORAGE_CAPACITY_BROWSER_PORT ?? 5197);
assert.ok(Number.isSafeInteger(port) && port > 0 && port < 65536);
const origin = `http://127.0.0.1:${port}`;
const liveApi = process.env.STORAGE_CAPACITY_API ? new URL(process.env.STORAGE_CAPACITY_API) : null;
const liveToken = process.env.STORAGE_CAPACITY_TOKEN;
const writerRoot = process.env.STORAGE_CAPACITY_WRITER_ROOT;
const releasedMarkers = process.env.STORAGE_CAPACITY_MARKERS === 'true';
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
process.env.VITE_API_URL = liveApi ? new URL('/api', liveApi).href : `${origin}/api`;
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
let vite;
let browser;
const browserErrors = [];
const externalRequests = [];

try {
  vite = await createServer({
    envDir: resolve('scripts'),
    server: { host: '127.0.0.1', port, strictPort: true },
    logLevel: 'error',
  });
  await vite.listen();
  for (const channel of ['msedge', 'chrome', undefined]) {
    try { browser = await chromium.launch({ headless: true, channel }); break; }
    catch { /* Do not download a browser. */ }
  }
  assert.ok(browser, 'An installed Chromium browser is required.');
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, timezoneId: 'Asia/Novosibirsk' });
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
  await page.goto(`${origin}/scripts/fixtures/storage-capacity-visual.html${releasedMarkers ? '?markers=true' : ''}`, { waitUntil: 'domcontentloaded' });
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
  await mkdir('.codex-build', { recursive: true });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    if (releasedMarkers) await expect(page.getByRole('heading', { name: 'Очистка служебных маркеров' })).toBeVisible();
    else await expect(page.getByText('Из них для временных файлов:')).toBeVisible();
    const horizontalOverflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    assert.equal(horizontalOverflow, false, `Horizontal overflow at ${width}px.`);
    await page.screenshot({ path: `.codex-build/storage-capacity-${releasedMarkers ? 'released-markers-' : writerRoot ? 'writer-' : ''}${width}.png`, fullPage: true });
  }
  if (liveApi) {
    if (writerRoot) {
      const file = await open(join(writerRoot, 'objects', '.task-uploads', 'changed.tmp'), 'wx');
      try { await file.writeFile(Buffer.from([3, 4])); await file.sync(); }
      finally { await file.close(); }
    }
    const appliedResponse = page.waitForResponse(response =>
      new URL(response.url()).pathname === `/api/storage/capacity/stages/${releasedMarkers ? 'markers/' : ''}reconcile`
      && new URL(response.url()).searchParams.get('apply') === 'true');
    await page.getByRole('button', { name: releasedMarkers ? /Очистить маркеры/ : /Подтвердить восстановление/ }).click();
    const applied = await appliedResponse;
    assert.equal(applied.status(), 200);
    if (releasedMarkers) {
      const report = await applied.json();
      assert.equal(report.removed, 1);
      assert.equal(report.items.filter(item => item.status === 'releasedMarkerRemoved').length, 1);
      assert.equal(report.items.filter(item => item.status === 'markerNotProvenStopped').length, 2);
      await expect(page.getByText('Служебного маркера нет')).toBeVisible();
      await expect(page.getByText('Резерв освобождён — служебный маркер можно удалить')).toHaveCount(0);
      await expect(page.getByRole('button', { name: /Очистить маркеры/ })).toBeDisabled();
    } else if (writerRoot) {
      const report = await applied.json();
      assert.equal(report.settled, 1);
      assert.equal(report.items.filter(item => item.status === 'releasedMissingStage').length, 1);
      assert.equal(report.items.filter(item => item.status === 'writerNotProvenStopped').length, 1);
      assert.equal(report.items.filter(item => item.status === 'incompleteFile').length, 2);
      await expect(page.getByText('Файл отсутствует, запись остановлена — резерв можно освободить')).toHaveCount(0);
      await expect(page.getByText('Файл неполный — резерв сохранён')).toHaveCount(2);
      await expect(page.getByText('Запись файла может продолжаться — резерв сохранён')).toBeVisible();
      await expect(page.getByRole('button', { name: /Подтвердить восстановление/ })).toBeDisabled();
    }
    if (!releasedMarkers) {
      await expect(page.getByText('Полный файл — можно подтвердить')).toHaveCount(0, { timeout: 15_000 });
      await expect.poll(async () => page.locator('section').first().innerText(), { timeout: 15_000 })
        .toContain(`Ожидающих резервов всех загрузок: ${writerRoot ? 3 : 2}`);
    }
  }
  assert.deepEqual(browserErrors, []);
  assert.deepEqual(externalRequests, []);
  console.log(`Storage capacity ${liveApi ? 'owned backend' : 'visual'} browser check passed at 1280px and 390px; no console errors or external requests.`);
} finally {
  if (browser) await browser.close();
  if (vite) await vite.close();
}
