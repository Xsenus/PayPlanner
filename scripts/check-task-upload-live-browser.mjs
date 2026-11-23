import assert from 'node:assert/strict';
import { chromium, expect } from '@playwright/test';
import { basename, join } from 'node:path';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { runTaskUploadRoute } from './task-upload-route-lifecycle.mjs';
import { installTaskNotificationRefreshObserver } from './task-notification-refresh-observer.mjs';
import { prepareSyntheticBrowserUi } from './synthetic-browser-ui.mjs';

console.log('TASK_UPLOAD_STAGE_MODULES_READY');

const api = new URL(process.env.TASK_UPLOAD_API);
assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.protocol, 'http:');
assert.match(basename(process.env.TASK_UPLOAD_ROOT), /^pp_task_upload_browser_[a-f0-9]{32}$/);
const mode = process.env.TASK_UPLOAD_CARD_MODE;
assert.ok(['legacy', 'modern'].includes(mode));
const port = Number(process.env.TASK_UPLOAD_PORT);
let taskId = Number(process.env.TASK_UPLOAD_ID);
assert.ok(Number.isSafeInteger(port) && port > 0 && port < 65536);
assert.ok(Number.isSafeInteger(taskId) && taskId > 0);
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
process.env.VITE_API_URL = new URL('/api', api).href;
const ui = `http://127.0.0.1:${port}`;
const queueReplay = process.env.TASK_UPLOAD_QUEUE_REPLAY === 'true';
const delayCardSettings = process.env.TASK_UPLOAD_DELAY_CARD_SETTINGS === 'true';
const delayedSettingsRelease = delayCardSettings ? Promise.withResolvers() : null;
const liveFlow = process.env.TASK_UPLOAD_FLOW === 'live';
const completionSaveFailure = process.env.TASK_UPLOAD_FLOW === 'completionSaveFailure';
const completionFlow = process.env.TASK_UPLOAD_FLOW === 'completion' || completionSaveFailure;
const creationFlow = process.env.TASK_UPLOAD_FLOW === 'creation';
assert.ok(!completionFlow || queueReplay);
assert.ok(!creationFlow || queueReplay);
const fileName = queueReplay ? 'queued-wire.txt' : `browser-${mode}-attachment.txt`;
const bytes = Buffer.from(queueReplay ? 'Synthetic queued HTTP bytes: Файл задачи' : 'Synthetic browser file: Приложение к задаче');
let browser;
let closingBrowser = false;
let routeDisposalsDuringClose = 0;
let vite;
let page;
let postCount = 0;
let taskCreateCount = 0;
let statusSaveCount = 0;
let savedId;
let uploadKey;
const recoveryKeys = [];
const errors = [];
const outside = [];
const inboxFrames = [];
const liveFrames = [];
const pendingRequests = new Map();
const recentResponses = [];
let separateIconRequests = 0;
let queueNoticeActions;
let queueCompletedAt;
let reloadStartedAt;
let rejectStatusRead = false;
let initialTaskData;
let delayedSettingsChecked = false;
let taskReadBoundary = 'initial';
let automationReadSequence = 0;
let automationReadDiagnosticCount = 0;
const automationReadDiagnostics = new Map();
const taskReadTrace = [];
const taskReadBoundaries = [];
const notificationEventTrace = [];
const completionTransfers = new Map();
let completionTransferSequence = 0;
const automationReadPaths = new Set([
  '/api/tasks/automation/case-candidates',
  '/api/tasks/automation/case-candidates/summary',
  '/api/tasks',
  '/api/tasks/summary',
  '/api/tasks/settings',
  '/api/profile',
  '/api/notifications/inbox',
  '/api/notifications/inbox/unread-count',
]);
function readTaskBrowserResourceTiming(request) {
  try {
    const timing = request.timing();
    const fields = ['requestStart', 'responseStart', 'responseEnd'];
    const values = fields.map(field => timing[field]);
    if (!values.every(value => typeof value === 'number' && Number.isFinite(value)
      && (value === -1 || (value >= 0 && value <= 3_600_000)))) return null;
    return Object.fromEntries(fields.map((field, index) => [field, values[index]]));
  } catch {
    // Missing diagnostic timing must not alter the original browser scenario.
    return null;
  }
}
function recordTaskReadBoundary(phase) {
  taskReadBoundary = phase;
  const entry = {
    phase, atUtc: new Date().toISOString(),
    pending: [...automationReadDiagnostics.entries()].map(([request, read]) => ({
      id: read.id, path: read.path, startedPhase: read.startedPhase, elapsedMs: Date.now() - read.started,
      resourceTiming: readTaskBrowserResourceTiming(request),
    })),
    pendingTransfers: [...completionTransfers.values()].map(transfer => ({
      method: transfer.method, path: transfer.path, startedPhase: transfer.startedPhase,
      elapsedMs: Date.now() - transfer.started, status: transfer.status ?? null, exactKeyRead: transfer.exactKeyRead,
    })),
  };
  if (taskReadBoundaries.length < 32) taskReadBoundaries.push(entry);
  if (taskReadTrace.length < 96) taskReadTrace.push({ boundary: entry });
  console.log('TASK_UPLOAD_READ_BOUNDARY ' + JSON.stringify(entry));
}
function recordAutomationReadEvent(event, read, aborted = false, request = null) {
  // Keep the existing budget for card/replay boundaries; filtered revision queues
  // need only one completed-response measurement, not three duplicate events.
  if (read.path === '/api/notifications/inbox' && read.filtered && event !== 'response') return;
  if (automationReadDiagnosticCount >= 72) return;
  automationReadDiagnosticCount++;
  const entry = {
    event, id: read.id, path: read.path, startedPhase: read.startedPhase,
    phase: taskReadBoundary, atUtc: new Date().toISOString(), elapsedMs: Date.now() - read.started, aborted,
    resourceTiming: request ? readTaskBrowserResourceTiming(request) : null,
    ...(read.path === '/api/notifications/inbox' ? {
      pageSize: read.pageSize, filtered: read.filtered, status: read.status ?? null,
      itemCount: read.itemCount ?? null, publicationNoticeCount: read.publicationNoticeCount ?? null,
    } : {}),
  };
  if (taskReadTrace.length < 96) taskReadTrace.push({ read: entry });
  console.log('TASK_UPLOAD_AUTOMATION_READ ' + JSON.stringify(entry));
}
function recordCompletionTransfer(event, transfer) {
  const entry = {
    event, id: transfer.id, method: transfer.method, path: transfer.path,
    startedPhase: transfer.startedPhase, phase: taskReadBoundary,
    atUtc: new Date().toISOString(), elapsedMs: Date.now() - transfer.started,
    status: transfer.status ?? null, exactKeyRead: transfer.exactKeyRead,
  };
  if (taskReadTrace.length < 96) taskReadTrace.push({ transfer: entry });
}
async function waitForCardReadsBeforeLeaving() {
  // A deliberate navigation is not part of the upload being exercised. Let its
  // actual reads, including the inbox refresh, finish before unmounting the
  // card or disposing the browser's request context.
  await expect.poll(() => [...pendingRequests.keys()].filter(request => {
    const url = new URL(request.url());
    return request.method() === 'GET' && url.origin === api.origin
      && (url.pathname === '/api/v1/cases' || url.pathname.startsWith('/api/tasks')
        || url.pathname === '/api/notifications/inbox' || url.pathname === '/api/notifications/inbox/unread-count');
  }).map(request => new URL(request.url()).pathname), {
    message: 'Actual task, case and inbox reads must finish before this test leaves the card.',
  }).toEqual([]);
}
async function waitForNotificationRefreshBeforeClosing() {
  // TopBar debounces realtime count refreshes by 800 ms. An empty request map
  // does not mean that this scheduled real GET has started or completed yet.
  // Check dispatch with the usual short poll. Let the dispatched read complete
  // within the page's response budget before closing the browser.
  await expect.poll(() => page.evaluate(() =>
    window.__taskUploadUnreadCountStartedRevision >= window.__taskUploadNotificationRevision), {
    message: 'The real unread-count refresh after the latest notification must start before browser close.',
  }).toBe(true);
  await page.waitForFunction(() =>
    window.__taskUploadUnreadCountRefreshRevision >= window.__taskUploadNotificationRevision, undefined, { polling: 100 });
}
async function waitForInitialTaskCardData(beforeCardReads) {
  const readResponse = async path => {
    const response = await page.waitForResponse(candidate => {
      const url = new URL(candidate.url());
      return url.origin === api.origin && url.pathname === path && candidate.request().method() === 'GET';
    });
    assert.equal(response.status(), 200, `Initial task card dependency failed: ${path}`);
    return response.json();
  };
  const readCardDependencies = async () => {
    if (beforeCardReads) await beforeCardReads;
    const [settings] = await Promise.all([
      readResponse('/api/tasks/settings'),
      readResponse(`/api/tasks/${taskId}/files`),
      readResponse(`/api/tasks/${taskId}/files/uploads`),
    ]);
    return settings;
  };
  initialTaskData = readResponse(`/api/tasks/${taskId}`);
  const [task, settings] = await Promise.all([initialTaskData, readCardDependencies()]);
  assert.equal(settings.taskCardWindowMode, mode, 'The actual backend must serve the seeded card mode.');
  assert.equal(task.id, taskId, 'The actual backend must serve the requested task.');
}
async function completeOwnedQueue() {
  const completion = new Promise((resolve, reject) => {
    let input = '';
    const timeout = setTimeout(() => { process.stdin.off('data', receive); reject(new Error('Owned queue did not complete.')); }, 30_000);
    const receive = chunk => {
      input += chunk.toString();
      if (!input.includes('\n')) return;
      clearTimeout(timeout);
      process.stdin.off('data', receive);
      process.stdin.destroy();
      if (input.trim() === 'QUEUE_COMPLETED') resolve();
      else reject(new Error('Unexpected owned queue command.'));
    };
    process.stdin.on('data', receive);
  });
  console.log('TASK_UPLOAD_QUEUE_READY');
  await completion;
  console.log('TASK_UPLOAD_STAGE_QUEUE_COMPLETED');
}
try {
  // Do not load the developer's .env files into this synthetic UI runtime.
  console.log('TASK_UPLOAD_STAGE_VITE_STARTING');
  const browserUi = await prepareSyntheticBrowserUi({ api, port });
  console.log('TASK_UPLOAD_STAGE_VITE_CREATED');
  vite = await browserUi.startPreview();
  assert.equal(vite.config.isProduction, true, 'The task browser must qualify production React behavior.');
  assert.equal(vite.config.env.DEV, false, 'The release browser must not use development-only mount effects.');
  for (const chunk of browserUi.chunks) {
    const code = await readFile(join(browserUi.outDir, chunk.fileName), 'utf8');
    assert.doesNotMatch(code, /\$Refresh(?:Reg|Sig)\$|\/@react-refresh|react-refresh\/runtime/,
      'The compiled task UI must not inject React Refresh globals.');
    assert.ok(chunk.moduleIds.every(id => !id.includes('/react-refresh/')),
      'The compiled task UI must not include the React Refresh runtime.');
  }
  assert.equal(vite.watcher, undefined, 'The immutable task preview must not create a file watcher.');
  console.log('TASK_UPLOAD_STAGE_VITE_LISTENING');
  // Qualify the complete production artifact before starting the browser clock.
  // Its cache retains the source, dependency and tool fingerprints.
  console.log('TASK_UPLOAD_STAGE_VITE_ENTRY_PREPARING');
  assert.ok(browserUi.chunks.some(chunk => chunk.isEntry && chunk.name === 'index' && chunk.codeLength > 0),
    'The synthetic task UI entry module must compile successfully.');
  assert.ok(browserUi.chunks.some(chunk => chunk.moduleIds.some(id => id.endsWith('/src/components/Tasks/TaskJournal.tsx'))),
    'The synthetic task route must compile successfully.');
  console.log('TASK_UPLOAD_STAGE_VITE_ENTRY_READY');
  // Use the Chromium version installed for this Playwright package first.
  for (const channel of [undefined, 'msedge', 'chrome']) {
    console.log(`TASK_UPLOAD_STAGE_BROWSER_STARTING_${channel ?? 'bundled'}`);
    try { browser = await chromium.launch({ headless: true, channel,
      ...(process.env.PAYPLANNER_TEST_BROWSER_PATH ? { executablePath: process.env.PAYPLANNER_TEST_BROWSER_PATH } : {}) }); break; }
    catch { console.log(`TASK_UPLOAD_STAGE_BROWSER_UNAVAILABLE_${channel ?? 'bundled'}`); /* Never download a browser. */ }
  }
  assert.ok(browser, 'An installed Chromium-compatible browser is required.');
  console.log('TASK_UPLOAD_STAGE_BROWSER_READY');
  console.log(`TASK_UPLOAD_STAGE_BROWSER_VERSION_${browser.version()}`);
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, timezoneId: 'Asia/Novosibirsk' });
  await context.addInitScript(installTaskNotificationRefreshObserver, api.origin);
  await context.addInitScript(() => {
    let observed = 0;
    // Same USER_NOTIFICATIONS_EVENT as the existing refresh observer; read only.
    window.addEventListener('payplanner:user-notifications-changed', event => {
      if (observed >= 24) return;
      const detail = event.detail ?? {};
      const source = ['inbox', 'realtime', 'system'].includes(detail.source) ? detail.source : null;
      const reason = ['created', 'fallback-poll', 'missed-replay', 'visibility-resume', 'reconnected']
        .includes(detail.reason) ? detail.reason : null;
      const objectType = typeof detail.objectType === 'string' ? detail.objectType.trim().toLowerCase() : null;
      console.log('TASK_UPLOAD_NOTIFICATION_EVENT ' + JSON.stringify({
        sequence: ++observed, atUtc: new Date().toISOString(), source, reason,
        objectType: ['task', 'case', 'client-case'].includes(objectType) ? objectType : null,
        hasTaskId: Number.isSafeInteger(detail.taskJournalItemId) && detail.taskJournalItemId > 0,
        hasCaseId: Number.isSafeInteger(detail.clientCaseId) && detail.clientCaseId > 0,
        taskNotificationType: typeof detail.notificationType === 'string' && detail.notificationType.startsWith('task'),
      }));
    });
  });
  if (completionFlow) {
    const completionUserId = Number(process.env.TASK_UPLOAD_COMPLETION_USER_ID);
    assert.ok(Number.isSafeInteger(completionUserId) && completionUserId > 0);
    // Only this fresh owned browser gets filters that keep its completed task visible.
    await context.addInitScript(({ origin, userId }) => {
      if (window.location.origin !== origin) return;
      window.localStorage.setItem(`pp.task_journal.filters.v1.user:${userId}`,
        JSON.stringify({ statusFilter: 'all', periodMode: 'all' }));
    }, { origin: ui, userId: completionUserId });
  }
  page = await context.newPage();
  page.setDefaultTimeout(25_000);
  page.on('request', request => {
    const url = new URL(request.url());
    const path = url.pathname;
    if (path.includes('/lucide-react/dist/esm/icons/')) separateIconRequests++;
    pendingRequests.set(request, { path, type: request.resourceType(), started: Date.now() });
    if (request.method() === 'GET' && new URL(request.url()).origin === api.origin
      && (automationReadPaths.has(path) || path === `/api/tasks/${taskId}`)) {
      const read = { id: ++automationReadSequence, path: path === `/api/tasks/${taskId}` ? '/api/tasks/:taskId' : path,
        startedPhase: taskReadBoundary, started: Date.now() };
      if (path === '/api/notifications/inbox') {
        const pageSize = Number(url.searchParams.get('pageSize'));
        read.pageSize = Number.isInteger(pageSize) && pageSize >= 1 && pageSize <= 100 ? pageSize : null;
        read.filtered = url.searchParams.has('type') || url.searchParams.has('requiresAction');
      }
      automationReadDiagnostics.set(request, read);
      recordAutomationReadEvent('started', read, false, request);
    }
    if (url.origin === api.origin
      && ((completionFlow && request.method() === 'POST' && path === `/api/tasks/${taskId}/files`)
        || (request.method() === 'GET' && path === `/api/tasks/${taskId}/files/uploads`))) {
      const transfer = { id: ++completionTransferSequence, method: request.method(),
        path: request.method() === 'POST' ? '/api/tasks/:taskId/files' : '/api/tasks/:taskId/files/uploads',
        startedPhase: taskReadBoundary, started: Date.now(), exactKeyRead: url.searchParams.has('uploadKeys') };
      completionTransfers.set(request, transfer);
      recordCompletionTransfer('started', transfer);
    }
  });
  page.on('requestfinished', request => {
    pendingRequests.delete(request);
    const read = automationReadDiagnostics.get(request);
    if (read) recordAutomationReadEvent('finished', read, false, request);
    automationReadDiagnostics.delete(request);
    const transfer = completionTransfers.get(request);
    if (transfer) recordCompletionTransfer('finished', transfer);
    completionTransfers.delete(request);
  });
  page.on('requestfailed', request => {
    pendingRequests.delete(request);
    const read = automationReadDiagnostics.get(request);
    if (read) recordAutomationReadEvent('failed', read, request.failure()?.errorText === 'net::ERR_ABORTED', request);
    automationReadDiagnostics.delete(request);
    const transfer = completionTransfers.get(request);
    if (transfer) recordCompletionTransfer('failed', transfer);
    completionTransfers.delete(request);
  });
  page.on('response', response => {
    const pending = pendingRequests.get(response.request());
    if (pending) pending.status = response.status();
    const transfer = completionTransfers.get(response.request());
    if (transfer) {
      transfer.status = response.status();
      recordCompletionTransfer('response', transfer);
    }
    recentResponses.push({ path: new URL(response.url()).pathname, status: response.status() });
    if (recentResponses.length > 20) recentResponses.shift();
  });
  page.on('websocket', socket => {
    if (new URL(socket.url()).pathname !== '/hubs/notifications') return;
    socket.on('framereceived', ({ payload }) => {
      for (const value of String(payload).split('\u001e').filter(Boolean)) {
        const frame = JSON.parse(value);
        if (frame.target === 'userNotificationCreated') liveFrames.push(frame.arguments[0]);
      }
    });
  });
  page.on('response', async response => {
    if (new URL(response.url()).pathname !== '/api/notifications/inbox') return;
    try {
      const started = pendingRequests.get(response.request())?.started;
      const query = new URL(response.url()).searchParams;
      const read = automationReadDiagnostics.get(response.request());
      const body = await response.json();
      // Observe existing reads only; filtered forced-revision queues are not toast replay.
      if (read) {
        read.status = response.status();
        read.itemCount = Array.isArray(body.items) ? Math.min(body.items.length, 100) : null;
        read.publicationNoticeCount = Array.isArray(body.items)
          ? body.items.filter(item => item.title === 'Файл прикреплён к задаче').length : null;
        recordAutomationReadEvent('response', read, false, response.request());
      }
      inboxFrames.push({ observedAt: new Date().toISOString(), startedAt: started ? new Date(started).toISOString() : null,
        statusFilter: query.get("status"), pageSize: query.get("pageSize"), status: response.status(), items: body.items?.map(item => ({
        title: item.title, createdAt: item.createdAt, toastEnabled: item.toastEnabled, isRead: item.isRead, taskId: item.taskId,
      })) });
      if (inboxFrames.length > 6) inboxFrames.shift();
    } catch { /* A page navigation may abort an already replaced response. */ }
  });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    const notificationPrefix = 'TASK_UPLOAD_NOTIFICATION_EVENT ';
    if (message.type() === 'log' && message.text().startsWith(notificationPrefix) && notificationEventTrace.length < 24) {
      try {
        const event = JSON.parse(message.text().slice(notificationPrefix.length));
        if (Number.isInteger(event.sequence) && event.sequence >= 1 && event.sequence <= 24
          && typeof event.atUtc === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(event.atUtc)
          && [null, 'inbox', 'realtime', 'system'].includes(event.source)
          && [null, 'created', 'fallback-poll', 'missed-replay', 'visibility-resume', 'reconnected'].includes(event.reason)
          && [null, 'task', 'case', 'client-case'].includes(event.objectType)
          && ['hasTaskId', 'hasCaseId', 'taskNotificationType'].every(key => typeof event[key] === 'boolean')) {
          notificationEventTrace.push({
            sequence: notificationEventTrace.length + 1, browserSequence: event.sequence,
            atUtc: event.atUtc, receivedAtUtc: new Date().toISOString(), phase: taskReadBoundary,
            source: event.source, reason: event.reason, objectType: event.objectType,
            hasTaskId: event.hasTaskId, hasCaseId: event.hasCaseId, taskNotificationType: event.taskNotificationType,
          });
        }
      } catch { /* Diagnostic parsing must not alter the original browser scenario. */ }
    }
    if (message.type() === 'log' && message.text() === 'TASK_UPLOAD_COMPLETION_NOTICE_OBSERVED') {
      recordTaskReadBoundary('completion-first-notice-observed');
    }
    // The sole synthetic 503 intentionally replaces an actual successful upload response.
    if (message.type() === 'error' && !(message.text().includes('503') && (message.location().url === `${api.origin}/api/tasks/${taskId}/files`
      || (completionFlow && message.location().url.startsWith(`${api.origin}/api/tasks/${taskId}/files/uploads?`))
      || (completionSaveFailure && message.location().url === `${api.origin}/api/tasks/${taskId}/status`))))
      errors.push(message.text());
  });
  await context.route('**/*', route => runTaskUploadRoute(async () => {
    const request = route.request();
    const url = new URL(request.url());
    if (![api.origin, ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
    if (delayCardSettings && !delayedSettingsChecked && url.pathname === '/api/tasks/settings' && request.method() === 'GET') {
      delayedSettingsChecked = true;
      try {
        const response = await route.fetch();
        assert.equal(response.status(), 200);
        assert.equal((await response.json()).taskCardWindowMode, mode);
        assert.ok(initialTaskData, 'Observe the real task request before holding its settings response.');
        await initialTaskData;
        const gate = page.getByTestId('task-card-settings-gate');
        await expect(gate).toBeVisible();
        await expect(page.getByTestId('task-legacy-card-window')).toHaveCount(0);
        await expect(page.getByTestId('task-modern-card-window')).toHaveCount(0);
        await mkdir('.playwright-cli', { recursive: true });
        const screenshotStem = `task-card-settings-${mode}-${basename(process.env.TASK_UPLOAD_ROOT)}`;
        await page.screenshot({ path: join('.playwright-cli', `${screenshotStem}-desktop.png`), fullPage: true, animations: 'disabled' });
        const viewport = page.viewportSize();
        await page.setViewportSize({ width: 390, height: 844 });
        await expect(gate).toBeVisible();
        await page.screenshot({ path: join('.playwright-cli', `${screenshotStem}-mobile.png`), fullPage: true, animations: 'disabled' });
        await page.setViewportSize(viewport);
        await expect(page.getByTestId('task-legacy-card-window')).toHaveCount(0);
        await expect(page.getByTestId('task-modern-card-window')).toHaveCount(0);
        console.log('TASK_UPLOAD_STAGE_SETTINGS_WAIT_VERIFIED');
        delayedSettingsRelease.resolve();
        return route.fulfill({ response });
      } catch (error) {
        delayedSettingsRelease.reject(error);
        throw error;
      }
    }
    if (completionSaveFailure && url.pathname === `/api/tasks/${taskId}/status` && request.method() === 'PATCH') {
      statusSaveCount++;
      if (statusSaveCount === 1)
        return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic status save unavailable' }) });
      assert.equal(statusSaveCount, 2);
    }
    if (creationFlow && url.pathname === '/api/tasks' && request.method() === 'POST') {
      taskCreateCount++;
      assert.equal(taskCreateCount, 1, 'Pending attachments must not create a second task.');
      const response = await route.fetch();
      assert.equal(response.status(), 201);
      taskId = (await response.json()).id;
      assert.ok(Number.isSafeInteger(taskId) && taskId > 0);
      return route.fulfill({ response });
    }
    if (url.pathname === `/api/tasks/${taskId}/files/uploads` && request.method() === 'GET') {
      recoveryKeys.push(...url.searchParams.getAll('uploadKeys'));
      if (rejectStatusRead && url.searchParams.has('uploadKeys')) {
        return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic status unavailable' }) });
      }
    }
    if (url.href === `${api.origin}/api/tasks/${taskId}/files` && request.method() === 'POST') {
      assert.ok(!queueReplay || completionFlow || creationFlow, 'Reopening an accepted queued upload must not retransmit its bytes.');
      postCount++;
      assert.equal(postCount, 1, 'Accepted bytes must not be retransmitted after the lost acknowledgement.');
      uploadKey = request.headers()['x-upload-key'];
      assert.match(uploadKey, /^[a-f0-9-]{36}$/i);
      const response = await route.fetch();
      if (completionFlow || creationFlow) {
        assert.equal(response.status(), 503, 'The actual backend must retain this pending completion upload.');
        return route.fulfill({ response });
      }
      assert.equal(response.status(), 201, 'The actual backend must publish this upload before losing its response.');
      const saved = await response.json();
      savedId = saved.id;
      assert.ok(savedId > 0 && saved.clientCaseDocumentId > 0);
      return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic lost acknowledgement' }) });
    }
    return route.continue();
  }, () => closingBrowser, () => { routeDisposalsDuringClose++; }));
  let initialPendingUploads;
  await page.goto(`${ui}/tasks/${taskId}`, { waitUntil: 'domcontentloaded' });
  await page.bringToFront();
  await page.locator('input[type="email"]').fill(process.env.TASK_UPLOAD_EMAIL);
  await page.locator('input[type="password"]').fill(process.env.TASK_UPLOAD_PASSWORD);
  const signIn = () => page.getByRole('button', { name: 'Войти', exact: true }).click();
  // Register the card reads when the login response arrives. The page still
  // needs its login body before it can send authenticated requests.
  const signedIn = page.waitForResponse(response =>
    response.url() === new URL('/api/auth/login', api).href && response.request().method() === 'POST'
  ).then(response => {
    assert.equal(response.status(), 200, 'The owned browser must sign in to the synthetic backend.');
    // The delayed-settings scenario holds the real response while it checks
    // two viewports. Attachment requests can only start after that inspection.
    const beforeCardReads = delayedSettingsRelease && Promise.all([
      page.waitForRequest(request => {
        const url = new URL(request.url());
        return request.method() === 'GET' && url.origin === api.origin && url.pathname === '/api/tasks/settings';
      }),
      delayedSettingsRelease.promise,
    ]);
    const readInitialPendingUploads = () => !completionFlow && !creationFlow ? page.waitForResponse(response => {
      const url = new URL(response.url());
      return response.request().method() === 'GET' && url.origin === api.origin
        && url.pathname === `/api/tasks/${taskId}/files/uploads`
        && url.searchParams.get('pendingOnly') === 'true' && url.searchParams.get('includePageInfo') === 'true';
    }).then(async response => {
      assert.equal(response.status(), 200, 'The initial pending upload page must load successfully.');
      const body = await response.json();
      assert.ok(Array.isArray(body.items));
      assert.ok(body.items.some(item => item.fileName === (queueReplay ? fileName : 'synthetic-paused.txt')
        && item.status === 'paused'), 'The real initial page must contain the seeded paused upload.');
    }) : null;
    initialPendingUploads = beforeCardReads ? beforeCardReads.then(readInitialPendingUploads) : readInitialPendingUploads();
    // Preserve an earlier primary failure if authentication or navigation fails first.
    void initialPendingUploads?.catch(() => {});
    return waitForInitialTaskCardData(beforeCardReads);
  });
  await Promise.all([signedIn, signIn()]);
  await page.locator('input[type="email"]').waitFor({ state: 'hidden' });
  console.log('TASK_UPLOAD_STAGE_AUTHENTICATED');
  const card = page.getByTestId(`task-${mode}-card-window`);
  await expect(card).toBeVisible();
  recordTaskReadBoundary('initial-card-visible');
  if (creationFlow) {
    await waitForCardReadsBeforeLeaving();
    await card.getByRole('button', { name: mode === 'modern' ? 'К журналу' : 'Журнал задач', exact: true }).click();
    await page.getByRole('button', { name: 'Добавить задачу', exact: true }).click();
    await page.getByPlaceholder('Выберите клиента или юридическое лицо', { exact: true }).fill('Synthetic wire client');
    await page.getByRole('option', { name: /Synthetic wire client/ }).click();
    await page.getByTestId('task-billing-wizard-case').selectOption({ label: 'Synthetic wire case' }, { force: true });
    await page.getByRole('button', { name: 'Неоплачиваемая задача', exact: true }).click();
    await expect(card.getByTestId('task-title-input')).toBeVisible();
    await card.getByTestId('task-title-input').fill('Synthetic creation pending task');
    await card.getByPlaceholder(mode === 'modern' ? 'Напишите комментарий...' : 'Введите первый комментарий', { exact: true })
      .fill('Synthetic creation: Первый комментарий.');
    await card.locator('input[type="file"]').first().setInputFiles({ name: fileName, mimeType: 'text/plain', buffer: bytes });
    await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname === `/api/tasks/${taskId}/files`
        && response.request().method() === 'POST' && response.status() === 503).then(async response => {
          assert.equal(await response.finished(), null, 'The pending upload acknowledgement must be fully received.');
        }),
      card.getByRole('button', { name: 'Создать задачу', exact: true }).click(),
    ]);
    await expect(page.getByTestId(`task-${mode}-card-window`).getByRole('region', { name: 'Состояние ваших загрузок', exact: true })).toBeVisible();
    await expect(page.getByText(/Задача создана. Файлы ещё ожидают прикрепления:/)).toBeVisible();
    assert.equal(taskCreateCount, 1);
    assert.equal(postCount, 1);
    await completeOwnedQueue();
    await waitForCardReadsBeforeLeaving();
    await Promise.all([
      waitForInitialTaskCardData(),
      page.goto(`${ui}/tasks/${taskId}`, { waitUntil: 'domcontentloaded' }).then(() => {
        recordTaskReadBoundary('creation-reopen-dom-ready');
      }),
    ]);
    await expect(card).toBeVisible();
    recordTaskReadBoundary('creation-reopen-card-visible');
    await expect(card.getByText('Synthetic creation: Первый комментарий.', { exact: true })).toBeVisible();
    assert.equal(taskCreateCount, 1);
    assert.equal(postCount, 1);
    console.log('TASK_UPLOAD_STAGE_CREATED_TASK');
  }
  if (completionFlow) {
    const waitForCompletionResponse = async (method, suffix, status, exactUploadKey = false) => {
      const response = await page.waitForResponse(candidate => {
        const url = new URL(candidate.url());
        return candidate.request().method() === method && url.origin === api.origin
          && url.pathname === `/api/tasks/${taskId}${suffix}`
          && (!exactUploadKey || url.searchParams.getAll('uploadKeys').includes(uploadKey));
      });
      assert.equal(response.status(), status, `The completion ${method} ${suffix} must return its expected status.`);
      assert.equal(await response.finished(), null, 'The completion response body must finish before its UI assertion.');
    };
    await card.getByRole('button', { name: 'Завершить', exact: true }).last().click();
    const result = page.locator('#task-completion-result');
    const finish = page.getByRole('button', { name: 'Завершить задачу', exact: true });
    await expect(result).toBeVisible();
    await result.fill('Synthetic completion: Работа выполнена.');
    await page.getByTestId('task-completion-file-input').setInputFiles({ name: fileName, mimeType: 'text/plain', buffer: bytes });
    await page.evaluate(() => {
      const panel = document.getElementById('task-completion-result')?.parentElement;
      if (!panel) throw new Error('Completion panel must exist before timing its notice.');
      const observer = new MutationObserver(() => {
        const notice = [...panel.querySelectorAll('p')].find(element =>
          element.textContent?.includes('Задача пока не завершена и не отправлена') && element.getClientRects().length > 0);
        if (!notice) return;
        observer.disconnect();
        console.log('TASK_UPLOAD_COMPLETION_NOTICE_OBSERVED');
      });
      observer.observe(panel, { subtree: true, childList: true, characterData: true });
    });
    recordTaskReadBoundary('completion-first-submit');
    await Promise.all([
      waitForCompletionResponse('POST', '/files', 503),
      finish.click().then(() => { recordTaskReadBoundary('completion-first-click-returned'); }),
    ]);
    await expect(page.getByText(/Задача пока не завершена и не отправлена/)).toBeVisible();
    assert.equal(postCount, 1);
    rejectStatusRead = true;
    recordTaskReadBoundary('completion-status-recheck');
    await Promise.all([
      waitForCompletionResponse('GET', '/files/uploads', 503, true),
      finish.click(),
    ]);
    await expect(page.getByText('Synthetic status unavailable', { exact: true })).toBeVisible();
    rejectStatusRead = false;
    await expect(result).toHaveValue('Synthetic completion: Работа выполнена.');
    await expect(page.getByTitle(fileName, { exact: true })).toBeVisible();
    assert.equal(postCount, 1, 'An unavailable status check must not resend the selected result file.');
    await completeOwnedQueue();
    recordTaskReadBoundary('completion-after-copy-submit');
    await Promise.all([
      waitForCompletionResponse('GET', '/files/uploads', 200, true),
      waitForCompletionResponse('GET', '/files', 200),
      waitForCompletionResponse('PATCH', '/status', completionSaveFailure ? 503 : 200),
      finish.click(),
    ]);
    if (completionSaveFailure) {
      await expect(page.getByText(/Не удалось подтвердить сохранение результата задачи/)).toBeVisible();
      await expect(result).toHaveValue('Synthetic completion: Работа выполнена.');
      await expect(page.getByTitle(fileName, { exact: true })).toBeVisible();
      assert.equal(statusSaveCount, 1);
      assert.equal(postCount, 1);
      recordTaskReadBoundary('completion-after-save-retry');
      await Promise.all([
        waitForCompletionResponse('GET', '/files', 200),
        waitForCompletionResponse('PATCH', '/status', 200),
        finish.click(),
      ]);
    }
    await expect(result).toHaveCount(0);
    if (completionSaveFailure) assert.equal(statusSaveCount, 2);
    assert.equal(postCount, 1);
    assert.ok(recoveryKeys.includes(uploadKey));
    // Use the journal's own navigation first: its cleanup cancels pending refresh
    // timers and retains already-started same-user reads until they finish.
    recordTaskReadBoundary('completion-journal');
    const completedTaskLink = page.getByTestId(`task-title-link-${taskId}`);
    await expect(completedTaskLink).toBeVisible();
    const precedingRequests = new Set(pendingRequests.keys());
    const pendingPage = page.waitForResponse(response => {
      const request = response.request();
      const url = new URL(response.url());
      return !precedingRequests.has(request) && request.method() === 'GET' && url.origin === api.origin
        && url.pathname === `/api/tasks/${taskId}/files/uploads`
        && url.searchParams.get('pendingOnly') === 'true' && url.searchParams.get('includePageInfo') === 'true';
    });
    recordTaskReadBoundary('completion-spa-reopen');
    const [pendingResponse] = await Promise.all([pendingPage, completedTaskLink.click()]);
    assert.equal(pendingResponse.status(), 200);
    assert.equal(await pendingResponse.finished(), null);
    const pendingBody = await pendingResponse.json();
    assert.deepEqual(pendingBody.items, [], 'The completed upload must not schedule card polling or publication callbacks.');
    assert.equal(pendingBody.nextCursor ?? null, null);
    await expect(card).toBeVisible();
    if (mode === 'modern') await card.getByRole('tab', { name: /^Файлы \d+$/ }).click();
    await expect(card.locator('article').filter({ hasText: fileName })
      .filter({ has: page.getByRole('button', { name: /Скачать/ }) })).toBeVisible();
    recordTaskReadBoundary('completion-spa-card-ready');
    // Keep the original hard reload and verification of its published attachment.
    recordTaskReadBoundary('completion-reopen');
    await waitForCardReadsBeforeLeaving();
    recordTaskReadBoundary('completion-reopen-after-drain');
    await Promise.all([
      waitForInitialTaskCardData(),
      page.goto(`${ui}/tasks/${taskId}`, { waitUntil: 'domcontentloaded' }).then(() => {
        recordTaskReadBoundary('completion-reopen-dom-ready');
      }),
    ]);
    await expect(card).toBeVisible();
    recordTaskReadBoundary('completion-reopen-card-visible');
    console.log('TASK_UPLOAD_STAGE_COMPLETED_TASK');
  }
  const uploads = card.getByRole('region', { name: 'Состояние ваших загрузок', exact: true });
  if (!completionFlow && !creationFlow) {
    await initialPendingUploads;
    await expect(uploads).toBeVisible();
    await expect(uploads.getByText(queueReplay ? fileName : 'synthetic-paused.txt', { exact: true })).toBeVisible();
    await expect(uploads.getByText('Приостановлено', { exact: true })).toBeVisible();
    await expect(uploads).toContainText('отправлять файл заново не нужно');
    console.log('TASK_UPLOAD_STAGE_PENDING_CARD');
    if (queueReplay) {
      // Use the real back button: close the card without reloading the entire application.
      await waitForCardReadsBeforeLeaving();
      recordTaskReadBoundary('before-journal-back');
      await card.getByRole('button', { name: mode === 'modern' ? 'К журналу' : 'Журнал задач', exact: true }).click();
      recordTaskReadBoundary('after-journal-back');
      await expect(card).toHaveCount(0);
      await completeOwnedQueue();
      queueCompletedAt = new Date().toISOString();
      reloadStartedAt = new Date().toISOString();
      // Live flow must deliver through the actual hub without a reload or fabricated event.
      if (!liveFlow) {
        // This deliberate reload is outside the file publication being exercised.
        await waitForCardReadsBeforeLeaving();
        recordTaskReadBoundary('before-replay-reload');
        await Promise.all([
          page.waitForResponse(async response => {
            const url = new URL(response.url());
            if (response.request().method() !== 'GET' || url.origin !== api.origin
              || url.pathname !== '/api/notifications/inbox' || url.searchParams.get('pageSize') !== '50'
              || url.searchParams.get('status') !== 'unread' || response.status() !== 200) return false;
            const body = await response.json();
            return Array.isArray(body.items) && body.items.some(item =>
              item.title === 'Файл прикреплён к задаче' && item.taskId === taskId);
          }),
          page.reload({ waitUntil: 'domcontentloaded' }).then(() => {
            recordTaskReadBoundary('after-replay-reload');
          }),
        ]);
      }
      const notice = page.getByTestId('toast-card').filter({ hasText: 'Файл прикреплён к задаче' });
      await expect(notice).toHaveCount(1);
      recordTaskReadBoundary('replay-notice-visible');
      if (liveFlow) {
        assert.equal(liveFrames.length, 1);
        assert.equal(liveFrames[0].taskJournalItemId, taskId);
        assert.equal(liveFrames[0].title, 'Файл прикреплён к задаче');
        assert.equal(JSON.parse(liveFrames[0].metadataJson).result, 'uploaded');
      }
      console.log('TASK_UPLOAD_STAGE_NOTICE_VISIBLE');
      await expect(card).toHaveCount(0);
      queueNoticeActions = await notice.getByRole('button').allTextContents();
      recordTaskReadBoundary('before-toast-open-click');
      await Promise.all([
        page.waitForResponse(response => new URL(response.url()).pathname === `/api/tasks/${taskId}/files`
          && response.request().method() === 'GET').then(async response => {
          assert.equal(response.status(), 200);
          const files = await response.json();
          assert.ok(Array.isArray(files) && files.some(file => file.originalFileName === fileName),
            'Reopening the published task must return its actual file before checking the rendered list.');
        }),
        page.waitForResponse(response => /\/api\/notifications\/inbox\/\d+\/read$/.test(new URL(response.url()).pathname)
          && response.request().method() === 'PATCH' && response.status() === 200),
        notice.getByRole('button', { name: 'Открыть задачу', exact: true }).click()
          .then(() => recordTaskReadBoundary('after-toast-open-click')),
      ]);
      await expect(card).toBeVisible();
      console.log('TASK_UPLOAD_STAGE_RESULT_CARD');
      assert.equal(postCount, 0);
    }
  }
  if (mode === 'modern') await card.getByRole('tab', { name: /^Файлы \d+$/ }).click();
  // No edit/status action: use the ordinary working upload button in the view card.
  if (!queueReplay) {
    // Rendering starts after the actual lost acknowledgement has been recovered, not when
    // the file chooser dispatches its change event while the real upload is still in flight.
    const recoveredUpload = page.waitForResponse(async response => {
      if (!uploadKey || new URL(response.url()).pathname !== `/api/tasks/${taskId}/files/uploads`
        || response.request().method() !== 'GET' || response.status() !== 200) return false;
      const states = await response.json();
      return Array.isArray(states) && states.some(state => state.uploadKey === uploadKey && state.status === 'uploaded');
    });
    const publishedFiles = page.waitForResponse(async response => {
      if (!savedId || new URL(response.url()).pathname !== `/api/tasks/${taskId}/files`
        || response.request().method() !== 'GET' || response.status() !== 200) return false;
      const files = await response.json();
      return Array.isArray(files) && files.some(file => file.id === savedId && file.originalFileName === fileName);
    });
    const uploadButton = (mode === 'modern' ? card.locator('main') : card)
      .getByRole('button', { name: 'Добавить файл', exact: true });
    const [chooser] = await Promise.all([page.waitForEvent('filechooser'), uploadButton.click()]);
    await Promise.all([recoveredUpload, publishedFiles, chooser.setFiles({ name: fileName, mimeType: 'text/plain', buffer: bytes })]);
  }
  const article = card.locator('article').filter({ hasText: fileName })
    .filter({ has: page.getByRole('button', { name: /Скачать/ }) });
  await expect(article).toBeVisible();
  if (!queueReplay) {
    assert.equal(postCount, 1);
    assert.ok(savedId > 0 && uploadKey);
    assert.ok(recoveryKeys.includes(uploadKey), 'The UI must recover the exact accepted upload key from the real status endpoint.');
  }
  const [download] = await Promise.all([
    page.waitForEvent('download'), article.getByRole('button', { name: /Скачать/ }).click(),
  ]);
  assert.equal(download.suggestedFilename(), fileName);
  assert.equal(await download.failure(), null);
  const stream = await download.createReadStream();
  assert.ok(stream);
  const chunks = [];
  for await (const chunk of stream) chunks.push(chunk);
  assert.deepEqual(Buffer.concat(chunks), bytes);
  await expect(page.getByText('Ошибка загрузки файла', { exact: true })).toHaveCount(0);
  assert.deepEqual(outside, [], 'The synthetic UI attempted an outside request.');
  assert.deepEqual(errors, [], 'The synthetic UI reported unexpected browser errors.');
  assert.equal(separateIconRequests, 0, 'The UI must not load the full icon export graph as separate browser requests.');
  if (delayCardSettings) assert.equal(delayedSettingsChecked, true, 'The actual settings response must have been held and checked.');
  recordTaskReadBoundary('before-teardown');
  await waitForNotificationRefreshBeforeClosing();
  await waitForCardReadsBeforeLeaving();
  recordTaskReadBoundary('teardown-after-drain');
  console.log('TASK_UPLOAD_BROWSER_OK');
} catch (error) {
  recordTaskReadBoundary('browser-scenario-failed');
  console.error(JSON.stringify({ cardMode: mode, postCount, browserErrors: errors, outsideOrigins: outside,
    cardFlags: page ? await page.evaluate(() => ['legacy', 'modern'].map(cardMode => {
      const element = document.querySelector(`[data-testid="task-${cardMode}-card-window"]`);
      const rectangle = element?.getBoundingClientRect();
      const style = element ? getComputedStyle(element) : null;
      return { mode: cardMode, present: element !== null, hasBox: Boolean(rectangle?.width && rectangle?.height),
        displayNone: style?.display === 'none', visibilityHidden: style?.visibility === 'hidden' };
    })) : [],
    separateIconRequests, pendingRequests: [...pendingRequests.values()].slice(0, 20).map(request => ({ path: request.path,
      type: request.type, elapsedMs: Date.now() - request.started })), recentResponses,
    inboxFrames, queueNoticeActions, queueCompletedAt, reloadStartedAt, clock: page ? await page.evaluate(() => ({ now: new Date().toISOString(),
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone, offset: new Date().getTimezoneOffset(),
      replayFrontiers: Object.keys(localStorage).filter(key => key.startsWith("payplanner.realtimeToastLastSeenAt.v1.")).map(key => ({ key, value: localStorage.getItem(key) })) })) : null,
    visibleText: page ? (await page.locator('body').innerText()).slice(0, 4000) : '' }));
  throw error;
} finally {
  closingBrowser = true;
  recordTaskReadBoundary('browser-close');
  if (browser) await browser.close();
  recordTaskReadBoundary('browser-closed');
  if (vite) await vite.close();
  await mkdir('.playwright-cli', { recursive: true });
  await writeFile(join('.playwright-cli', `task-read-boundaries-${basename(process.env.TASK_UPLOAD_ROOT)}.json`),
    JSON.stringify({ cardMode: mode, flow: process.env.TASK_UPLOAD_FLOW ?? 'replay', routeDisposalsDuringClose,
      trace: taskReadTrace, boundaries: taskReadBoundaries, notificationEvents: notificationEventTrace }, null, 2),
    { flag: 'wx' });
}
