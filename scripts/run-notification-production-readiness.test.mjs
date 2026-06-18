import { describe, expect, it } from 'vitest';

import {
  DEFAULT_API_URL,
  DEFAULT_FRONTEND_URL,
  buildConfig,
  buildHubUrl,
  buildUrl,
  checkWebSocketUpgrade,
  parseArgs,
  parseHttpStatusLine,
  readBoolean,
  readPositiveInt,
  trimTrailingSlash,
} from './run-notification-production-readiness.mjs';

describe('run-notification-production-readiness configuration', () => {
  it('parses dashed and equals cli arguments', () => {
    expect(parseArgs(['--api-url=https://api.example', '--timeout-ms', '30000'])).toEqual({
      apiUrl: 'https://api.example',
      timeoutMs: '30000',
    });
  });

  it('builds defaults for production domains', () => {
    expect(buildConfig({})).toMatchObject({
      apiUrl: DEFAULT_API_URL,
      frontendUrl: DEFAULT_FRONTEND_URL,
      timeoutMs: 15_000,
      requireWebSocket: false,
      allowHttp: false,
    });
  });

  it('requires HTTPS unless explicitly allowed for local dry runs', () => {
    expect(() => buildConfig({ apiUrl: 'http://localhost:5080' })).toThrow(/HTTPS/);
    expect(buildConfig({
      apiUrl: 'http://localhost:5080/',
      frontendUrl: 'http://localhost:5173/',
      allowHttp: 'true',
    })).toMatchObject({
      apiUrl: 'http://localhost:5080',
      frontendUrl: 'http://localhost:5173',
      allowHttp: true,
    });
  });

  it('normalizes URLs and hub access tokens', () => {
    expect(trimTrailingSlash('https://crm.example///')).toBe('https://crm.example');
    expect(buildUrl('https://api.example/', '/api/health')).toBe('https://api.example/api/health');

    const hubUrl = buildHubUrl('https://api.example', '/hubs/notifications', 'token value');

    expect(hubUrl.pathname).toBe('/hubs/notifications');
    expect(hubUrl.searchParams.get('access_token')).toBe('token value');
  });

  it('reads boolean and positive integer values predictably', () => {
    expect(readBoolean('true')).toBe(true);
    expect(readBoolean('0')).toBe(false);
    expect(readPositiveInt('10', 5)).toBe(10);
    expect(readPositiveInt('', 5)).toBe(5);
    expect(() => readPositiveInt('0', 5)).toThrow(/positive integer/);
  });

  it('parses HTTP status lines from raw WebSocket upgrade responses', () => {
    expect(parseHttpStatusLine('HTTP/1.1 101 Switching Protocols')).toEqual({
      statusCode: 101,
      statusText: 'Switching Protocols',
    });
    expect(parseHttpStatusLine('HTTP/1.1 401 Unauthorized')).toEqual({
      statusCode: 401,
      statusText: 'Unauthorized',
    });
    expect(() => parseHttpStatusLine('not http')).toThrow(/Invalid HTTP status/);
  });

  it('fails required WebSocket checks before production deploy when token is missing', async () => {
    await expect(checkWebSocketUpgrade({
      apiUrl: DEFAULT_API_URL,
      frontendUrl: DEFAULT_FRONTEND_URL,
      timeoutMs: 100,
      token: '',
      requireWebSocket: true,
      allowHttp: false,
    }, '/hubs/notifications')).rejects.toThrow(/requires --token/);
  });
});
