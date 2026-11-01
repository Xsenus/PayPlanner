import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('client detail read ownership', () => {
  afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });

  it('keeps the single-client route and forwards an uncached cancellable personal-data read', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json({ id: 17, name: 'Синтетический клиент' }));
    vi.stubGlobal('fetch', fetch);
    const controller = new AbortController();
    expect(await apiService.getClient(17, { signal: controller.signal })).toMatchObject({ id: 17 });
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/clients\/17$/);
    expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
    await apiService.getClient(17);
    expect(fetch.mock.calls[1][1].signal).toBeUndefined(); expect(fetch.mock.calls[1][1].cache).toBe('no-store');
  });

  it('retains a denied personal-data response as a failure instead of a client card', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ message: 'Доступ закрыт' }, { status: 403 })));
    await expect(apiService.getClient(17)).rejects.toThrow('HTTP 403');
  });
});
