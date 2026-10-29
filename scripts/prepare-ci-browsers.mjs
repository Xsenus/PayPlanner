import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { requireTrustedHost } from './verify-xsenus-runner.mjs';

const require = createRequire(import.meta.url);

export async function verifyBrowser(channel, browserType = chromium) {
  const browser = await browserType.launch({ headless: true, timeout: 20000, ...(channel ? { channel } : {}) });
  try {
    const version = browser.version();
    if (!/^\d+\.\d+\.\d+\.\d+$/.test(version)) throw new Error('Browser returned an invalid version');
    const page = await browser.newPage();
    await page.setContent('<title>PayPlanner browser preflight</title>');
    if (await page.title() !== 'PayPlanner browser preflight') throw new Error('Browser page preflight failed');
    return version;
  } finally {
    await browser.close();
  }
}

function installBrowser(name) {
  const cli = join(dirname(require.resolve('playwright/package.json')), 'cli.js');
  const result = spawnSync(process.execPath, [cli, 'install', name], {
    stdio: 'inherit', windowsHide: true, timeout: 10 * 60 * 1000,
  });
  if (result.error || result.status !== 0) throw new Error(`Required ${name} installation failed`);
}

export async function prepareCiBrowsers(env = process.env, platform = process.platform,
  install = installBrowser, probe = verifyBrowser) {
  requireTrustedHost(env, platform);
  // Chromium is versioned by the lockfile's Playwright package. Chrome is a
  // system channel: Playwright's CI installer downloads its MSI on every call,
  // even when a working installation already exists. Verify that installation
  // with the same channel used by the real tests before deciding to install it.
  await install('chromium');
  const chromiumVersion = await probe();
  let chromeVersion;
  let chromeSource = 'verified-existing';
  try {
    chromeVersion = await probe('chrome');
  } catch {
    await install('chrome');
    chromeSource = 'installed-and-verified';
    chromeVersion = await probe('chrome');
  }
  return { chromiumVersion, chromeVersion, chromeSource };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    console.log(JSON.stringify(await prepareCiBrowsers()));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
