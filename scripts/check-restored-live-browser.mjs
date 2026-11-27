import assert from 'node:assert/strict';
import { chromium } from '@playwright/test';
import { build, preview } from 'vite';
import { mkdir } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { writeSync } from 'node:fs';

const api = new URL(process.env.RESTORED_SMOKE_API);
assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.protocol, 'http:');
assert.match(process.env.RESTORED_SMOKE_INSTANCE, /^[a-f0-9]{32}$/);
const runtimeRoot = process.env.RESTORED_SMOKE_ROOT;
assert.ok(runtimeRoot && isAbsolute(runtimeRoot), 'An owned restored runtime directory is required.');
assert.notEqual(resolve(runtimeRoot), resolve(process.cwd()));
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'true';
process.env.VITE_RESTORED_RUNTIME_INSTANCE_ID = process.env.RESTORED_SMOKE_INSTANCE;
process.env.VITE_API_URL = new URL('/api', api).href;
const port = Number(process.env.RESTORED_SMOKE_PORT);
assert.ok(Number.isInteger(port) && port > 0 && port < 65536, 'A preconfigured local UI port is required.');
const configuredBrowser = process.env.PAYPLANNER_TEST_BROWSER;
assert.ok(!configuredBrowser || ['msedge', 'chrome'].includes(configuredBrowser), 'Use the configured installed Edge or Chrome channel.');
const browserChannels = configuredBrowser ? [configuredBrowser]
  : [undefined, 'msedge', 'chrome'];
