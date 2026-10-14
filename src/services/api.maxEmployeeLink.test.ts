import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('personal MAX link API', () => {
  afterEach(() => { localStorage.removeItem('pp_token'); vi.unstubAllGlobals(); });

  it('uses the authenticated session, uncached personal routes, and no recipient parameter', async () => {
    localStorage.setItem('pp_token', 'header.fixture.signature');
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(Response.json({ status: 'unlinked', channelReady: true, linkId: null, expiresAt: null, verifiedAt: null }))
      .mockResolvedValueOnce(Response.json({ linkId: 'fixture-link', deepLinkUrl: 'https://max.ru/TestBot?start=fixture', expiresAt: '2026-10-14T18:00:00Z' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', request);
    expect((await apiService.getEmployeeMaxLinkStatus()).status).toBe('unlinked');
    expect((await apiService.createEmployeeMaxLink()).linkId).toBe('fixture-link');
    await apiService.deleteEmployeeMaxLink();
    expect(request).toHaveBeenCalledTimes(3);
    for (const [index, [url, options]] of request.mock.calls.entries()) {
      expect(String(url).endsWith('/max/link')).toBe(true);
      expect(options?.method ?? 'GET').toBe(['GET', 'POST', 'DELETE'][index]);
      expect(options?.cache).toBe('no-store');
      expect(new Headers(options?.headers).get('Authorization')).toBe('Bearer header.fixture.signature');
      expect(options?.body).toBeUndefined();
    }
  });
});
