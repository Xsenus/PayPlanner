// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { prepareCiBrowsers, verifyBrowser } from './prepare-ci-browsers.mjs';

const trusted = {
  COMPUTERNAME: 'XSENUS', GITHUB_REPOSITORY: 'Xsenus/PayPlanner',
  GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'push',
  GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main',
};

describe('required CI browsers', () => {
  it('requires the trusted host before any installation or launch', async () => {
    const install = vi.fn(); const probe = vi.fn();
    await expect(prepareCiBrowsers({}, 'win32', install, probe)).rejects.toThrow(/trusted/);
    expect(install).not.toHaveBeenCalled(); expect(probe).not.toHaveBeenCalled();
  });

  it('installs pinned Chromium and launches both required engines without reinstalling working Chrome', async () => {
    const calls: string[] = [];
    const result = await prepareCiBrowsers(trusted, 'win32', async (name: string) => { calls.push(`install:${name}`); },
      async (channel?: string) => { calls.push(`probe:${channel ?? 'chromium'}`); return '145.0.1.2'; });
    expect(calls).toEqual(['install:chromium', 'probe:chromium', 'probe:chrome']);
    expect(result.chromeSource).toBe('verified-existing');
  });

  it('installs unavailable Chrome once and requires a successful fresh launch', async () => {
    const install = vi.fn();
    const probe = vi.fn().mockResolvedValueOnce('145.0.1.2').mockRejectedValueOnce(new Error('missing'))
      .mockResolvedValueOnce('145.0.2.3');
    const result = await prepareCiBrowsers(trusted, 'win32', install, probe);
    expect(install.mock.calls).toEqual([['chromium'], ['chrome']]);
    expect(probe.mock.calls).toEqual([[], ['chrome'], ['chrome']]);
    expect(result).toEqual({ chromiumVersion: '145.0.1.2', chromeVersion: '145.0.2.3', chromeSource: 'installed-and-verified' });
  });

  it('fails on a download error and does not mask it with another launch or retry', async () => {
    const probe = vi.fn().mockResolvedValueOnce('145.0.1.2').mockRejectedValueOnce(new Error('missing'));
    const install = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error('download failed'));
    await expect(prepareCiBrowsers(trusted, 'win32', install, probe)).rejects.toThrow('download failed');
    expect(probe).toHaveBeenCalledTimes(2); expect(install).toHaveBeenCalledTimes(2);
  });

  it('fails when installed Chrome still cannot launch', async () => {
    const probe = vi.fn().mockResolvedValueOnce('145.0.1.2').mockRejectedValueOnce(new Error('missing'))
      .mockRejectedValueOnce(new Error('still broken'));
    await expect(prepareCiBrowsers(trusted, 'win32', vi.fn(), probe)).rejects.toThrow('still broken');
  });

  it('does not substitute Chrome for a failed pinned Chromium prerequisite', async () => {
    const install = vi.fn().mockRejectedValue(new Error('chromium failed')); const probe = vi.fn();
    await expect(prepareCiBrowsers(trusted, 'win32', install, probe)).rejects.toThrow('chromium failed');
    expect(install.mock.calls).toEqual([['chromium']]); expect(probe).not.toHaveBeenCalled();
  });

  it.each(['chromium', 'chrome'])('verifies real page execution and closes %s', async channel => {
    const page = { setContent: vi.fn(), title: vi.fn().mockResolvedValue('PayPlanner browser preflight') };
    const browser = { version: () => '145.0.1.2', newPage: vi.fn().mockResolvedValue(page), close: vi.fn() };
    const type = { launch: vi.fn().mockResolvedValue(browser) };
    await expect(verifyBrowser(channel === 'chrome' ? channel : undefined, type)).resolves.toBe('145.0.1.2');
    expect(type.launch).toHaveBeenCalledWith({ headless: true, timeout: 20000, ...(channel === 'chrome' ? { channel } : {}) });
    expect(page.setContent).toHaveBeenCalled(); expect(browser.close).toHaveBeenCalledTimes(1);
  });

  it.each(['version', 'page'])('closes the browser and rejects invalid %s proof', async failure => {
    const browser = { version: () => failure === 'version' ? 'invalid' : '145.0.1.2',
      newPage: vi.fn().mockResolvedValue({ setContent: vi.fn(), title: vi.fn().mockResolvedValue('wrong page') }), close: vi.fn() };
    await expect(verifyBrowser('chrome', { launch: vi.fn().mockResolvedValue(browser) })).rejects.toThrow(/preflight|version/);
    expect(browser.close).toHaveBeenCalledTimes(1);
  });
});