let browser;
let vite;
let page;
const errors = [];
const outside = [];
const failedRequests = [];
let phase = 'startup';
const notificationRequests = new Set();
const notificationLifecycle = { Started: 0, Finished: 0, FailedBeforeClose: 0, FailedDuringClose: 0, PendingBeforeClose: 0 };
let closingBrowser = false;
// Fixed categories and counters only: never environment, URLs, paths or page content.
const browserStarted = performance.now();
const browserPhases = new Set([
  'ui-build', 'preview-listen', 'launch-msedge', 'launch-chrome', 'launch-chromium',
  'browser-context', 'new-page', 'login-navigation', 'login-email', 'login-password', 'login-submit',
  'login-complete', 'user-menu', 'user-name', 'avatar', 'document-button', 'document-download',
  'download-read', 'client-portal-preview', 'activity-drain', 'notification-drain', 'final-assertions',
  'browser-close', 'vite-close', 'done',
]);
let browserPhase = 'ui-build';
const recordBrowserPhase = (nextPhase, event = 'enter', error) => {
  // Diagnostics never create a test gate or replace an existing failure.
  try {
    if (!browserPhases.has(nextPhase) || !['enter', 'failed', 'attempt-failed'].includes(event)) return;
    browserPhase = nextPhase;
    const errorKind = event === 'enter' ? 'none' : error instanceof Error
      ? error.name === 'TimeoutError' ? 'timeout' : error.name === 'AssertionError' ? 'assertion' : 'error'
      : 'unknown';
    // Emit before the next await; keep every diagnostic operation inside this guard.
    writeSync(1, `PP_RESTORED_BROWSER_PHASE ${JSON.stringify({
      Phase: browserPhase, Event: event, ElapsedMs: Math.round(performance.now() - browserStarted),
      ProcessUptimeMs: Math.round(process.uptime() * 1000),
      BrowserErrors: errors.length, OutsideRequests: outside.length, FailedRequests: failedRequests.length,
      PendingNotifications: notificationRequests.size, ErrorKind: errorKind,
    })}\n`);
  } catch { /* Best-effort diagnostics only; preserve the existing test/cleanup outcome. */ }
};
const isNotificationRequest = (request) => {
  const url = new URL(request.url());
  return url.origin === api.origin && url.pathname === '/api/notifications/inbox' && request.method() === 'GET';
};
try {
  recordBrowserPhase('ui-build');
  // Build this owned instance with its exact isolated API and instance identity.
  // Refuse an existing output directory; never clean the restored runtime.
  const outDir = join(runtimeRoot, 'browser-ui-dist');
  await mkdir(outDir);
  const uiConfig = { envDir: runtimeRoot, cacheDir: join(runtimeRoot, 'vite-browser-cache'),
    build: { outDir, emptyOutDir: false }, logLevel: 'error' };
  // Independent preparation shares the original 60-second budget. Wait for both
  // outcomes so even a failed build retains ownership of a launched browser.
  const browserPreparation = (async () => {
    for (const channel of browserChannels) {
      recordBrowserPhase(`launch-${channel ?? 'chromium'}`);
      try { return await chromium.launch({ headless: true, channel,
        ...(process.env.PAYPLANNER_TEST_BROWSER_PATH ? { executablePath: process.env.PAYPLANNER_TEST_BROWSER_PATH } : {}) }); }
      catch (error) { recordBrowserPhase(`launch-${channel ?? 'chromium'}`, 'attempt-failed', error); /* Never download a browser. */ }
    }
    assert.fail('An installed Chromium-compatible browser is required.');
  })();
  const [uiResult, browserResult] = await Promise.allSettled([build(uiConfig), browserPreparation]);
  if (browserResult.status === 'fulfilled') browser = browserResult.value;
  if (uiResult.status === 'rejected') throw uiResult.reason;
  if (browserResult.status === 'rejected') throw browserResult.reason;
  const built = uiResult.value;
  assert.ok((Array.isArray(built) ? built : [built]).some(output =>
    output.output?.some(chunk => chunk.type === 'chunk' && chunk.isEntry && chunk.code.length > 0)),
  'The restored UI entry module must compile successfully.');
  recordBrowserPhase('preview-listen');
  vite = await preview({ ...uiConfig, preview: { host: '127.0.0.1', port, strictPort: true } });
  assert.ok(browser, 'An installed Chromium-compatible browser is required.');
  recordBrowserPhase('browser-context');
  const context = await browser.newContext();
  recordBrowserPhase('new-page');
  page = await context.newPage();
  page.on('request', (request) => {
    if (isNotificationRequest(request)) { notificationLifecycle.Started += 1; notificationRequests.add(request); }
  });
  page.on('requestfinished', (request) => {
    if (notificationRequests.delete(request)) notificationLifecycle.Finished += 1;
  });
  page.on('requestfailed', (request) => {
    if (notificationRequests.delete(request)) {
      if (closingBrowser) notificationLifecycle.FailedDuringClose += 1;
      else notificationLifecycle.FailedBeforeClose += 1;
    }
  });
  page.setDefaultTimeout(20_000);
  const ui = `http://127.0.0.1:${port}`;
  const caseId = Number(process.env.RESTORED_SMOKE_CASE_ID);
  assert.ok(Number.isSafeInteger(caseId) && caseId > 0);
  const activityRequests = new Set();
  const activityCompletions = [];
  let activityStarted = 0;
  const isActivityRequest = (request) => {
    const url = new URL(request.url());
    return url.origin === api.origin && url.pathname === `/api/v2/cases/${caseId}/activity` && request.method() === 'POST';
  };
  page.on('request', (request) => {
    if (isActivityRequest(request)) { activityStarted += 1; activityRequests.add(request); }
  });
  page.on('requestfinished', (request) => activityRequests.delete(request));
  page.on('response', (response) => {
    if (!isActivityRequest(response.request())) return;
    activityCompletions.push((async () => {
      assert.equal(response.status(), 200, 'The existing case activity request must succeed.');
      assert.equal(await response.finished(), null, 'The activity response body must complete.');
      return { error: null };
    })().catch((error) => ({ error })));
  });
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('requestfailed', (request) => {
    const url = new URL(request.url());
    failedRequests.push({ phase, path: url.pathname, reason: request.failure()?.errorText });
  });
  await context.route('**/*', (route) => {
    const url = new URL(route.request().url());
    if (![api.origin, ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
    return route.continue();
  });
  phase = 'login';
  // Authenticate at the real document deep link. A forced full navigation
  // immediately after login aborts the newly mounted CRM's background reads.
  recordBrowserPhase('login-navigation');
  await page.goto(`${ui}/cases/${caseId}?ppCaseTab=documents`, { waitUntil: 'domcontentloaded' });
  recordBrowserPhase('login-email');
  const loginEmail = page.getByLabel('Email', { exact: true });
  await loginEmail.fill(process.env.RESTORED_SMOKE_EMAIL);
  recordBrowserPhase('login-password');
  await page.locator('input[type="password"]').fill(process.env.RESTORED_SMOKE_PASSWORD);
  // Observe existing activity responses throughout login and loading. Start the
  // completion budget when the required document is ready, not before login.
  recordBrowserPhase('login-submit');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  recordBrowserPhase('login-complete');
  await loginEmail.waitFor({ state: 'hidden' });
  recordBrowserPhase('user-menu');
  await page.getByRole('button', { name: 'Меню пользователя', exact: true }).click();
  recordBrowserPhase('user-name');
  await page.getByText('Synthetic restore operator', { exact: true }).first().waitFor();
  recordBrowserPhase('avatar');
  await page.waitForFunction(() => [...document.images].some((image) => image.src.includes('/user-photos/') && image.naturalWidth > 0));
  // The avatar above and the document action below prove the required UI data.
  // Continuous CRM requests are not a document-readiness signal.
  phase = 'case-document';
  const documentName = process.env.RESTORED_SMOKE_DOCUMENT_NAME;
  assert.ok(documentName);
  const downloadButton = page.getByRole('button', { name: `Скачать документ ${documentName}`, exact: true });
  recordBrowserPhase('document-button');
  const actionsButton = page.getByRole('button', { name: `Сведения и действия документа ${documentName}`, exact: true });
  await actionsButton.click();
  assert.equal(await actionsButton.getAttribute('aria-expanded'), 'true', 'The restored document actions must be expanded before downloading.');
  await downloadButton.waitFor();
  recordBrowserPhase('document-download');
  const [download] = await Promise.all([page.waitForEvent('download'), downloadButton.click()]);
  assert.equal(download.suggestedFilename(), documentName);
  assert.equal(await download.failure(), null);
  recordBrowserPhase('download-read');
  const stream = await download.createReadStream();
  assert.ok(stream);
  const chunks = [];
  for await (const chunk of stream) chunks.push(chunk);
  assert.deepEqual(Buffer.concat(chunks), Buffer.from(process.env.RESTORED_SMOKE_DOCUMENT_BASE64, 'base64'));

  phase = 'client-portal-preview';
  recordBrowserPhase('client-portal-preview');
  const previewButton = page.getByRole('button', { name: 'Посмотреть как клиент', exact: true });
  await previewButton.waitFor();
  const previewUrl = `${api.origin}/api/portal/admin/cases/${caseId}/preview`;
  const [portalResponse] = await Promise.all([
    page.waitForResponse(response => response.url() === previewUrl && response.request().method() === 'POST'),
    previewButton.click(),
  ]);
  assert.equal(portalResponse.status(), 200, 'The administrator must be able to preview this case.');
  const portalCase = await portalResponse.json();
  assert.ok(typeof portalCase.title === 'string' && portalCase.title.trim(), 'The preview must return the case title.');
  const portalView = page.getByRole('region', { name: 'Предпросмотр клиентского кабинета', exact: true });
  await portalView.getByRole('heading', { name: portalCase.title, exact: true }).waitFor();
  await portalView.getByRole('button', { name: 'Закрыть предпросмотр', exact: true }).click();
  await portalView.waitFor({ state: 'hidden' });

  assert.ok(activityStarted > 0, 'The real case page must begin its existing activity request.');
  recordBrowserPhase('activity-drain');
  if (activityRequests.size) {
    await new Promise((resolveActivity, rejectActivity) => {
      const cleanup = () => {
        clearTimeout(timer);
        page.off('requestfinished', finished);
        page.off('requestfailed', failed);
      };
      const finished = () => {
        if (!activityRequests.size) { cleanup(); resolveActivity(); }
      };
      const failed = (request) => {
        if (isActivityRequest(request)) { cleanup(); rejectActivity(new Error('The existing activity request failed.')); }
      };
      const timer = setTimeout(() => {
        cleanup(); rejectActivity(new Error('The existing activity request did not complete within 20000ms.'));
      }, 20000);
      page.on('requestfinished', finished);
      page.on('requestfailed', failed);
      finished();
    });
  }
  assert.ok(activityCompletions.length > 0, 'The existing activity request must have a response.');
  const activityResults = await Promise.all(activityCompletions);
  for (const activity of activityResults)
    assert.equal(activity.error, null, 'The browser smoke must complete its existing activity request before closing.');
  // Closing the browser aborts existing CRM reads. Complete the reads it has
  // actually begun, including chained startup batches, before destroying it.
  // This is part of the original enclosing 60-second browser budget.
  recordBrowserPhase('notification-drain');
  let notificationTimer;
  try {
    await Promise.race([
      (async () => {
        do {
          await Promise.all([...notificationRequests].map(async (request) => {
            const response = await request.response();
            assert.ok(response, 'An existing notification read must have a response.');
            assert.equal(response.status(), 200, 'An existing notification read must succeed.');
            assert.equal(await response.finished(), null, 'An existing notification response body must complete.');
          }));
          // Let the real page consume completed responses and start its next
          // batch. Do not issue synthetic requests or wait for polling to stop.
          await page.evaluate(() => new Promise((resolveFrame) => requestAnimationFrame(() => resolveFrame())));
          assert.equal(notificationLifecycle.FailedBeforeClose, 0, 'An existing notification read failed before browser teardown.');
        } while (notificationRequests.size);
      })(),
      new Promise((_, rejectNotifications) => {
        notificationTimer = setTimeout(() => rejectNotifications(new Error('Existing notification reads did not complete within 20000ms.')), 20000);
      }),
    ]);
  } finally {
    clearTimeout(notificationTimer);
  }
  recordBrowserPhase('final-assertions');
  assert.equal(notificationRequests.size, 0, 'The browser smoke must finish notification reads before closing.');
  assert.deepEqual(outside, [], 'The restored UI attempted an outside request.');
  assert.deepEqual(errors, [], 'The restored UI reported browser errors.');
  assert.ok(activityStarted > 0, 'The real case page must begin its existing activity request.');
  assert.equal(activityRequests.size, 0, 'The browser smoke reaches success while its case activity request is unfinished.');
  console.log('RESTORED_BROWSER_OK');
} catch (error) {
  // Report the failed boundary immediately; page inspection must not hide the primary error.
  recordBrowserPhase(browserPhase, 'failed', error);
  throw error;
} finally {
  notificationLifecycle.PendingBeforeClose = notificationRequests.size;
  closingBrowser = true;
  recordBrowserPhase('browser-close');
  if (browser) await browser.close();
  console.log(`PP_RESTORED_NOTIFICATION_LIFECYCLE ${JSON.stringify(notificationLifecycle)}`);
  recordBrowserPhase('vite-close');
  if (vite) await vite.close();
  recordBrowserPhase('done');
}
