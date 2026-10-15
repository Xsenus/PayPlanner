import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { createServer as createControlServer } from 'node:http';
import { build, preview } from 'vite';
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
assert.match(flow, /^financial-(invoice|act|contract)-(edit|modal|editor)(-lost)?$/);
const clientId = Number(process.env.CASE_RETIREMENT_CLIENT_ID);
assert.ok(Number.isSafeInteger(clientId) && clientId > 0);
assert.equal(process.env.CASE_RETIREMENT_BOUNDARY, flow.endsWith('-lost') ? 'false' : 'true');
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
const scenarioPath = join(root, 'financial-browser-scenario.js');
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
const phase = name => console.log('PP_FINANCIAL_BROWSER_PHASE ' + name);
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
  phase('vite-create');
  // Match the existing chat gate: compile the released UI before navigation,
  // so source transforms do not compete with the API during real requests.
  // The build remains inside the unchanged enclosing three-minute deadline.
  const outDir = join(root, 'financial-browser-ui-dist');
  await mkdir(outDir);
  const uiConfig = { envDir: root, cacheDir: join(root, 'vite-financial-cache'),
    build: { outDir, emptyOutDir: false }, logLevel: 'error' };
  const built = await build(uiConfig);
  assert.ok((Array.isArray(built) ? built : [built]).some(output =>
    output.output?.some(chunk => chunk.type === 'chunk' && chunk.isEntry && chunk.code.length > 0)),
  'The synthetic financial UI production entry must compile successfully.');
  phase('vite-listen');
  vite = await preview({ ...uiConfig, preview: { host: '127.0.0.1', port, strictPort: true } });
  assert.equal(vite.config.isProduction, true, 'The financial browser must qualify production React behavior.');
  assert.equal(vite.config.env.DEV, false, 'The release browser must not use development-only mount effects.');
  phase('cli-open');
  await cli('open', 'about:blank', '--browser', browser); opened = true;
  phase('cli-goto');
  await cli('goto', ui + '/cases/' + caseId);
  phase('cli-snapshot');
  await cli('snapshot');
  const settings = JSON.stringify({ api: api.origin, ui, caseId, clientId, flow, controlUrl,
    downloadPath: join(root, 'financial-browser-download.html'),
    email: process.env.CASE_RETIREMENT_EMAIL, password: process.env.CASE_RETIREMENT_PASSWORD });
  phase('scenario');
  await writeFile(scenarioPath, String.raw`async (page) => {
    const settings = ${settings};
    const check = (value, message) => { if (!value) throw new Error(message); };
    const checkpoint = name => page.evaluate(value => { globalThis.__ppFinancialGatePhase = value; }, name);
    await checkpoint('started');
    // Wait for response bodies and the next render frame, using the same
    // five-second budget. Periodic inbox refreshes need no artificial quiet gap.
    const reads = (${createCaseReadLifecycle})(page,
      request => (${isCaseBoundaryRead})(request, settings.api, settings.caseId), 5000);
    const kind = settings.flow.split('-')[1]; const modal = settings.flow.split('-')[2] === 'modal'; const lost = settings.flow.endsWith('-lost');
    const editor = settings.flow.split('-')[2] === 'editor';
    let firstHandled; const first = new Promise(resolve => { firstHandled = resolve; });
    const path = '/api/v2/cases/' + settings.caseId + '/work-order/create-' + kind;
    const keys = []; const outside = []; const errors = []; let otherWrites = 0;
    page.setDefaultTimeout(45000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const expectedPending = message.location().url === settings.api + path && (lost ? /Failed to fetch|net::ERR_FAILED|Failed to load resource/.test(message.text()) : message.text().includes('503'));
      if (!expectedPending) errors.push(message.text());
    });
    await page.context().route('**/*', async route => {
      const request = route.request(); const url = new URL(request.url());
      if (![settings.api, settings.ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
      if (url.origin === settings.api && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())
        && !['/api/auth/login', '/api/user-activity', '/api/user-activity/heartbeat', '/api/notifications/mark-read',
          '/api/v2/cases/' + settings.caseId + '/activity'].includes(url.pathname)) {
        if (url.pathname !== path && !url.pathname.startsWith('/hubs/')) otherWrites++;
      }
      if (url.pathname !== path || request.method() !== 'POST') return route.continue();
      const key = request.headers()['x-upload-key'];
      check(/^[a-f0-9-]{36}$/i.test(key), 'Financial generation key is missing'); keys.push(key);
      check(keys.length === 1, 'Unexpected automatic financial generation');
      const response = await route.fetch();
      check(response.status() === (lost ? 201 : 503), 'Unexpected financial result at copy boundary: ' + response.status());
      const result = await response.json();
      if (lost) {
        check(result.documentId > 0 && result.amount === 35, 'Lost acknowledgement has no complete financial result');
        await route.abort('failed');
      } else {
        check(result.status === 'pending', 'Financial pending response lost its state');
        await route.fulfill({ response });
      }
      firstHandled();
    });
    await checkpoint('login-read-boundary');
    await reads.drain('client-navigation');
    await checkpoint('login-fields');
    await page.locator('input[type="email"]').fill(settings.email);
    await page.locator('input[type="password"]').fill(settings.password);
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    await page.locator('input[type="email"]').waitFor({ state: 'hidden' });
    await checkpoint('authenticated');
    await page.getByRole('button', { name: 'Заказ-наряд', exact: true }).click();
    const buttonName = kind === 'invoice' ? 'Создать счет' : kind === 'act' ? 'Создать акт' : 'Создать договор';
    const createButton = page.getByRole('button', { name: buttonName, exact: true });
    await createButton.click(); await first;
    await page.waitForFunction(name => [...document.querySelectorAll('button')]
      .some(button => button.textContent.trim() === name && !button.disabled), buttonName);
    await page.waitForFunction(() => JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1'))?.length === 1);
    const accepted = await page.evaluate(() => JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1')));
    check(accepted[0].key === keys[0], 'Pending financial request lost its identity');
    let card = page;
    if (modal) {
      await reads.drain('client-navigation');
      await page.goto(settings.ui + '/clients/' + settings.clientId, { waitUntil: 'domcontentloaded' });
      await page.locator('main').getByRole('button', { name: 'Дела', exact: true }).click();
      await page.getByRole('button', { name: 'Редактировать дело Synthetic financial browser case', exact: true }).click();
      card = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Дело', exact: true }) });
      await card.waitFor();
      await card.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).fill('Unsaved financial strategy');
    } else if (editor) {
      await page.getByRole('button', { name: 'Редактировать дело', exact: true }).click();
      await page.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).fill('Unsaved financial strategy');
    } else await page.getByRole('button', { name: 'Документы', exact: true }).click();
    const pendingName = card.getByText(new RegExp('case-' + settings.caseId + '-' + kind + '-[0-9]+\\.html')).first();
    if (!(editor && lost)) await pendingName.waitFor();
    check(keys.length === 1, 'Opening document state started another financial request');
    if (!lost) {
      const boundary = await page.request.post(settings.controlUrl);
      check(boundary.ok(), 'Owned financial worker boundary failed');
    }
    await page.waitForFunction(() => sessionStorage.getItem('pp.case-document-upload-attempts.v1') === null);
    check(keys.length === 1, 'Copy completion started another financial request');
    if (modal || editor) check(await card.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue()
      === 'Unsaved financial strategy', 'Financial publication lost unsaved fields');
    if (editor) {
      await page.getByRole('button', { name: 'Отмена', exact: true }).first().click();
      if (kind === 'invoice') {
        await page.waitForFunction(() => [...document.querySelectorAll('button')]
          .some(button => button.textContent.trim() === 'Создать счет' && button.disabled));
        check(await createButton.isDisabled(), 'Published invoice left its work order action enabled after editing');
      }
      await page.getByRole('button', { name: 'Документы', exact: true }).click();
    }
    const downloadButton = card.getByRole('button', { name: new RegExp((modal ? '^Скачать case-' : '^Скачать документ case-') + settings.caseId + '-' + kind + '-[0-9]+\\.html$') });
    if (!modal) {
      const actionsButton = card.getByRole('button', { name: new RegExp('^Сведения и действия документа case-' + settings.caseId + '-' + kind + '-[0-9]+[.]html$') });
      await actionsButton.click();
      check(await actionsButton.getAttribute('aria-expanded') === 'true', 'The generated document actions must be expanded before downloading');
    }
    await downloadButton.waitFor();
    const downloadEvent = page.waitForEvent('download'); await downloadButton.click();
    const download = await downloadEvent; check(await download.failure() === null, 'Financial document download failed');
    await download.saveAs(settings.downloadPath);
    await reads.drain('browser-close');
    check(otherWrites === 0, 'Financial generation caused another business write: ' + otherWrites);
    check(outside.length === 0, 'Browser called an external host');
    check(errors.length === 0, 'Browser errors: ' + errors.join('; '));
    reads.dispose();
    return 'CASE_RETIREMENT_BROWSER_OK ' + JSON.stringify({ flow: settings.flow, posts: keys.length });
  }`, { mode: 0o600 });
  const result = await cli('run-code', '--filename', scenarioPath);
  assert.ok(result.includes('CASE_RETIREMENT_BROWSER_OK'), result); console.log(result);
} catch (error) {
  const safe = value => redactChatBrowserDiagnostic(value,
    [process.env.CASE_RETIREMENT_PASSWORD, process.env.CASE_RETIREMENT_EMAIL]).slice(0, 16000);
  const diagnostic = safe(JSON.stringify({ message: error.message, code: error.code,
    signal: error.signal, killed: error.killed, stdout: error.stdout, stderr: error.stderr }));
  console.error(diagnostic);
  if (opened) {
    try {
      console.error(safe(await cli('run-code', 'async page => ({ phase: await page.evaluate(() => globalThis.__ppFinancialGatePhase ?? "not-started") })')));
      console.error(safe(await cli('snapshot')));
      console.error(safe(await cli('console', 'error')));
    } catch (inspectionError) {
      console.error(safe('Financial failure inspection: ' + inspectionError.message));
    }
  }
  throw new Error(diagnostic);
} finally {
  phase('browser-close');
  try { if (opened) await cli('close'); }
  finally {
    phase('vite-close');
    try { if (vite) await vite.close(); }
    finally {
      phase('control-close');
      try { if (control) await new Promise((resolve, reject) => control.close(error => error ? reject(error) : resolve())); }
      finally {
        phase('stdin-close');
        if (control) process.stdin.destroy();
        await rm(scenarioPath, { force: true });
      }
    }
  }
}
phase('done');
await new Promise(setImmediate);
const remainingResources = Object.create(null);
for (const resource of process.getActiveResourcesInfo()) remainingResources[resource] = (remainingResources[resource] ?? 0) + 1;
await writeFile(join(root, 'financial-shutdown-resources.json'), JSON.stringify(remainingResources));
