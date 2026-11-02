import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { authService } from './authService';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); session.restore(); });

const confirmations = [
  { name: 'position', path: '/v2/cases/19/position-review/checked', body: { expectedStrategy: '  Позиция\nСледующая строка  ' }, send: () => apiService.reviewCasePosition(19, '  Позиция\nСледующая строка  ') },
  { name: 'naive UTC hearing', path: '/v2/cases/19/next-hearing-confirmation/checked', body: { expectedNextHearingAt: '2099-11-03T12:00:00.1234567' }, send: () => apiService.confirmNextHearing(19, '2099-11-03T12:00:00.1234567') },
  { name: 'offset hearing', path: '/v2/cases/19/next-hearing-confirmation/checked', body: { expectedNextHearingAt: '2099-11-03T21:00:00.1234567+09:00' }, send: () => apiService.confirmNextHearing(19, '2099-11-03T21:00:00.1234567+09:00') },
];

describe('checked case confirmation transport', () => {
  it.each(confirmations)('sends the exact saved $name source only to the checked route', async ({ path, body, send }) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ confirmed: true })); vi.stubGlobal('fetch', fetch);
    await expect(send()).resolves.toEqual({ confirmed: true });
    expect(fetch).toHaveBeenCalledOnce();
    const [url, options] = fetch.mock.calls[0];
    expect(String(url).endsWith(path)).toBe(true);
    expect(options?.method).toBe('POST');
    expect(JSON.parse(String(options?.body))).toEqual(body);
  });

  it.each(confirmations.slice(0, 2).flatMap(row => [400, 401, 403, 404, 409, 503].map(status => ({ ...row, status }))))('does not retry $name or fall back after HTTP $status', async ({ path, send, status }) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ message: 'Обновите карточку дела' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(send()).rejects.toMatchObject({ status });
    if (status === 401) {
      const writes = fetch.mock.calls.filter(([url]) => String(url).includes('/v2/cases/'));
      expect(writes).toHaveLength(1);
    } else expect(fetch).toHaveBeenCalledOnce();
    expect(String(fetch.mock.calls[0][0]).endsWith(path)).toBe(true);
  });

  it.each(confirmations.slice(0, 2))('refreshes the session with the same $name expectation after an unauthorized response', async ({ path, body, send }) => {
    const refresh = vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async force => {
      if (force) session.refresh();
      return localStorage.getItem('pp_token');
    });
    const fetch = vi.fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(Response.json({ message: 'Unauthorized' }, { status: 401 }))
      .mockResolvedValueOnce(Response.json({ confirmed: true }));
    vi.stubGlobal('fetch', fetch);
    await expect(send()).resolves.toEqual({ confirmed: true });
    expect(refresh).toHaveBeenCalledWith(true);
    expect(fetch).toHaveBeenCalledTimes(2);
    for (const [url, options] of fetch.mock.calls) {
      expect(String(url).endsWith(path)).toBe(true);
      expect(options?.method).toBe('POST');
      expect(JSON.parse(String(options?.body))).toEqual(body);
    }
  });
});
