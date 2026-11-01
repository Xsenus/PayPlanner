import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';

afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });
it('client POA read forwards cancellation and bypasses caches with its existing route', async () => {
  const fetch = vi.fn().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch);
  const controller = new AbortController(); await apiService.getClientPowersOfAttorney(17, { signal: controller.signal });
  expect(String(fetch.mock.calls[0][0])).toMatch(/\/clients\/17\/powers-of-attorney$/);
  expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  await apiService.getClientPowersOfAttorney(18);
  expect(String(fetch.mock.calls[1][0])).toMatch(/\/clients\/18\/powers-of-attorney$/);
  expect(fetch.mock.calls[1][1].signal).toBeUndefined(); expect(fetch.mock.calls[1][1].cache).toBe('no-store');
});
