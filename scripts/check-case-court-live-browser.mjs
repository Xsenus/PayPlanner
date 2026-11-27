import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { createServer as createControlServer } from 'node:http';
import { prepareSyntheticBrowserUi } from './synthetic-browser-ui.mjs';
import { createCaseReadLifecycle, isCaseBoundaryRead } from './case-retirement-browser-lifecycle.mjs';
import { redactChatBrowserDiagnostic } from './chat-browser-read-lifecycle.mjs';

const api = new URL(process.env.CASE_RETIREMENT_API);
assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.protocol, 'http:');
const root = process.env.CASE_RETIREMENT_ROOT;
assert.match(basename(root), /^pp_task_upload_browser_[a-f0-9]{32}$/);
const caseId = Number(process.env.CASE_RETIREMENT_ID);
const port = Number(process.env.CASE_RETIREMENT_PORT);
assert.ok(Number.isSafeInteger(caseId) && caseId > 0);
assert.ok(Number.isSafeInteger(port) && port > 0 && port < 65536);
const flow = process.env.CASE_RETIREMENT_FLOW;
assert.ok(['court-edit', 'court-overview'].includes(flow));
assert.equal(process.env.CASE_RETIREMENT_BOUNDARY, 'true');
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
const scenarioPath = join(root, 'court-browser-scenario.js');
const execute = promisify(execFile);
const safe = value => redactChatBrowserDiagnostic(value,
  [process.env.CASE_RETIREMENT_PASSWORD, process.env.CASE_RETIREMENT_EMAIL]).slice(0, 16000);
