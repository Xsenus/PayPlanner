import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readdir, readFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { prepareSyntheticBrowserUi } from './synthetic-browser-ui.mjs';
import { createCaseReadLifecycle, isCaseBoundaryRead } from './case-retirement-browser-lifecycle.mjs';

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
assert.ok(['generated-edit', 'generated-modal', 'generated-order'].includes(flow));
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
let vite; let opened = false;
try {
  // Reuse only a fully qualified production artifact; API/data/port remain scenario-owned.
  console.log('PP_GENERATED_BROWSER_PHASE ui-build');
  const browserUi = await prepareSyntheticBrowserUi({ api, port });
  vite = await browserUi.startPreview();
  assert.equal(vite.config.isProduction, true, 'The generated-document browser must qualify production React behavior.');
  assert.equal(vite.config.env.DEV, false, 'The release browser must not use development-only mount effects.');
  console.log('PP_GENERATED_BROWSER_PHASE cli-open');
  await cli('open', 'about:blank', '--browser', browser); opened = true;
  await cli('goto', ui + (flow === 'generated-modal' ? '/clients/' + clientId : '/cases/' + caseId));
  await cli('snapshot');
  const settings = JSON.stringify({ api: api.origin, ui, caseId, clientId, flow, email: process.env.CASE_RETIREMENT_EMAIL, password: process.env.CASE_RETIREMENT_PASSWORD });
  const result = await cli('run-code', String.raw`async (page) => {
    const settings = ${settings};
    const check = (value, message) => { if (!value) throw new Error(message); };
    const order = settings.flow === 'generated-order';
    const modal = settings.flow === 'generated-modal';
    const path = '/api/v2/cases/' + settings.caseId + (order ? '/work-order/generate-document' : '/documents/generate-template');
    const keys = []; const documents = []; const outside = []; const errors = [];
    let otherWrites = 0; let completeFirst; let completeSecond;
    const first = new Promise(resolve => { completeFirst = resolve; });
    const second = new Promise(resolve => { completeSecond = resolve; });
    let allowPublicationReceipts;
    const publicationReceipts = new Promise(resolve => { allowPublicationReceipts = resolve; });
    page.setDefaultTimeout(20000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const expectedLoss = message.location().url === settings.api + path && message.text().includes('net::ERR_FAILED');
      if (!expectedLoss) errors.push(message.text());
    });
    await page.context().route('**/*', async route => {
      const request = route.request(); const url = new URL(request.url());
      if (![settings.api, settings.ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
      // This scenario exercises explicit retry before a publication read can
      // acknowledge the lost response. Ready-receipt recovery has its own gate.
      if (url.origin === settings.api && request.method() === 'GET'
        && url.pathname.startsWith('/api/v2/cases/' + settings.caseId + '/documents/uploads')) {
        const response = await route.fetch();
        if (keys.length === 1) await publicationReceipts;
        return route.fulfill({ response });
      }
      if (url.origin === settings.api && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())
        && !['/api/auth/login', '/api/user-activity', '/api/user-activity/heartbeat', '/api/notifications/mark-read', '/api/v2/cases/' + settings.caseId + '/activity'].includes(url.pathname)) {
        if (url.pathname !== path && !url.pathname.startsWith('/hubs/')) otherWrites++;
      }
      if (url.pathname !== path || request.method() !== 'POST') return route.continue();
      const key = request.headers()['x-upload-key'];
      check(/^[a-f0-9-]{36}$/i.test(key), 'Generation key is missing'); keys.push(key);
      check(keys.length <= 2, 'Unexpected automatic generation');
      const response = await route.fetch();
      check(response.status() === 201, 'Owned generation failed: ' + response.status());
      const document = await response.json(); documents.push(document);
      if (keys.length === 1) { await route.abort('failed'); completeFirst(); }
      else { await route.fulfill({ response }); completeSecond(); allowPublicationReceipts(); }
    });
    const caseReads = (${createCaseReadLifecycle})(page,
      request => (${isCaseBoundaryRead})(request, settings.api, settings.caseId), 20000);
    try {
      const loginEmail = page.getByLabel('Email', { exact: true });
      await loginEmail.fill(settings.email);
      await page.locator('input[type="password"]').fill(settings.password);
      await page.getByRole('button', { name: 'Войти', exact: true }).click();
      await loginEmail.waitFor({ state: 'hidden' });
      let card = page;
      if (modal) {
        const casesTab = page.locator('main').getByRole('button', { name: 'Дела', exact: true });
        await casesTab.waitFor();
        await caseReads.drain('client-navigation');
        await casesTab.click();
        const editCaseButton = page.getByRole('button', { name: 'Редактировать дело Synthetic generated browser case', exact: true });
        await editCaseButton.waitFor();
        await caseReads.drain('editor-open');
        await editCaseButton.click();
        card = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Дело', exact: true }) });
        await card.waitFor();
        await card.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).fill('Unsaved generated strategy');
      } else {
        const caseTab = page.getByRole('button', { name: order ? 'Заказ-наряд' : 'Документы', exact: true });
        await caseTab.waitFor();
        await caseReads.drain('document-tab');
        await caseTab.click();
      }
      if (!order) await card.getByLabel('Шаблон документа дела', { exact: true }).selectOption('custom');
      const name = order ? 'В документы' : 'Сформировать';
      const button = card.getByRole('button', { name, exact: true });
      await button.click(); await first;
      await page.waitForFunction(name => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === name && !button.disabled), name);
      const pending = await page.evaluate(() => JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1')));
      check(pending?.length === 1 && pending[0].key === keys[0], 'Lost ACK did not retain the original identity');
      check(keys.length === 1, 'Lost ACK started an automatic generation');
      await button.click(); await second;
      await page.waitForFunction(() => sessionStorage.getItem('pp.case-document-upload-attempts.v1') === null);
      check(keys.length === 2 && keys[0] === keys[1], 'Explicit replay changed the attempt');
      check(documents[0].id === documents[1].id && documents[1].version === 1, 'Replay created another document');
      await card.getByText(documents[1].originalFileName, { exact: true }).first().waitFor();
      if (modal) check(await card.getByRole('textbox', { name: 'Стратегия и план действий', exact: true }).inputValue() === 'Unsaved generated strategy', 'Generation lost unsaved fields');
      await caseReads.drain('browser-close');
      check(otherWrites === 0, 'Generation caused another business write: ' + otherWrites);
      check(outside.length === 0, 'Browser called an external host');
      check(errors.length === 0, 'Browser errors: ' + errors.join('; '));
      return 'CASE_RETIREMENT_BROWSER_OK ' + JSON.stringify({ flow: settings.flow, posts: keys.length, documentId: documents[0].id });
    } finally {
      caseReads.dispose();
    }
  }`);
  assert.ok(result.includes('CASE_RETIREMENT_BROWSER_OK'), result); console.log(result);
} catch (error) {
  if (opened) { console.error(await cli('snapshot')); console.error(await cli('console', 'error')); }
  throw new Error(error.stdout || error.message);
} finally {
  try { if (opened) await cli('close'); } finally { if (vite) await vite.close(); }
}
