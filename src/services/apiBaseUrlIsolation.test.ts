import { describe, expect, it } from 'vitest';
import { resolveApiBaseUrl } from './apiBaseUrl';
import { parseRestoredRuntimeMode } from '../utils/restoredRuntimeSafety';

describe('isolated restored API selection', () => {
  it.each(['http://127.0.0.1:5080/api', 'https://[::1]:5080/api', 'http://127.0.0.2:5080/api'])('accepts only a configured loopback API: %s', configuredUrl => {
    expect(resolveApiBaseUrl({ restoredRuntime: true, configuredUrl, isProduction: true, pageProtocol: 'http:' })).toBe(configuredUrl);
  });

  it.each([
    '', 'https://api.elitpravo.com/api', 'http://localhost:5080/api', 'http://0.0.0.0:5080/api',
    'http://127.0.0.1.evil.invalid/api', 'http://user:fixture-secret@127.0.0.1:5080/api',
    'http://127.0.0.1:5080/api?token=fixture-secret', 'http://127.0.0.1:5080/api#fixture-secret',
    'http://127.0.0.1:5080/other', 'ftp://127.0.0.1/api', '/api',
  ])('rejects unsafe or missing configuration without production fallback: %s', configuredUrl => {
    expect(() => resolveApiBaseUrl({ restoredRuntime: true, configuredUrl, isProduction: true, pageProtocol: 'http:' })).toThrow();
    try {
      resolveApiBaseUrl({ restoredRuntime: true, configuredUrl, isProduction: true, pageProtocol: 'http:' });
    } catch (error) {
      expect(String(error)).not.toContain('fixture-secret');
    }
  });

  it('rejects mixed content instead of silently selecting the working API', () => {
    expect(() => resolveApiBaseUrl({ restoredRuntime: true, configuredUrl: 'http://127.0.0.1:5080/api', isProduction: true, pageProtocol: 'https:' })).toThrow('HTTPS');
  });

  it('normalizes a trailing slash', () => {
    expect(resolveApiBaseUrl({ restoredRuntime: true, configuredUrl: ' http://127.0.0.1:5080/api/ ', pageProtocol: 'http:' })).toBe('http://127.0.0.1:5080/api');
  });

  it('preserves normal production mixed-content behavior outside the opt-in mode', () => {
    expect(resolveApiBaseUrl({ restoredRuntime: false, configuredUrl: 'http://localhost:5080/api', isProduction: true, pageProtocol: 'https:' })).toBe('https://api.elitpravo.com/api');
  });

  it.each([undefined, '', 'false'])('keeps isolation disabled by default: %s', value => {
    expect(parseRestoredRuntimeMode(value)).toBe(false);
  });
  it('enables only the explicit true setting and fails closed on a typo', () => {
    expect(parseRestoredRuntimeMode('true')).toBe(true);
    expect(() => parseRestoredRuntimeMode('tru')).toThrow();
  });
});
