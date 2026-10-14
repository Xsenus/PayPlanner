import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, readdir, readFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { build, preview } from 'vite';
import { createChatPostRecorder, createChatReadLifecycle, drainChatNavigationReads, redactChatBrowserDiagnostic } from './chat-browser-read-lifecycle.mjs';

const api = new URL(process.env.CASE_RETIREMENT_API);
assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.protocol, 'http:');
const root = process.env.CASE_RETIREMENT_ROOT;
assert.match(basename(root), /^pp_task_upload_browser_[a-f0-9]{32}$/);
const threadId = Number(process.env.CASE_RETIREMENT_ID);
const port = Number(process.env.CASE_RETIREMENT_PORT);
assert.ok(Number.isSafeInteger(threadId) && threadId > 0);
assert.ok(Number.isSafeInteger(port) && port > 0 && port < 65536);
let cliPath = process.env.CASE_RETIREMENT_CLI;
if (cliPath) assert.ok(isAbsolute(cliPath));
else if (process.platform === 'win32' && process.env.LOCALAPPDATA) {
  for (const entry of (await readdir(join(process.env.LOCALAPPDATA, 'npm-cache', '_npx'))).sort()) {
    const directory = join(process.env.LOCALAPPDATA, 'npm-cache', '_npx', entry, 'node_modules', '@playwright', 'cli');
    try {
      const metadata = JSON.parse(await readFile(join(directory, 'package.json'), 'utf8'));
      if (metadata.name === '@playwright/cli' && metadata.version === '0.1.22') {
        cliPath = join(directory, metadata.bin['playwright-cli']); break;
      }
    } catch (cause) { if (cause.code !== 'ENOENT') throw cause; }
  }
}
assert.ok(cliPath, 'An already installed Playwright CLI is required.');
const metadata = JSON.parse(await readFile(join(dirname(cliPath), 'package.json'), 'utf8'));
assert.equal(metadata.name, '@playwright/cli');
assert.equal(metadata.version, '0.1.22');
assert.equal(resolve(cliPath), resolve(dirname(cliPath), metadata.bin['playwright-cli']));
const channel = process.env.CASE_RETIREMENT_BROWSER ?? (process.platform === 'win32' ? 'msedge' : 'chrome');
assert.ok(['msedge', 'chrome'].includes(channel));
const session = `chat-managed-${basename(root).slice(-12)}`;
const execute = promisify(execFile);
const safe = value => redactChatBrowserDiagnostic(value,
  [process.env.CASE_RETIREMENT_PASSWORD, process.env.CASE_RETIREMENT_EMAIL]);
async function cli(...args) {
  let result;
  try {
    result = await execute(process.execPath, [cliPath, `-s=${session}`, ...args], { timeout: 110000, maxBuffer: 1024 * 1024, windowsHide: true });
  } catch (cause) {
    throw new Error(safe(cause.stdout || cause.message));
  }
  // Forward only finite, payload-free counters, never run-code or credentials.
  for (const match of result.stdout.matchAll(/PP_CHAT_BROWSER_READS phase=(?:before-lost-reload|before-draft-reload|pending|published|before-close) started=\d{1,6} finished=\d{1,6} failed=\d{1,6} locallyAborted=\d{1,6} pending=\d{1,6}/g))
    console.log(match[0]);
  for (const match of result.stdout.matchAll(/PP_CHAT_BROWSER_POST phase=(?:start|end|fail) elapsedMs=\d{1,9} apiReadsStarted=\d{1,6} apiReadsFinished=\d{1,6} apiReadsFailed=\d{1,6} apiReadsPending=\d{1,6}/g))
    console.log(match[0]);
  assert.ok(!result.stdout.includes('### Error'), safe(result.stdout + result.stderr));
  return result.stdout;
}
async function applyBoundary() {
  const completed = new Promise((resolve, reject) => {
    let input = '';
    const timer = setTimeout(() => { process.stdin.off('data', receive); reject(new Error('Owned chat copies did not complete.')); }, 30000);
    const receive = (chunk) => {
      input += chunk.toString();
      if (!input.includes('\n')) return;
      clearTimeout(timer); process.stdin.off('data', receive); process.stdin.pause();
      if (input.trim() === 'BOUNDARY_APPLIED') resolve(); else reject(new Error('Unexpected owned boundary command.'));
    };
    process.stdin.on('data', receive);
  });
  console.log('CASE_RETIREMENT_BOUNDARY_READY');
  await completed;
}
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
process.env.VITE_API_URL = new URL('/api', api).href;
const ui = `http://127.0.0.1:${port}`;
const screenshots = resolve('output', 'playwright');
await mkdir(screenshots, { recursive: true });
let vite; let opened = false; let scenarioFailed = false;
const settings = JSON.stringify({ api: api.origin, ui, threadId, email: process.env.CASE_RETIREMENT_EMAIL,
  password: process.env.CASE_RETIREMENT_PASSWORD, lostAcknowledgement: process.env.CASE_RETIREMENT_LOST_ACK === 'true',
  screenshots, suffix: basename(root).slice(-12) });