async function cli(...args) {
  let result;
  try {
    result = await execute(process.execPath, [cliPath, `-s=${session}`, ...args], {
      timeout: 110_000, maxBuffer: 1024 * 1024, windowsHide: true,
    });
  } catch (error) {
    throw new Error(safe(error.stdout || error.stderr || `Court Playwright command ${args[0]} ${error.killed ? 'timed out' : 'failed'}`));
  }
  assert.ok(!result.stdout.includes('### Error'), safe(result.stdout + result.stderr));
  return result.stdout;
}
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
process.env.VITE_API_URL = new URL('/api', api).href;
const ui = `http://127.0.0.1:${port}`;
let vite;
let opened = false;
let control;
let controlUrl;
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
  // Reuse only a fully qualified production artifact; API/data/port remain scenario-owned.
  console.log('PP_COURT_BROWSER_PHASE ui-build');
  const browserUi = await prepareSyntheticBrowserUi({ api, port });
  vite = await browserUi.startPreview();
  assert.equal(vite.config.isProduction, true, 'The court browser must qualify production React behavior.');
  assert.equal(vite.config.env.DEV, false, 'The release browser must not use development-only mount effects.');
  console.log('PP_COURT_BROWSER_PHASE cli-open');
  await cli('open', 'about:blank', '--browser', browser); opened = true;
  await cli('goto', ui + '/cases/' + caseId);
  await cli('snapshot');
  const settings = JSON.stringify({ api: api.origin, ui, caseId, flow, controlUrl, downloadPath: join(root, 'court-browser-download.html'),
    email: process.env.CASE_RETIREMENT_EMAIL, password: process.env.CASE_RETIREMENT_PASSWORD });
  const scenario = String.raw`async (page) => {
    const settings = ${settings};
    const check = (condition, message) => { if (!condition) throw new Error(message); };
    const checkpoint = name => page.evaluate(value => { globalThis.__ppCourtGatePhase = value; }, name);
    const casePath = '/api/v2/cases/' + settings.caseId;
    const path = casePath + '/court-sync/run';
    const courtLogsPath = casePath + '/court-sync/logs';
    const outside = []; const errors = []; const results = [];
    let otherWrites = 0; let authorization;
    page.setDefaultTimeout(20000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await page.context().route('**/*', async route => {
      const request = route.request(); const url = new URL(request.url());
      if (![settings.api, settings.ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
      if (url.origin === settings.api && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())
        && !['/api/auth/login', '/api/user-activity', '/api/user-activity/heartbeat', '/api/notifications/mark-read', '/api/v2/cases/' + settings.caseId + '/activity'].includes(url.pathname)) {
        if (url.pathname !== path && !url.pathname.startsWith('/hubs/')) otherWrites++;
      }
      if (url.pathname !== path || request.method() !== 'POST') return route.continue();
      authorization = request.headers().authorization;
      const response = await route.fetch();
      check(response.status() === 200, 'Court sync HTTP status: ' + response.status());
      const result = await response.json(); results.push(result);
      check(result.status === 'external-sync-completed', 'Court sync did not complete');
      check(results.length <= 2, 'Unexpected automatic court sync');
      await route.fulfill({ response });
    });
    const caseReads = (${createCaseReadLifecycle})(page,
      request => (${isCaseBoundaryRead})(request, settings.api, settings.caseId), 20000);
    const completeRead = async (ownedPath, timeout = 20000) => {
      // Reload can cancel a superseded read after its headers arrived. Observe
      // the next completed request, including its body, within the same deadline.
      const request = await page.waitForEvent('requestfinished', {
        predicate: candidate => candidate.method() === 'GET' && candidate.url() === settings.api + ownedPath,
        timeout,
      });
      check((await request.response())?.status() === 200, 'Owned court page read failed: ' + ownedPath);
    };
    const waitForSyncPost = () => page.waitForResponse(response => response.url() === settings.api + path
      && response.request().method() === 'POST');
    try {
      await checkpoint('login');
      await page.bringToFront();
      const loginEmail = page.getByLabel('Email', { exact: true });
      await loginEmail.fill(settings.email);
      await page.locator('input[type="password"]').fill(settings.password);
      // The case render depends on the first authenticated reads. Use the
      // cold-start budget for those reads before timing the court controls.
      const ready = page.waitForResponse(response => response.request().method() === 'POST'
        && response.url() === settings.api + '/api/auth/login').then(response => {
        check(response.status() === 200, 'Owned court browser login failed');
        return Promise.all([completeRead(casePath, 45000), completeRead(courtLogsPath, 45000)]);
      });
      await Promise.all([ready, page.getByRole('button', { name: 'Войти', exact: true }).click()]);
      await loginEmail.waitFor({ state: 'hidden' });
      const courtTab = page.getByRole('button', { name: 'Суд', exact: true });
      await courtTab.waitFor();
      await caseReads.drain('document-tab');
      await courtTab.click();
      if (settings.flow === 'court-overview') {
        await page.getByRole('button', { name: 'Вернуться к обзору', exact: true }).click();
        await page.getByRole('heading', { name: 'Сводка по судебному делу', exact: true }).waitFor();
      } else await page.getByText('Режим редактирования судебных данных', { exact: true }).waitFor();
      const sync = page.getByRole('button', { name: 'Проверить источник', exact: true });
      await checkpoint('first-source-check');
      await Promise.all([waitForSyncPost(), completeRead(courtLogsPath), sync.click()]);
      const pending = page.getByText(/сводки ожидают обязательных копий: 1; сроки и напоминания сохранены/).first();
      await pending.waitFor();
      check((await pending.locator('..').getAttribute('class')).includes('bg-amber-50'), 'Pending court summary is not shown as waiting');
      check(results.length === 1 && results[0].createdProceduralDeadlines === 1 && results[0].createdCourtActDocuments === 0
        && results[0].pendingCourtActDocuments === 1, 'Pending response lost immediate controls or claimed a ready document');
      const before = await page.request.get(settings.api + '/api/v2/cases/' + settings.caseId + '/documents', { headers: { Authorization: authorization } });
      check(before.ok() && (await before.json()).length === 0, 'A document became visible before copies');
      await checkpoint('storage-worker-boundary');
      const boundary = await page.request.post(settings.controlUrl);
      check(boundary.ok(), 'Owned storage worker boundary failed');
      check(results.length === 1, 'Worker completion caused an automatic sync');
      await checkpoint('second-source-check');
      await Promise.all([waitForSyncPost(), completeRead(courtLogsPath), sync.click()]);
      await page.waitForFunction(() => !document.body.textContent.includes('сводки ожидают обязательных копий: 1'));
      check(results.length === 2 && results[1].createdProceduralDeadlines === 0 && results[1].pendingCourtActDocuments === 0
        && results[1].createdCourtActDocuments === 0, 'Repeated sync created another control or summary');
      const response = await page.request.get(settings.api + '/api/v2/cases/' + settings.caseId + '/documents', { headers: { Authorization: authorization } });
      check(response.ok(), 'Ready document list failed');
      const documents = await response.json(); check(documents.length === 1, 'Expected one ready court summary');
      const document = documents[0];
      const documentsTab = page.getByRole('button', { name: 'Документы', exact: true });
      await documentsTab.waitFor();
      await caseReads.drain('document-tab');
      await documentsTab.click();
      await page.getByText(document.originalFileName, { exact: true }).first().waitFor();
      const actionsButton = page.getByRole('button', { name: 'Сведения и действия документа ' + document.originalFileName, exact: true });
      await actionsButton.click();
      check(await actionsButton.getAttribute('aria-expanded') === 'true', 'The court document actions must be expanded before downloading');
      const downloadEvent = page.waitForEvent('download');
      await page.getByRole('button', { name: 'Скачать документ ' + document.originalFileName, exact: true }).click();
      const download = await downloadEvent; check(await download.failure() === null, 'Court summary download failed');
      await download.saveAs(settings.downloadPath);
      await caseReads.drain('browser-close');
      check(otherWrites === 0, 'Court sync caused another business write: ' + otherWrites);
      check(outside.length === 0, 'Browser called an external host');
      check(errors.length === 0, 'Browser errors: ' + errors.join('; '));
      return 'CASE_RETIREMENT_BROWSER_OK ' + JSON.stringify({ flow: settings.flow, posts: results.length, documentId: document.id });
    } finally {
      caseReads.dispose();
    }
  }`;
  await writeFile(scenarioPath, scenario, { flag: 'wx', mode: 0o600 });
  const result = await cli('run-code', '--filename', scenarioPath);
  assert.ok(result.includes('CASE_RETIREMENT_BROWSER_OK'), result); console.log(result);
} catch (error) {
  if (opened) {
    try {
      console.error(safe(await cli('run-code', 'async page => page.evaluate(() => globalThis.__ppCourtGatePhase ?? "not-started")')));
      console.error(safe(await cli('snapshot')));
      console.error(safe(await cli('console', 'error')));
    } catch { /* Preserve the original browser failure. */ }
  }
  throw error;
} finally {
  try { if (opened) await cli('close'); }
  finally {
    try { if (vite) await vite.close(); }
    finally {
      try { if (control) await new Promise((resolve, reject) => control.close(error => error ? reject(error) : resolve())); }
      finally {
        if (control) process.stdin.destroy();
        await rm(scenarioPath, { force: true });
      }
    }
  }
}
