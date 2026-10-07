import { describe, expect, it } from 'vitest';
import { createRestoredBrowserPolicy } from './restoredBrowserPolicy';

describe('restored browser resource policy', () => {
  it.each([
    ['http://127.0.0.1:5080/api', 'http://127.0.0.1:5080', 'ws://127.0.0.1:5080'],
    ['https://[::1]:5080/api', 'https://[::1]:5080', 'wss://[::1]:5080'],
  ])('uses exact local API and websocket origins: %s', (api, origin, websocket) => {
    const policy = createRestoredBrowserPolicy(api);
    expect(policy).toContain(`connect-src 'self' ${origin} ${websocket}`);
    expect(policy).toContain(`img-src 'self' data: blob: ${origin}`);
    expect(policy).toContain("script-src 'self'");
    expect(policy).toContain("object-src 'none'");
    expect(policy).not.toContain('*');
    expect(policy).not.toContain('frame-ancestors'); // This directive belongs to the HTTP header, not meta.
  });
  it('rejects a working API instead of adding it to the policy', () => {
    expect(() => createRestoredBrowserPolicy('https://api.elitpravo.com/api')).toThrow();
  });
  it('allows only a supplied SHA-256 digest and an exact local HMR endpoint', () => {
    const policy = createRestoredBrowserPolicy('http://127.0.0.1:5080/api', {
      inlineScriptHashes: ['A'.repeat(43) + '='], devWebSocketUrl: 'ws://127.0.0.1:5173',
    });
    expect(policy).toContain(`'sha256-${'A'.repeat(43)}='`);
    expect(policy).toContain('ws://127.0.0.1:5173');
    expect(policy.split(';').find(part => part.trim().startsWith('script-src'))).not.toContain('unsafe-inline');
  });
  it('rejects unsafe hashes and external HMR', () => {
    expect(() => createRestoredBrowserPolicy('http://127.0.0.1:5080/api', { inlineScriptHashes: ["'; *"] })).toThrow();
    expect(() => createRestoredBrowserPolicy('http://127.0.0.1:5080/api', { devWebSocketUrl: 'wss://fixture.invalid' })).toThrow();
  });
});