try {
  console.log('PP_CHAT_BROWSER_PHASE ui-build');
  // Exercise the released React lifecycle, not development StrictMode's extra
  // mount/cleanup cycle, which deliberately aborts the initial status read.
  const outDir = join(root, 'chat-browser-ui-dist');
  await mkdir(outDir);
  const uiConfig = { envDir: root, cacheDir: join(root, 'vite-chat-cache'),
    build: { outDir, emptyOutDir: false }, logLevel: 'error' };
  const built = await build(uiConfig);
  assert.ok((Array.isArray(built) ? built : [built]).some(output =>
    output.output?.some(chunk => chunk.type === 'chunk' && chunk.isEntry && chunk.code.length > 0)),
  'The synthetic chat UI entry module must compile successfully.');
  // The existing enclosing three-minute scenario budget includes this build.
  console.log('PP_CHAT_BROWSER_PHASE preview-listen');
  vite = await preview({ ...uiConfig, preview: { host: '127.0.0.1', port, strictPort: true } });
  await cli('open', 'about:blank', '--browser', channel); opened = true;
  await cli('snapshot');
  console.log('PP_CHAT_BROWSER_PHASE pending-scenario');
  const pending = await cli('run-code', String.raw`async (page) => {
    const settings = ${settings};
    const check = (value, message) => { if (!value) throw new Error(message); };
    const prefix = '/api/internal-chat/threads/' + settings.threadId;
    const outside = []; const errors = []; let sends = 0; let resumes = 0; let key;
    let allowStatus = !settings.lostAcknowledgement; let completeFirst;
    const reads = (${createChatReadLifecycle.toString()})(page, request => {
      const url = new URL(request.url());
      return request.method() === 'GET' && url.origin === settings.api
        && (url.pathname === prefix + '/sends' || url.pathname === prefix + '/sends/' + key);
    }, 20000);
    page.__ownedChatReads = reads;
    const apiReads = (${createChatReadLifecycle.toString()})(page, request =>
      request.method() === 'GET' && new URL(request.url()).origin === settings.api, 20000);
    page.__ownedChatApiReads = apiReads;
    const drainNavigationReads = () => (${drainChatNavigationReads.toString()})(reads, apiReads);
    page.__ownedChatPostTimings = [];
    const recordFirstPost = (${createChatPostRecorder.toString()})(apiReads, page.__ownedChatPostTimings);
    const readPhases = [];
    const recordReads = phase => readPhases.push('PP_CHAT_BROWSER_READS phase=' + phase + ' ' + reads.describe());
    const firstAccepted = new Promise(resolve => { completeFirst = resolve; });
    page.setDefaultTimeout(20000);
    await page.setViewportSize({ width: 1440, height: 900 });
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', entry => {
      if (entry.type() !== 'error') return;
      const location = entry.location().url;
      const expectedLoss = settings.lostAcknowledgement && entry.text().includes('net::ERR_FAILED')
        && (location === settings.api + prefix + '/messages'
          || location === settings.api + prefix + '/sends/' + key);
      if (!expectedLoss) errors.push(entry.text());
    });
    await page.context().route('**/*', async route => {
      const url = new URL(route.request().url());
      if (![settings.api, settings.ui].includes(url.origin)) { outside.push(url.origin); return route.abort(); }
      if (!allowStatus && url.pathname === prefix + '/sends/' + key && route.request().method() === 'GET') {
        reads.expectLocalAbort(route.request());
        apiReads.expectLocalAbort(route.request());
        return route.abort('failed');
      }
      if (url.pathname === prefix + '/messages' && route.request().method() === 'POST') {
        sends++; check(sends <= (settings.lostAcknowledgement ? 2 : 1), 'Unexpected multipart submission');
        if (sends === 1) recordFirstPost('start');
        let response;
        try {
          response = await route.fetch();
          if (sends === 1) recordFirstPost('end');
        } catch (cause) {
          if (sends === 1) recordFirstPost('fail');
          throw cause;
        }
        check(response.status() === 202, 'Send did not remain pending');
        const state = await response.json();
        if (key) check(state.clientRequestId === key, 'Exact retry changed identity');
        key = state.clientRequestId;
        check(/^[a-f0-9-]{36}$/i.test(key) && !state.message, 'Pending identity or message is invalid');
        if (settings.lostAcknowledgement && sends === 1) await route.abort('failed');
        else await route.fulfill({ response });
        if (sends === 1) completeFirst();
        return;
      }
      if (url.pathname.startsWith(prefix + '/sends/') && url.pathname.endsWith('/resume') && route.request().method() === 'POST') {
        resumes++; check(resumes === 1 && url.pathname === prefix + '/sends/' + key + '/resume', 'Continuation changed identity');
      }
      await route.continue();
    });
    await page.goto(settings.ui + '/?ppTab=messenger', { waitUntil: 'domcontentloaded' });
    await page.locator('input[type="email"]').fill(settings.email);
    await page.locator('input[type="password"]').fill(settings.password);
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    await page.locator('input[type="email"]').waitFor({ state: 'hidden' });
    const editor = page.getByPlaceholder('Напишите сообщение…'); await editor.waitFor();
    await editor.fill('Frozen browser chat message');
    await page.locator('input[type="file"]').setInputFiles([
      { name: 'first.txt', mimeType: 'text/plain', buffer: Buffer.from([1, 2, 3]) },
      { name: 'second.txt', mimeType: 'text/plain', buffer: Buffer.from([4, 5, 6]) },
    ]);
    await page.getByRole('button', { name: 'Отправить', exact: true }).click(); await firstAccepted;
    if (settings.lostAcknowledgement) {
      await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.getAttribute('aria-label') === 'Отправить' && !button.disabled));
      const unconfirmed = await page.evaluate(({ threadId, key }) => {
        const storageKey = Object.keys(sessionStorage).find(name => name.startsWith('payplanner.chatSend.v1.')
          && name.endsWith('.' + threadId) && JSON.parse(sessionStorage.getItem(name)).id === key);
        return storageKey ? JSON.parse(sessionStorage.getItem(storageKey)) : null;
      }, { threadId: settings.threadId, key });
      check(unconfirmed?.id === key && unconfirmed.managed !== true, 'Lost response unexpectedly confirmed the attempt');
      await drainNavigationReads(); recordReads('before-lost-reload');
      allowStatus = true;
      await page.reload({ waitUntil: 'domcontentloaded' });
      await page.getByRole('status').waitFor();
      const confirmed = await page.evaluate(({ threadId, key }) => {
        const storageKey = Object.keys(sessionStorage).find(name => name.startsWith('payplanner.chatSend.v1.')
          && name.endsWith('.' + threadId) && JSON.parse(sessionStorage.getItem(name)).id === key);
        return storageKey ? JSON.parse(sessionStorage.getItem(storageKey)) : null;
      }, { threadId: settings.threadId, key });
      check(confirmed?.id === key && confirmed.managed === true, 'Recovered status did not preserve managed identity');
      await page.locator('input[type="file"]').setInputFiles([
        { name: 'first.txt', mimeType: 'text/plain', buffer: Buffer.from([1, 2, 3]) },
        { name: 'second.txt', mimeType: 'text/plain', buffer: Buffer.from([4, 5, 6]) },
      ]);
      const newTimes = await page.locator('input[type="file"]').evaluate(input => Array.from(input.files, file => file.lastModified));
      check(newTimes.some((time, index) => time !== unconfirmed.files[index].lastModified), 'Reselection did not exercise changed file timestamps');
      const retried = page.waitForResponse(response => response.url() === settings.api + prefix + '/messages' && response.status() === 202);
      await page.getByRole('button', { name: 'Отправить', exact: true }).click(); await retried;
    }
    await page.getByRole('status').waitFor();
    check(await editor.inputValue() === 'Frozen browser chat message', 'Pending response erased text');
    check(await page.getByText('first.txt', { exact: true }).count() === 1 && await page.getByText('second.txt', { exact: true }).count() === 1, 'Pending response erased selected files');
    await page.screenshot({ path: settings.screenshots + '/chat-managed-' + settings.suffix + '-pending-desktop.png' });
    await editor.fill('Новый черновик после прежней отправки');
    await drainNavigationReads(); recordReads('before-draft-reload');
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.getByRole('status').waitFor();
    check(await page.getByPlaceholder('Напишите сообщение…').inputValue() === 'Новый черновик после прежней отправки', 'Reload erased the changed draft');
    await page.setViewportSize({ width: 390, height: 844 });
    if (await page.locator('aside.pp-navigation-shell').evaluate(aside => aside.classList.contains('translate-x-0')))
      await page.getByRole('button', { name: 'Toggle menu', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('aside.pp-navigation-shell').getBoundingClientRect().right <= 1);
    await page.getByRole('status').scrollIntoViewIfNeeded();
    check(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'Pending UI overflows the mobile viewport');
    await page.screenshot({ path: settings.screenshots + '/chat-managed-' + settings.suffix + '-pending-mobile.png' });
    await page.setViewportSize({ width: 1440, height: 900 });
    check(outside.length === 0 && errors.length === 0 && sends === (settings.lostAcknowledgement ? 2 : 1) && resumes === 0, 'Pending browser traffic or console errors');
    await reads.drain(); recordReads('pending');
    check(reads.snapshot().failed === 0, 'An owned chat status read failed before continuation.');
    await page.evaluate(proof => { window.__ownedChatProof = proof; }, { api: settings.api, ui: settings.ui,
      key, sends, threadId: settings.threadId, screenshots: settings.screenshots, suffix: settings.suffix });
    return 'CHAT_PENDING_BROWSER_OK\n' + readPhases.join('\n') + '\n' + page.__ownedChatPostTimings.join('\n');
  }`);
  assert.ok(pending.includes('CHAT_PENDING_BROWSER_OK'), pending);
  await cli('snapshot');
  console.log('PP_CHAT_BROWSER_PHASE apply-boundary');
  await applyBoundary();
  console.log('PP_CHAT_BROWSER_PHASE published-scenario');
  const completed = await cli('run-code', String.raw`async (page) => {
    const settings = await page.evaluate(() => window.__ownedChatProof);
    const errors = []; const outside = []; let resumes = 0; let sends = 0;
    const check = (value, message) => { if (!value) throw new Error(message); };
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', entry => { if (entry.type() === 'error') errors.push(entry.text()); });
    page.on('request', request => {
      const url = new URL(request.url());
      if (![settings.api, settings.ui].includes(url.origin)) outside.push(url.origin);
      if (request.method() !== 'POST') return;
      if (url.pathname === '/api/internal-chat/threads/' + settings.threadId + '/messages') sends++;
      if (url.pathname.endsWith('/resume')) {
        resumes++; check(url.pathname === '/api/internal-chat/threads/' + settings.threadId + '/sends/' + settings.key + '/resume', 'Continuation changed identity');
      }
    });
    const resumed = page.waitForResponse(response => response.url().endsWith('/resume') && response.status() === 200);
    await page.getByRole('button', { name: 'Продолжить отправку', exact: true }).click(); await resumed;
    await page.getByRole('status').waitFor({ state: 'hidden' });
    await page.getByLabel('Сообщения чата', { exact: true }).getByText('Frozen browser chat message', { exact: true }).waitFor();
    check(await page.getByPlaceholder('Напишите сообщение…').inputValue() === 'Новый черновик после прежней отправки', 'Publication erased the new draft');
    check(await page.locator('input[type="file"]').evaluate(input => input.files.length) === 0, 'Recovery required browser originals');
    check([1, 2].includes(settings.sends) && sends === 0 && resumes === 1, 'Recovery submitted another message');
    check(outside.length === 0 && errors.length === 0, 'Browser network or console errors: ' + errors.join('; '));
    await page.screenshot({ path: settings.screenshots + '/chat-managed-' + settings.suffix + '-published.png' });
    await page.__ownedChatReads.drain();
    check(page.__ownedChatReads.snapshot().failed === 0, 'An owned chat status read failed after continuation.');
    return 'CASE_RETIREMENT_BROWSER_OK ' + JSON.stringify({ sends: settings.sends, resumes, key: settings.key })
      + '\nPP_CHAT_BROWSER_READS phase=published ' + page.__ownedChatReads.describe();
  }`);
  assert.ok(completed.includes('CASE_RETIREMENT_BROWSER_OK'), completed); console.log(completed);
} catch (cause) {
  scenarioFailed = true;
  if (opened) { console.error(safe(await cli('snapshot'))); console.error(safe(await cli('console', 'error'))); }
  // Avoid echoing run-code arguments containing synthetic credentials.
  throw new Error(safe(cause.stdout || cause.message));
} finally {
  try {
    if (opened) {
      let drainError;
      try {
        await cli('run-code', String.raw`async (page) => {
          if (!page.__ownedChatReads) return 'CHAT_READ_OBSERVER_NOT_STARTED';
          const postTimings = (page.__ownedChatPostTimings ?? []).join('\n');
          try {
            await (${drainChatNavigationReads.toString()})(page.__ownedChatReads, page.__ownedChatApiReads);
            if (page.__ownedChatReads.snapshot().failed !== 0) throw new Error('An owned chat read failed before browser close.');
            return 'PP_CHAT_BROWSER_READS phase=before-close ' + page.__ownedChatReads.describe()
              + '\n' + postTimings;
          } catch (cause) {
            // Keep the first POST evidence even when the existing read drain fails.
            throw new Error(postTimings + '\n' + cause.message);
          } finally { page.__ownedChatReads.dispose(); page.__ownedChatApiReads?.dispose(); }
        }`);
      } catch (cause) { drainError = cause; }
      finally { console.log('PP_CHAT_BROWSER_PHASE browser-close'); await cli('close'); }
      // Cleanup diagnostics must not replace a failed scenario's primary error.
      if (drainError && !scenarioFailed) throw drainError;
    }
  }
  finally { try { if (vite) await vite.close(); } finally { process.stdin.destroy(); } }
}
