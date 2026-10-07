import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createServer, resolveConfig } from 'vite';
import { createPayPlannerViteConfig } from './vite.config';

afterEach(() => vi.unstubAllEnvs());

describe('isolated dev HTML transform', () => {
  it.each([false, true])('uses the real Vite configuration context for preview mode (%s)', async (isPreview) => {
    const env = { VITE_RESTORED_RUNTIME_ISOLATION: 'true', VITE_API_URL: 'http://127.0.0.1:5080/api', VITE_RESTORED_RUNTIME_INSTANCE_ID: 'cefe9b7e3bb64c51b3c80a9e96bb57ea' };
    Object.entries(env).forEach(([key, value]) => vi.stubEnv(key, value));
    const config = await resolveConfig({ ...createPayPlannerViteConfig(env), configFile: false },
      'serve', 'development', 'development', isPreview);
    const policy = String(config.preview.headers?.['Content-Security-Policy']);
    const scripts = policy.split(';').find(part => part.trim().startsWith('script-src'))?.trim();
    if (isPreview) {
      expect(scripts).toBe("script-src 'self'");
      expect(policy.split(';').find(part => part.trim().startsWith('connect-src'))?.trim())
        .toBe("connect-src 'self' http://127.0.0.1:5080 ws://127.0.0.1:5080");
    } else {
      expect(scripts).toContain("'sha256-");
      expect(policy).toContain('ws://127.0.0.1:5173');
    }
    expect(config.server.headers?.['Content-Security-Policy']).toBe(policy);
  });

  it('matches the exact React refresh script hash in meta and response policy without opening a listener', async () => {
    vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'true');
    vi.stubEnv('VITE_API_URL', 'http://127.0.0.1:5080/api');
    vi.stubEnv('VITE_RESTORED_RUNTIME_INSTANCE_ID', 'cefe9b7e3bb64c51b3c80a9e96bb57ea');
    const config = createPayPlannerViteConfig({ VITE_RESTORED_RUNTIME_ISOLATION: 'true', VITE_API_URL: 'http://127.0.0.1:5080/api', VITE_RESTORED_RUNTIME_INSTANCE_ID: 'cefe9b7e3bb64c51b3c80a9e96bb57ea' });
    const server = await createServer({ ...config, configFile: false, server: { ...config.server, watch: null, preTransformRequests: false } });
    try {
      expect(server.httpServer?.listening).toBe(false);
      const html = await server.transformIndexHtml('/', readFileSync('index.html', 'utf8'));
      const document = new DOMParser().parseFromString(html, 'text/html');
      const script = Array.from(document.querySelectorAll('script:not([src])'))
        .map(node => node.textContent ?? '').find(text => text.includes('injectIntoGlobalHook'));
      expect(script).toBeDefined();
      const hash = createHash('sha256').update(script!).digest('base64');
      const policy = document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.getAttribute('content');
      expect(document.head.firstElementChild?.getAttribute('http-equiv')).toBe('Content-Security-Policy');
      expect(policy).toContain(`'sha256-${hash}'`);
      expect(policy).toContain('ws://127.0.0.1:5173');
      expect(policy?.split(';').find(part => part.trim().startsWith('script-src'))).not.toContain('unsafe-inline');
      expect(server.config.server.headers?.['Content-Security-Policy']).toBe(`${policy}; frame-ancestors 'none'`);
    } finally {
      await server.close();
    }
  });
});
