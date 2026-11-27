import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { createServer as createControlServer } from 'node:http';
import { build, preview } from 'vite';
import { prepareSyntheticBrowserUi } from './synthetic-browser-ui.mjs';
import { completeCaseUploadResponse, createCaseReadLifecycle, createCaseRetirementProofTrace, createCaseRestartObserver, createCaseStartupReadTrace, isCaseBoundaryRead, observeCaseRetirementOperation, waitForCaseRestartResolution } from './case-retirement-browser-lifecycle.mjs';
import { openRetirementProductionBundle, retirementBundlePhase, withRetirementBuildEnvironment } from './case-retirement-production-bundle.mjs';

const api = new URL(process.env.CASE_RETIREMENT_API);
assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.protocol, 'http:');
const root = process.env.CASE_RETIREMENT_ROOT;
assert.match(basename(root), /^pp_task_upload_browser_[a-f0-9]{32}$/);
const caseId = Number(process.env.CASE_RETIREMENT_ID);
const port = Number(process.env.CASE_RETIREMENT_PORT);
assert.ok(Number.isSafeInteger(caseId) && caseId > 0);
assert.ok(Number.isSafeInteger(port) && port > 0 && port < 65536);
const key = process.env.CASE_RETIREMENT_KEY;
assert.match(key, /^[a-f0-9-]{36}$/i);
const flow = process.env.CASE_RETIREMENT_FLOW ?? 'documents';
assert.ok(['documents', 'edit', 'published-edit', 'modal', 'published-modal', 'writ', 'modal-writ', 'version', 'modal-version', 'revoked-edit', 'revoked-modal', 'restart-edit', 'restart-modal', 'restart-slow-edit', 'restart-slow-modal'].includes(flow));
if (flow.startsWith('restart-')) assert.equal(process.env.CASE_RETIREMENT_LOST_ACK, 'true');
const clientId = Number(process.env.CASE_RETIREMENT_CLIENT_ID ?? 0);
if (flow.includes('modal')) assert.ok(Number.isSafeInteger(clientId) && clientId > 0);
const sourceDocumentId = Number(process.env.CASE_RETIREMENT_SOURCE_DOCUMENT_ID ?? 0);
const documentKey = process.env.CASE_RETIREMENT_DOCUMENT_KEY ?? '';
if (flow.includes('version')) {
  assert.ok(Number.isSafeInteger(sourceDocumentId) && sourceDocumentId > 0);
  assert.ok(documentKey.length > 0);
}
// Use the already installed CLI; never download packages or browsers in this gate.
let cliPath = process.env.CASE_RETIREMENT_CLI;
if (cliPath) {
  assert.ok(isAbsolute(cliPath), 'The configured Playwright CLI path must be absolute.');
} else if (process.platform === 'win32' && process.env.LOCALAPPDATA) {
  const cache = join(process.env.LOCALAPPDATA, 'npm-cache', '_npx');
  for (const entry of (await readdir(cache)).sort()) {
    const packageDir = join(cache, entry, 'node_modules', '@playwright', 'cli');
    try {
      const metadata = JSON.parse(await readFile(join(packageDir, 'package.json'), 'utf8'));
      if (metadata.name === '@playwright/cli' && metadata.version === '0.1.22') {
        cliPath = join(packageDir, metadata.bin['playwright-cli']); break;
      }
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
}
assert.ok(cliPath, 'Provide PAYPLANNER_TEST_PLAYWRIGHT_CLI pointing to an installed Playwright CLI.');
const cliMetadata = JSON.parse(await readFile(join(dirname(cliPath), 'package.json'), 'utf8'));
assert.equal(cliMetadata.name, '@playwright/cli');
assert.equal(cliMetadata.version, '0.1.22', 'Use the CLI version pinned by the browser gate.');
assert.equal(resolve(cliPath), resolve(dirname(cliPath), cliMetadata.bin['playwright-cli']));
const browser = process.env.CASE_RETIREMENT_BROWSER ?? (process.platform === 'win32' ? 'msedge' : 'chrome');
assert.ok(['msedge', 'chrome'].includes(browser), 'Use an installed Edge or Chrome channel.');
const session = `case-retire-${basename(root).slice(-12)}`;
const scenarioPath = join(root, 'case-retirement-scenario.js');
const execute = promisify(execFile);
async function cli(...args) {
  const result = await execute(process.execPath, [cliPath, `-s=${session}`, ...args], {
    timeout: 110_000, maxBuffer: 1024 * 1024, windowsHide: true,
  });
  assert.ok(!result.stdout.includes('### Error'), result.stdout + result.stderr);
  return result.stdout;
}
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
process.env.VITE_API_URL = new URL('/api', api).href;
const ui = `http://127.0.0.1:${port}`;
let vite;
let opened = false;
let control;
let controlUrl;
let bundleLease;
const phase = (name, action) => bundleLease ? retirementBundlePhase(name, action) : action();
try {
  if (process.env.CASE_RETIREMENT_BOUNDARY === 'true') {
    let applied = false;
    control = createControlServer(async (request, response) => {
      if (request.method !== 'POST' || request.url !== '/apply' || applied) {
        response.writeHead(404).end(); return;
      }
      applied = true;
      try {
        await new Promise((resolve, reject) => {
          let input = '';
          const timer = setTimeout(() => finish(new Error('Owned boundary acknowledgement timed out')), 45000);
          const receive = chunk => {
            input += chunk.toString();
            if (input.includes('\n')) finish(input.trim() === 'BOUNDARY_APPLIED' ? null : new Error('Invalid owned boundary acknowledgement'));
          };
          const finish = error => {
            clearTimeout(timer); process.stdin.off('data', receive); process.stdin.pause();
            if (error) reject(error); else resolve();
          };
          process.stdin.on('data', receive);
          console.log('CASE_RETIREMENT_BOUNDARY_READY');
        });
        response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}');
      } catch (error) { response.writeHead(500).end(error.message); }
    });
    await new Promise((resolve, reject) => {
      control.once('error', reject); control.listen(0, '127.0.0.1', resolve);
    });
    controlUrl = `http://127.0.0.1:${control.address().port}/apply`;
  }
  // Both paths use the real production bundle and upload/auth fixture entry.
  let fixtureChunk, realtimeDisconnectUrls, realtimePollingUrls;
  if (process.env.CASE_RETIREMENT_BUNDLE_ROOT) {
    bundleLease = await openRetirementProductionBundle({ root: process.env.CASE_RETIREMENT_BUNDLE_ROOT,
      owner: process.env.CASE_RETIREMENT_BUNDLE_OWNER, api, build });
    fixtureChunk = { fileName: bundleLease.fixtureFile };
    realtimeDisconnectUrls = bundleLease.disconnectFiles.map(file => ui + '/' + file);
    realtimePollingUrls = bundleLease.pollingFiles.map(file => ui + '/' + file);
    const startPreview = () => preview({ ...bundleLease.config, preview: { host: '127.0.0.1', port, strictPort: true } });
    vite = await phase('preview-listen', () => withRetirementBuildEnvironment(startPreview));
  } else {
    const browserUi = await prepareSyntheticBrowserUi({ api, port });
    const { chunks } = browserUi;
    assert.ok(chunks.some(chunk => chunk.isEntry && chunk.name === 'index' && chunk.codeLength > 0),
      'The real case-document UI entry must compile successfully.');
    const fixtureChunks = chunks.filter(chunk => chunk.isEntry && chunk.name === 'case-retirement-fixture');
    assert.equal(fixtureChunks.length, 1, 'The real upload helper must have one compiled fixture entry.');
    fixtureChunk = fixtureChunks[0];
    assert.ok(['beginCaseDocumentUpload', 'getAuthToken'].every(name => fixtureChunk.exports.includes(name)),
      'The compiled fixture must preserve the real upload and authentication helpers.');
    const moduleUrls = source => chunks.filter(chunk => chunk.moduleIds.some(id => id.replaceAll('\\', '/').endsWith(source)))
      .map(chunk => ui + '/' + chunk.fileName);
    realtimeDisconnectUrls = moduleUrls('/src/services/realtimeNotifications.ts');
    realtimePollingUrls = moduleUrls('/src/components/Notifications/RealtimeNotificationToasts.tsx');
    assert.ok(realtimeDisconnectUrls.length > 0 && realtimePollingUrls.length > 0,
      'The release bundle must retain the real notification modules.');
    vite = await browserUi.startPreview();
  }
  // Own the browser before navigation so a failed page load still closes it.
  console.log('PP_CASE_RETIREMENT_BROWSER_PHASE cli-open');
  await phase('browser-open', () => cli('open', 'about:blank', '--browser', browser));
  opened = true;
  await phase('browser-goto', () => cli('goto', ui + '/cases/' + caseId));
  await phase('browser-snapshot', () => cli('snapshot'));
  const settings = JSON.stringify({ api: api.origin, ui, caseId, key, flow, clientId, sourceDocumentId, documentKey, controlUrl,
    fixtureModule: ui + '/' + fixtureChunk.fileName, realtimeDisconnectUrls, realtimePollingUrls,
    pending: flow.includes('writ') ? { fileName: 'retirement-browser-owned.pdf', contentType: 'application/pdf',
      documentType: 'Исполнительный лист', status: 'Получен', bytes: '%PDF-1.4\nOwned browser writ\n%%EOF' }
      : { fileName: 'retirement-browser-owned.bin', contentType: 'application/octet-stream', documentType: 'Документ',
        status: 'Подготовлен', bytes: 'Owned browser retirement bytes' },
    email: process.env.CASE_RETIREMENT_EMAIL, password: process.env.CASE_RETIREMENT_PASSWORD,
    lostAck: process.env.CASE_RETIREMENT_LOST_ACK === 'true' });
  const scenario = String.raw`async (page) => {
    const settings = ${settings};
    const check = (condition, message) => { if (!condition) throw new Error(message); };
    const startupReads = (${createCaseStartupReadTrace.toString()})(page, settings.api, settings.caseId);
    const waitAtPhase = async (locator, phase, options) => {
      try { await locator.waitFor(options); }
      catch (error) {
        // Retain the original timeout, error type and cause; add only a fixed phase name.
        if (error && typeof error.message === 'string') {
          error.message = 'CASE_RETIREMENT_WAIT_PHASE ' + phase + ': ' + error.message
            + '\nCASE_RETIREMENT_STARTUP_READS ' + JSON.stringify(startupReads.snapshot());
          if (typeof error.stack === 'string')
            error.stack = 'CASE_RETIREMENT_WAIT_PHASE ' + phase + '\n' + error.stack;
        }
        throw error;
      }
    };
    const outside = []; const errors = []; const fileKeys = []; const reads = [];
    const readTrace = [];
    const tracedReads = new WeakMap();
    let tracePhase = 'login';
    let traceSequence = 0;
    const traceStarted = Date.now();
    const isSidebarAdvanceCount = request => {
      const url = new URL(request.url());
      return request.method() === 'GET' && url.origin === settings.api
        && url.pathname === '/api/v2/payments' && url.searchParams.get('advanceReviewStatus') === 'RequiresDecision';
    };
    const isClientFormRequirements = request => {
      const url = new URL(request.url());
      return request.method() === 'GET' && url.origin === settings.api
        && url.pathname === '/api/form-requirements' && url.searchParams.get('section') === 'clients';
    };
    const traceRead = (event, request) => {
      const url = new URL(request.url());
      if (url.origin !== settings.api || request.method() !== 'GET') return;
      const kind = url.pathname === '/api/v2/cases/' + settings.caseId + '/documents/uploads/pending' ? 'pending-uploads'
        : url.pathname === '/api/v2/cases/' + settings.caseId + '/documents/uploads' ? 'upload-receipts'
          : url.pathname === '/api/v2/cases/' + settings.caseId + '/court-recoveries' ? 'court-recoveries'
        : isClientFormRequirements(request) ? 'client-form-requirements' : null;
      if (!kind) return;
      if (event === 'start') tracedReads.set(request, ++traceSequence);
      if (readTrace.length < 64) readTrace.push({ event, kind, read: tracedReads.get(request), phase: tracePhase,
        elapsedMs: Date.now() - traceStarted,
        aborted: event === 'failed' && request.failure()?.errorText === 'net::ERR_ABORTED' });
    };
    page.on('request', request => traceRead('start', request));
    page.on('requestfinished', request => traceRead('finished', request));
    page.on('requestfailed', request => traceRead('failed', request));
    let retirePosts = 0; let filePosts = 0; let caseSaves = 0; let documentReads = 0; let publisherPosts = 0;
    let caseDetailReads = 0; let caseDetailReadsBeforeRestart = 0;
    let uploadInputState = null;
    const uploadResponseAtPhase = (phase, predicate) => page.waitForResponse(predicate).catch(error => {
      if (error && typeof error.message === 'string') {
        const diagnostic = JSON.stringify({ phase, filePosts, caseSaves, uploadInputState,
          reads: caseReads.snapshot(), startupReads: startupReads.snapshot() });
        error.message = 'CASE_RETIREMENT_UPLOAD_WAIT ' + diagnostic + '\n' + error.message;
        if (typeof error.stack === 'string') error.stack = 'CASE_RETIREMENT_UPLOAD_WAIT ' + diagnostic + '\n' + error.stack;
      }
      throw error;
    });
    const editing = settings.flow !== 'documents' && settings.flow !== 'version';
    const versionAttempt = settings.flow.includes('version');
    const publishedWinner = settings.flow.startsWith('published-');
    const revoked = settings.flow.startsWith('revoked-');
    const restarted = settings.flow.startsWith('restart-');
    let restarting = false;
    let restartApplied = false;
    let realtimeInterruptions = 0;
    let proofRefusals = 0;
    let manualRecovery = false;
    let acknowledgedRestartReceipt = null;
    let retirementOperationOutcome = null;
    let finishRestart;
    const restartFinished = new Promise(resolve => { finishRestart = resolve; });
    let notifyInitialConnection;
    let notifyReconnect;
    let restartReadBoundary;
    let rightsApplied = false;
    const modal = settings.flow.includes('modal');
    const openDocumentActions = async fileName => {
      if (modal || editing) return;
      const button = page.getByRole('button', { name: 'Сведения и действия документа ' + fileName, exact: true });
      if (await button.getAttribute('aria-expanded') !== 'true') await button.click();
      check(await button.getAttribute('aria-expanded') === 'true', 'Document actions must be expanded before accessing its version input');
    };
    const caseReads = (${createCaseReadLifecycle})(page,
      request => (${isCaseBoundaryRead})(request, settings.api, settings.caseId), 20000);
    const proofTrace = (${createCaseRetirementProofTrace})(page, settings.api, settings.caseId, settings.key);
    page.setDefaultTimeout(20000);
    page.on('pageerror', error => errors.push(error.message));
    const createRestartObserver = (${createCaseRestartObserver});
    let restartObserver = createRestartObserver(settings.api, () => notifyReconnect?.());
    page.on('websocket', socket => {
      if (!restartObserver.observeSocket(socket)) return;
      socket.on('framereceived', frame => {
        restartObserver.onHandshake(socket, frame.payload.toString());
        if (restartObserver.snapshot().initialReady) notifyInitialConnection?.();
      });
      socket.on('close', () => restartObserver.onClose(socket));
    });
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const expectedLoss = settings.lostAck && message.text().includes('503')
        && message.location().url === settings.api + '/api/v2/cases/' + settings.caseId + '/documents/uploads/' + settings.key + '/retire';
      const expectedDenial = revoked && rightsApplied && message.text().includes('403')
        && [settings.api + '/api/v2/cases/' + settings.caseId + '/documents/uploads',
          settings.api + '/api/v2/cases/' + settings.caseId + '/documents/uploads/pending',
          settings.api + '/api/v2/cases/' + settings.caseId + '/documents/uploads/' + settings.key + '/retire']
          .includes(message.location().url.split('?')[0]);
      const expectedRestartDisconnect = restarted && restarting
        && settings.realtimeDisconnectUrls.includes(message.location().url)
        && message.text().startsWith('Connection disconnected with error');
      const expectedRestartRefusal = restarted && restarting
        && message.text() === 'Failed to load resource: net::ERR_CONNECTION_REFUSED'
        && [settings.api + '/hubs/notifications/negotiate', settings.api + '/api/notifications/inbox']
          .includes(message.location().url.split('?')[0]);
      const expectedRestartPolling = restarted && restarting
        && settings.realtimePollingUrls.includes(message.location().url)
        && message.text().startsWith('Fallback notifications polling failed. TypeError: Failed to fetch');
      const expectedUnavailableProof = restarted && restarting
        && message.text() === 'Failed to load resource: net::ERR_CONNECTION_REFUSED'
        && [settings.api + '/api/v2/cases/' + settings.caseId + '/documents/uploads?uploadKeys=' + settings.key,
          settings.api + '/api/v2/cases/' + settings.caseId + '/documents/uploads/pending'].includes(message.location().url);
      if (expectedUnavailableProof) proofRefusals++;
      if (expectedRestartDisconnect || expectedRestartRefusal || expectedRestartPolling) realtimeInterruptions++;
      if (!expectedLoss && !expectedDenial && !expectedRestartDisconnect && !expectedRestartRefusal && !expectedRestartPolling && !expectedUnavailableProof) errors.push(message.text());
    });
    await page.context().route('**/*', async route => {
      const request = route.request(); const url = new URL(request.url());
      if (![settings.api, settings.ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
      // The real sidebar refresh is unrelated to the intentional outage.
      // Forward it unchanged after the owned server is back.
      if (restarting && settings.flow.includes('slow') && isSidebarAdvanceCount(request)) await restartFinished;
      if (restarted && !settings.flow.includes('slow') && restartObserver.snapshot().begun
        && request.method() === 'POST' && url.origin === settings.api && url.pathname === '/hubs/notifications/negotiate') {
        if (!restartReadBoundary) restartReadBoundary = (async () => {
          try {
            tracePhase = 'restart-operation-before-reconnect';
            await waitAtPhase({ waitFor: () => page.waitForFunction(() => {
              const operation = window.__payplannerOwnedRetirement?.snapshot();
              return operation?.attempt === 1 && ['fulfilled', 'rejected'].includes(operation.phase);
            }) }, 'restart-operation-before-reconnect');
            tracePhase = 'restart-reads-before-reconnect';
            await caseReads.drain('server-restart');
          } finally { tracePhase = 'retirement-confirmation'; }
        })();
        await restartReadBoundary;
        return route.continue();
      }
      if ((url.pathname === '/api/v2/cases/' + settings.caseId || url.pathname === '/api/v2/cases')
        && ['POST', 'PUT', 'PATCH'].includes(request.method())) caseSaves++;
      if (url.pathname === '/api/v2/cases/' + settings.caseId + '/documents' && request.method() === 'GET') documentReads++;
      if (url.pathname === '/api/v2/cases/' + settings.caseId && request.method() === 'GET') caseDetailReads++;
      if (url.pathname.endsWith('/retire') && request.method() === 'POST') {
        retirePosts++; check(retirePosts === (manualRecovery ? 2 : 1), 'Retirement was repeated without a separate confirmation');
        if (revoked) {
          const boundary = await page.request.post(settings.controlUrl);
          check(boundary.status() === 200, 'Owned rights boundary failed: ' + await boundary.text());
          rightsApplied = true;
          const denied = await route.fetch();
          check(denied.status() === 403, 'Fresh database rights did not deny retirement');
          return route.fulfill({ response: denied });
        }
        if (publishedWinner) {
          // Race a real publisher after the user confirms, before the retirement reaches the server.
          publisherPosts++;
          const published = await page.request.post(settings.api + '/api/v2/cases/' + settings.caseId + '/documents', {
            headers: { authorization: request.headers()['authorization'], 'X-Upload-Key': settings.key },
            multipart: { file: { name: settings.pending.fileName, mimeType: settings.pending.contentType, buffer: Buffer.from(settings.pending.bytes) },
              documentType: settings.pending.documentType, status: settings.pending.status },
          });
          check(published.status() === 201, 'Racing actual publisher failed: ' + await published.text());
        }
        const response = await route.fetch(); check(response.status() === 200, 'Actual retirement failed');
        check((await response.json()).status === (publishedWinner ? 'uploaded' : 'retired'), 'Actual server returned the wrong terminal proof');
        if (restarted && retirePosts === 1) {
          // Initial connection and existing reads share the same 20-second
          // readiness deadline; only then can this fixture stop its server.
          try {
            await caseReads.drain('server-restart', async () => {
              if (restartObserver.snapshot().initialReady) return;
              await new Promise(resolveInitial => { notifyInitialConnection = resolveInitial; });
            });
          } finally { notifyInitialConnection = undefined; }
          caseDetailReadsBeforeRestart = caseDetailReads;
          restartObserver.beginRestart();
          restarting = true;
          const boundary = await page.request.post(settings.controlUrl);
          check(boundary.status() === 200, 'Owned server restart failed: ' + await boundary.text());
          restartApplied = true;
          restartObserver.acknowledgeRestart();
          restarting = false;
          finishRestart();
        }
        if (settings.lostAck && retirePosts === 1) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Owned lost retirement acknowledgement' }) });
        return route.fulfill({ response });
      }
      if (url.pathname.includes('/documents/uploads') && request.method() === 'GET') reads.push(url.searchParams.getAll('uploadKeys'));
      if (request.method() === 'POST' && (url.pathname === '/api/v2/cases/' + settings.caseId + '/documents' || /\/api\/case-documents\/\d+\/versions$/.test(url.pathname))) {
        filePosts++; fileKeys.push(request.headers()['x-upload-key']);
        return (${completeCaseUploadResponse.toString()})(route, caseReads);
      }
      return route.continue();
    });
    await page.bringToFront();
    const loginEmail = page.getByLabel('Email', { exact: true });
    await loginEmail.fill(settings.email);
    await page.locator('input[type="password"]').fill(settings.password);
    const initialCaseRead = page.waitForResponse(response => response.request().method() === 'POST'
      && response.url() === settings.api + '/api/auth/login').then(response => {
      check(response.status() === 200, 'The owned browser login failed.');
      return page.waitForRequest(request => {
        const url = new URL(request.url());
        return request.method() === 'GET' && url.origin === settings.api
          && url.pathname === '/api/v2/cases/' + settings.caseId;
      });
    });
    const [caseRequest] = await Promise.all([initialCaseRead,
      page.getByRole('button', { name: 'Войти', exact: true }).click()]);
    // The case readiness timeout starts only once the real case request is in flight.
    await Promise.all([
      waitAtPhase(loginEmail, 'login-finished', { state: 'hidden' }),
      waitAtPhase(page.getByRole('button', { name: 'Редактировать дело', exact: true }), 'case-ready'),
    ]);
    check((await caseRequest.response())?.status() === 200, 'The owned case details read failed.');
    tracePhase = 'fixture-identity';
    // Register a fixture identity through the real helper, then bind it to the owned durable server attempt.
    await page.evaluate(async settings => {
      const { beginCaseDocumentUpload, getAuthToken } = await import(settings.fixtureModule);
      const scope = JSON.stringify(settings.flow === 'version'
        ? ['version', settings.api + '/api', settings.sourceDocumentId, settings.pending.documentType, settings.pending.status]
        : ['case', settings.api + '/api', settings.caseId, settings.documentKey, settings.pending.documentType, settings.pending.status]);
      const file = new File([settings.pending.bytes], settings.pending.fileName, { type: settings.pending.contentType, lastModified: 1 });
      beginCaseDocumentUpload(scope, file, getAuthToken());
      const rows = JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1'));
      rows[0].key = settings.key;
      sessionStorage.setItem('pp.case-document-upload-attempts.v1', JSON.stringify(rows));
    }, settings);
    if (modal) {
      tracePhase = 'client-navigation';
      // Finish already-issued reads and the owned activity write before navigation.
      await caseReads.drain('client-navigation');
      // Playwright clears its socket registry on main-document navigation without
      // delivering close events for every old socket. Observe the new document
      // before goto so its initial handshake and every restart socket are retained.
      restartObserver = createRestartObserver(settings.api, () => notifyReconnect?.());
      // Wait for the profile's real form rules to finish before leaving its section.
      const [formRequirements] = await Promise.all([
        page.waitForEvent('requestfinished', isClientFormRequirements),
        page.goto(settings.ui + '/clients/' + settings.clientId, { waitUntil: 'domcontentloaded' }),
      ]);
      check((await formRequirements.response())?.ok(), 'Client profile form requirements could not be loaded');
      await caseReads.drain('client-navigation');
      await page.locator('main').getByRole('button', { name: 'Дела', exact: true }).click();
      await page.getByRole('button', { name: 'Редактировать дело Synthetic retirement browser case', exact: true }).click();
      const card = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Дело', exact: true }) });
      await waitAtPhase(card, 'modal-visible');
      await waitAtPhase(card.getByText('Загружаем файлы...', { exact: true }), 'modal-files-loaded', { state: 'hidden' });
      await card.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).fill('Unsaved browser strategy');
    } else if (editing) {
      // Opening the editor invalidates the overview's child reads.
      await caseReads.drain('editor-open');
      tracePhase = 'editor-open';
      await page.getByRole('button', { name: 'Редактировать дело', exact: true }).click();
      await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).fill('Unsaved browser strategy');
    } else {
      await caseReads.drain('document-tab');
      tracePhase = 'document-tab';
      await page.getByRole('button', { name: 'Документы', exact: true }).click();
    }
    const panel = page.getByRole('region', { name: 'Состояние ваших загрузок', exact: true });
    await waitAtPhase(panel.getByText(settings.pending.fileName, { exact: true }), 'owned-attempt-visible');
    if (restarted) await page.evaluate(async settings => {
      const { apiService } = await import(settings.fixtureModule);
      window.__payplannerOwnedRetirement = (${observeCaseRetirementOperation.toString()})(apiService, settings.caseId, settings.key);
    }, settings);
    tracePhase = 'retirement-dialog';
    await panel.getByRole('button', { name: 'Подготовить новую загрузку', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Подготовить новую загрузку', exact: true });
    await caseReads.drain('retirement-confirmation');
    await dialog.getByRole('button', { name: 'Отмена', exact: true }).click();
    check(retirePosts === 0 && filePosts === 0, 'Cancel mutated the server');
    if (editing) check(await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved browser strategy', 'Cancel lost unsaved strategy');
    const readsBeforeConfirmation = documentReads;
    const proofReadsBeforeConfirmation = reads.filter(keys => keys.includes(settings.key)).length;
    await panel.getByRole('button', { name: 'Подготовить новую загрузку', exact: true }).click();
    // Cancelling the first dialog refreshes the upload receipts. Let those
    // real reads finish before the confirmed operation refreshes them again.
    await caseReads.drain('retirement-confirmation');
    tracePhase = 'retirement-confirmation';
    await dialog.getByRole('button', { name: 'Подтвердить', exact: true }).click();
    if (restarted) {
      await restartFinished;
      if (!restartObserver.hasProof()) await new Promise((resolve, reject) => {
        // Use the existing reconnect deadline before the next explicit action,
        // so its access refresh cannot race that action's response.
        const timer = setTimeout(() => { notifyReconnect = undefined; reject(new Error('Notifications did not reconnect after the owned server restart: ' + JSON.stringify(restartObserver.snapshot()))); }, 40000);
        notifyReconnect = () => { clearTimeout(timer); notifyReconnect = undefined; resolve(); };
      });
      const unresolved = panel.getByText('Не удалось подтвердить результат. Обновите загрузки перед следующим действием.', { exact: true });
      const succeeded = panel.getByText('Прежняя попытка прекращена. Новый файл выберите и отправьте отдельно.', { exact: true });
      // Observe actual settlement, rather than infer it from a notice that a
      // reconnect access refresh may replace. This uses the existing page timeout.
      await waitAtPhase({ waitFor: () => page.waitForFunction(() => {
        const operation = window.__payplannerOwnedRetirement?.snapshot();
        return operation?.attempt === 1 && ['fulfilled', 'rejected'].includes(operation.phase);
      }) }, 'restart-operation-settled');
      const operation = await page.evaluate(() => window.__payplannerOwnedRetirement.snapshot());
      const retired = panel.getByRole('listitem').filter({ hasText: settings.pending.fileName })
        .filter({ hasText: 'Прежняя попытка прекращена и больше не добавит документ. Новый файл отправляется отдельным действием.' })
        .getByRole('button', { name: 'Подготовить новую загрузку', exact: true, disabled: false });
      if (await (${waitForCaseRestartResolution.toString()})(unresolved, succeeded, waitAtPhase, retired, operation)) {
        check(retirePosts === 1 && filePosts === 0 && caseSaves === 0, 'Unresolved restart triggered an automatic write');
        const retained = await page.evaluate(() => JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1')));
        check(retained?.length === 1 && retained[0].key === settings.key, 'Unavailable proof released the identity: ' + JSON.stringify({
          registryCount: Array.isArray(retained) ? Math.min(retained.length, 65) : null,
          expectedKeyPresent: Array.isArray(retained) && retained.some(row => row?.key === settings.key),
          proofReads: proofTrace.snapshot(), caseReads: caseReads.snapshot(), startupReads: startupReads.snapshot(),
          retirePosts, filePosts, caseSaves, caseDetailReads, proofRefusals,
        }));
        check(await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved browser strategy', 'Unavailable proof lost unsaved fields');
        // Reconnect can start a pending-uploads read after the first attempt settles.
        // The explicit refresh supersedes that read, so let its real response finish first.
        await caseReads.drain('retirement-confirmation');
        await panel.getByRole('button', { name: 'Обновить загрузки', exact: true }).click();
        await waitAtPhase(panel.getByText('Прежняя попытка прекращена и больше не добавит документ. Новый файл отправляется отдельным действием.', { exact: true }), 'restart-receipt');
        const afterRead = await page.evaluate(() => JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1')));
        check(afterRead?.length === 1 && afterRead[0].key === settings.key, 'Ordinary polling released a retired identity');
        await panel.getByRole('button', { name: 'Подготовить новую загрузку', exact: true }).click();
        await caseReads.drain('retirement-confirmation');
        manualRecovery = true;
        await dialog.getByRole('button', { name: 'Подтвердить', exact: true }).click();
      } else acknowledgedRestartReceipt = retired;
      if (settings.flow.includes('slow')) check(manualRecovery && proofRefusals > 0, 'The forced outage did not exercise explicit unresolved-proof recovery');
      if (proofRefusals > 0) check(manualRecovery, 'A refused proof was silently accepted');
    }
    if (revoked) {
      await waitAtPhase(panel.getByText('Нет доступа к этой попытке. Обновите состояние загрузок.', { exact: true }), 'denied-receipt');
      check(await panel.getByText(settings.pending.fileName, { exact: true }).count() === 0, 'Denied attempt leaked its filename');
      check(await panel.getByRole('button', { name: 'Подготовить новую загрузку', exact: true }).count() === 0, 'Denied attempt remained actionable');
      const registry = await page.evaluate(() => JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1')));
      check(registry?.length === 1 && registry[0].key === settings.key, 'Denied retirement released the old identity');
      check(reads.filter(keys => keys.includes(settings.key)).length === proofReadsBeforeConfirmation, 'Definitive denial requested fallback proof');
      check(await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved browser strategy', 'Denied retirement lost unsaved fields');
      check(retirePosts === 1 && filePosts === 0 && caseSaves === 0, 'Denied retirement wrote unrelated data');
      check(outside.length === 0 && errors.length === 0, 'Unexpected browser activity: ' + JSON.stringify({ outside, errors }));
      await caseReads.drain('browser-close');
      return 'CASE_RETIREMENT_BROWSER_OK ' + JSON.stringify({ flow: settings.flow, retirePosts, filePosts, rightsApplied, readTrace });
    }
    const confirmationReceipt = panel.getByText(publishedWinner
      ? 'Прежняя попытка уже добавила документ. Он сохранён. Новый файл выберите и отправьте отдельно.'
      : 'Прежняя попытка прекращена. Новый файл выберите и отправьте отдельно.', { exact: true });
    await waitAtPhase(acknowledgedRestartReceipt ? confirmationReceipt.or(acknowledgedRestartReceipt).first()
      : confirmationReceipt, 'retirement-receipt');
    // Complete the actual access-refresh response bodies before counting reads
    // or starting a new upload. This is the same pre-upload boundary as below.
    await caseReads.drain('document-upload');
    if (publishedWinner) {
      await page.waitForFunction(() => Array.from(document.querySelectorAll('button'))
        .some(button => button.textContent === 'Обновить загрузки' && !button.disabled));
      check(documentReads === readsBeforeConfirmation + 1, 'Published winner did not refresh exactly the document list');
      check(publisherPosts === 1, 'The actual racing publisher did not run once');
    } else {
      const accessRefreshReads = restarted && !modal ? 1 : 0;
      if (accessRefreshReads) check(caseDetailReads === caseDetailReadsBeforeRestart + 1, 'Reconnect must confirm the current case exactly once');
      check(documentReads === readsBeforeConfirmation + accessRefreshReads,
        'Retirement refreshed unrelated document data beyond the single reconnect access refresh');
    }
    check(retirePosts === (manualRecovery ? 2 : 1) && filePosts === 0, 'Retirement sent a file or repeated automatically');
    if (editing) check(await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved browser strategy', 'Confirmation lost unsaved strategy');
    const registry = await page.evaluate(() => sessionStorage.getItem('pp.case-document-upload-attempts.v1'));
    check(registry === null, 'Confirmed retirement retained its browser identity');
    if (restarted) {
      const operation = await page.evaluate(() => window.__payplannerOwnedRetirement.snapshot());
      check(operation.attempt === (manualRecovery ? 2 : 1) && operation.phase === 'fulfilled' && operation.ownedTerminal,
        'The real retirement operation did not acknowledge the exact owned attempt');
      retirementOperationOutcome = operation;
    }
    if (settings.lostAck) check(reads.some(keys => keys.includes(settings.key)), 'Lost acknowledgement did not read the exact key');
    if (restarted) check(restartApplied, 'The server was never restarted after the committed retirement');
    tracePhase = 'document-upload';
    if (versionAttempt) {
      const inputName = modal ? 'Загрузить новую версию base-browser-version.bin' : 'Загрузить новую версию документа base-browser-version.bin';
      await openDocumentActions('base-browser-version.bin');
      const [versionResponse] = await Promise.all([
        uploadResponseAtPhase('separate-version-response', response => response.request().method() === 'POST' && (modal
          ? new URL(response.url()).pathname === '/api/v2/cases/' + settings.caseId + '/documents'
          : new URL(response.url()).pathname === '/api/case-documents/' + settings.sourceDocumentId + '/versions')),
        page.getByLabel(inputName, { exact: true }).setInputFiles({ name: settings.pending.fileName,
          mimeType: settings.pending.contentType, buffer: Buffer.from(settings.pending.bytes) }),
      ]);
      check(versionResponse.status() === 201, 'Separate version failed: ' + await versionResponse.text());
      const saved = await versionResponse.json();
      check(saved.version === 2 && saved.documentKey === settings.documentKey && saved.id !== settings.sourceDocumentId, 'Separate version did not preserve its source');
      await openDocumentActions(settings.pending.fileName);
      await waitAtPhase(page.getByLabel(modal ? 'Загрузить новую версию ' + settings.pending.fileName
        : 'Загрузить новую версию документа ' + settings.pending.fileName, { exact: true }), 'published-version-input', { state: 'attached' });
      check(filePosts === 1 && fileKeys[0] !== settings.key, 'Separate version reused the retired identity');
      if (editing) check(await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved browser strategy', 'Version lost unsaved strategy');
    } else if (editing) {
      const writFileName = settings.flow.includes('writ') ? settings.pending.fileName : 'separate-browser-writ.pdf';
      const input = page.getByLabel('Загрузить скан исполнительного листа', { exact: true });
      await waitAtPhase(input, 'separate-writ-input', { state: 'attached' });
      await page.waitForFunction(() => {
        const input = document.querySelector('input[aria-label="Загрузить скан исполнительного листа"]');
        return input instanceof HTMLInputElement && !input.disabled;
      });
      const [writResponse] = await Promise.all([
        uploadResponseAtPhase('separate-writ-response', response => new URL(response.url()).pathname === '/api/v2/cases/' + settings.caseId + '/documents' && response.request().method() === 'POST'),
        (async () => {
          await input.setInputFiles({ name: writFileName, mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\nOwned browser writ\n%%EOF') });
          uploadInputState = await input.evaluate(element => ({ connected: element.isConnected, disabled: element.disabled, files: element.files?.length ?? 0 }));
        })(),
      ]);
      check(writResponse.status() === 201, 'Actual writ upload failed: ' + await writResponse.text());
      await waitAtPhase(page.getByLabel('Скачать скан ' + writFileName, { exact: true }), 'new-writ-visible');
      check(filePosts === 1 && fileKeys[0] !== settings.key, 'Explicit writ upload reused the old identity');
      check(await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved browser strategy', 'Writ upload lost unsaved strategy');
    } else {
    await page.getByLabel('Файл документа', { exact: true }).setInputFiles({ name: 'separate-browser-document.txt', mimeType: 'text/plain', buffer: Buffer.from('Separate browser document') });
    const [uploaded] = await Promise.all([
      uploadResponseAtPhase('separate-document-response', response => new URL(response.url()).pathname === '/api/v2/cases/' + settings.caseId + '/documents' && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Загрузить', exact: true }).click(),
    ]);
    check(uploaded.status() === 201, 'Actual document upload failed: ' + uploaded.status() + ' ' + await uploaded.text());
    await openDocumentActions('separate-browser-document.txt');
    const versionInput = page.getByLabel('Загрузить новую версию документа separate-browser-document.txt', { exact: true });
    await waitAtPhase(versionInput, 'document-version-input', { state: 'attached' });
    check(filePosts === 1 && fileKeys[0] !== settings.key, 'Separate upload did not use a new identity');
    await caseReads.drain('document-upload');
    tracePhase = 'version-upload';
    const [versionResponse] = await Promise.all([
      uploadResponseAtPhase('separate-document-version-response', response => /\/api\/case-documents\/\d+\/versions$/.test(new URL(response.url()).pathname) && response.request().method() === 'POST'),
      versionInput.setInputFiles({ name: 'separate-browser-version.txt', mimeType: 'text/plain', buffer: Buffer.from('Separate browser version') }),
    ]);
    check(versionResponse.status() === 201, 'Actual version upload failed: ' + versionResponse.status() + ' ' + await versionResponse.text());
    await openDocumentActions('separate-browser-version.txt');
    await waitAtPhase(page.getByLabel('Загрузить новую версию документа separate-browser-version.txt', { exact: true }), 'new-version-visible', { state: 'attached' });
    check(filePosts === 2 && new Set(fileKeys).size === 2 && !fileKeys.includes(settings.key), 'Version identity was reused');
    }
    check(caseSaves === 0, 'Document actions saved unrelated case fields');
    const reconnectProved = restarted && restartObserver.hasProof();
    if (restarted) check(reconnectProved, 'The restarted server did not acknowledge a new notification connection');
    if (restarted) await page.evaluate(() => {
      window.__payplannerOwnedRetirement.restore();
      delete window.__payplannerOwnedRetirement;
    });
    // A visible upload result may precede its refreshed status read. Complete
    // actual response bodies before this fixture closes the browser connection.
    await caseReads.drain('browser-close');
    tracePhase = 'browser-close';
    check(outside.length === 0, 'Browser attempted an external request');
    check(errors.length === 0, 'Browser page errors: ' + JSON.stringify(errors));
    return 'CASE_RETIREMENT_BROWSER_OK ' + JSON.stringify({ flow: settings.flow, retirePosts, filePosts, publisherPosts, lostAck: settings.lostAck, restartApplied, manualRecovery, proofRefusals, realtimeInterruptions, reconnectProved, retirementOperationOutcome, exactProofRead: reads.some(keys => keys.includes(settings.key)), readTrace });
  }`;
  // Windows limits command lines to 32K characters. Keep the complete scenario
  // and its synthetic credentials in the owned temporary root, not in argv.
  await writeFile(scenarioPath, scenario, { mode: 0o600 });
  const result = await phase('scenario', () => cli('run-code', '--filename', scenarioPath));
  assert.ok(result.includes('CASE_RETIREMENT_BROWSER_OK'), result);
  // The test launcher deliberately clears non-allowlisted environment values.
  // Retain only bounded synthetic request metadata beside its owned temp roots.
  const traceDir = join(dirname(root), 'payplanner-case-retirement-read-traces');
  if (traceDir) {
    assert.ok(isAbsolute(traceDir), 'The owned diagnostic directory must be absolute.');
    const receiptLine = result.split(/\r?\n/).find(line => line.startsWith('"CASE_RETIREMENT_BROWSER_OK ')
      || line.startsWith('CASE_RETIREMENT_BROWSER_OK '));
    assert.ok(receiptLine, 'The CLI must retain its structured diagnostic receipt.');
    const receipt = receiptLine.startsWith('"') ? JSON.parse(receiptLine) : receiptLine;
    const diagnostic = JSON.parse(receipt.slice('CASE_RETIREMENT_BROWSER_OK '.length));
    await mkdir(traceDir, { recursive: true });
    await writeFile(join(traceDir, basename(root) + '.json'), JSON.stringify({ flow, lostAck: diagnostic.lostAck,
      readTrace: diagnostic.readTrace }, null, 2));
  }
  if (bundleLease) await bundleLease.verify();
  console.log(result.slice(result.indexOf('CASE_RETIREMENT_BROWSER_OK'), result.indexOf('CASE_RETIREMENT_BROWSER_OK') + 220));
} catch (error) {
  if (opened) {
    console.error(await cli('snapshot'));
    console.error(await cli('console', 'error'));
  }
  // CLI errors contain synthetic login arguments; emit only the diagnostic response.
  throw new Error(error.stdout || error.message);
} finally {
  try { if (opened) await phase('browser-close', () => cli('close')); }
  finally {
    try { if (vite) await phase('preview-close', () => vite.close()); }
    finally {
      try { if (control) await new Promise((resolve, reject) => control.close(error => error ? reject(error) : resolve())); }
      finally {
        // Release the owned IPC handle only after Vite has removed its stdin termination listener.
        if (control) process.stdin.destroy();
        await rm(scenarioPath, { force: true });
        if (bundleLease) await bundleLease.close();
      }
    }
  }
}
