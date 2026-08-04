import assert from 'node:assert/strict';
import { once } from 'node:events';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from '@playwright/test';

async function launchChromium() {
  const attempts = [
    process.env.PLAYWRIGHT_CHROMIUM_CHANNEL
      ? { label: process.env.PLAYWRIGHT_CHROMIUM_CHANNEL, options: { channel: process.env.PLAYWRIGHT_CHROMIUM_CHANNEL } }
      : null,
    { label: 'msedge', options: { channel: 'msedge' } },
    { label: 'chrome', options: { channel: 'chrome' } },
    { label: 'bundled chromium', options: {} },
  ].filter(Boolean);

  const failures = [];
  for (const attempt of attempts) {
    try {
      const browser = await chromium.launch({
        ...attempt.options,
        headless: true,
      });
      return { browser, label: attempt.label };
    } catch (error) {
      failures.push(`${attempt.label}: ${error.message}`);
    }
  }

  throw new Error(`Unable to launch Chromium for browser smoke.\n${failures.join('\n')}`);
}

async function createSmokeServer() {
  const server = createServer((request, response) => {
    if (request.url === '/popup') {
      response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>PayPlanner popup smoke</title><h1>Popup smoke</h1>');
      return;
    }

    response.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
    response.end('not found');
  });

  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  assert(address && typeof address === 'object');
  return { server, origin: `http://127.0.0.1:${address.port}` };
}

async function main() {
  const downloadDir = await mkdtemp(path.join(tmpdir(), 'payplanner-browser-smoke-'));
  const { browser, label } = await launchChromium();
  const { server, origin } = await createSmokeServer();

  try {
    const context = await browser.newContext({ acceptDownloads: true });
    const page = await context.newPage();

    await page.setContent(`
      <!doctype html>
      <meta charset="utf-8" />
      <title>PayPlanner browser smoke</title>
      <button id="download">Download</button>
      <a
        id="new-tab"
        href="${origin}/popup"
        target="_blank"
        rel="noopener noreferrer"
      >New tab</a>
      <script>
        document.querySelector('#download').addEventListener('click', () => {
          const blob = new Blob(['payplanner browser smoke'], { type: 'text/plain' });
          const url = URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = url;
          link.download = 'payplanner-browser-smoke.txt';
          document.body.appendChild(link);
          link.click();
          link.remove();
          URL.revokeObjectURL(url);
        });
      </script>
    `);

    const downloadPromise = page.waitForEvent('download');
    await page.locator('#download').click();
    const download = await downloadPromise;
    assert.equal(download.suggestedFilename(), 'payplanner-browser-smoke.txt');

    const savedDownloadPath = path.join(downloadDir, download.suggestedFilename());
    await download.saveAs(savedDownloadPath);
    assert.equal(await readFile(savedDownloadPath, 'utf8'), 'payplanner browser smoke');

    const popupPromise = page.waitForEvent('popup');
    await page.locator('#new-tab').click();
    const popup = await popupPromise;
    // The popup event can be emitted for Chromium's temporary "Loading …" page
    // before navigation to the requested URL has actually started. Waiting only
    // for domcontentloaded is therefore racy because that temporary document is
    // already loaded. Anchor the check to the final URL and document instead.
    await popup.waitForURL(`${origin}/popup`);
    await popup.waitForFunction(
      (expectedTitle) => document.readyState !== 'loading' && document.title === expectedTitle,
      'PayPlanner popup smoke',
    );
    assert.equal(await popup.title(), 'PayPlanner popup smoke');
    assert.equal(await popup.evaluate(() => window.opener === null), true);

    const printPage = await context.newPage();
    await printPage.setContent(`
      <!doctype html>
      <meta charset="utf-8" />
      <title>PayPlanner print smoke</title>
      <main>
        <h1>PayPlanner print smoke</h1>
        <p>Browser PDF rendering verifies the print surface outside jsdom.</p>
      </main>
    `);
    const pdf = await printPage.pdf({ format: 'A4', printBackground: true });
    assert.ok(pdf.byteLength > 1_000, `Expected rendered PDF, got ${pdf.byteLength} bytes.`);

    console.log(`Browser smoke passed with ${label}: download, new-tab, print PDF.`);
  } finally {
    await browser.close();
    server.close();
    await rm(downloadDir, { recursive: true, force: true });
  }
}

await main();
