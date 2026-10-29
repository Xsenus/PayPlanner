import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { authService } from './authService';

const flags = { includeLogo: false, includeSeal: true, includeSignature: false };
describe('explicit HTML print preparation transport', () => {
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); localStorage.removeItem('pp.auth-session-epoch.v1'); localStorage.removeItem('pp_token'); });
  it.each(['invoices', 'contracts'] as const)('uses a noncached POST for %s and preserves independent false flags', async section => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response('<html>prepared</html>'));
    vi.stubGlobal('fetch', request);
    const cancellation = new AbortController();
    const blob = await apiService.prepareDocumentPrint(section, 7, { ...flags, templateKey: 'invoice-offer', signal: cancellation.signal });
    expect(await blob.text()).toBe('<html>prepared</html>');
    expect(request).toHaveBeenCalledTimes(1);
    const [target, options] = request.mock.calls[0];
    const url = new URL(String(target), 'https://local.invalid');
    expect(url.pathname).toMatch(new RegExp(`/${section}/7/print$`));
    expect(Object.fromEntries(url.searchParams)).toEqual({ templateKey: 'invoice-offer', includeLogo: 'false', includeSeal: 'true', includeSignature: 'false' });
    expect(options?.method).toBe('POST'); expect(options?.cache).toBe('no-store');
    expect(options?.signal).toBe(cancellation.signal); expect(options?.body).toBeUndefined();
  });
  it.each([400, 403, 409])('preserves a readable preparation refusal %s without retrying', async status => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ message: 'Обновите карточку' }), { status, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.prepareDocumentPrint('invoices', 7, flags)).rejects.toThrow('Обновите карточку');
    expect(request).toHaveBeenCalledTimes(1);
  });
  it('rejects an already cancelled action before sending a preparation request', async () => {
    const request = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', request);
    const cancellation = new AbortController(); cancellation.abort();
    await expect(apiService.prepareDocumentPrint('contracts', 7, { ...flags, signal: cancellation.signal })).rejects.toMatchObject({ name: 'AbortError' });
    expect(request).not.toHaveBeenCalled();
  });
  it.each(['headers', 'body'] as const)('does not adopt a former session response at %s', async phase => {
    const token = `header.${btoa(JSON.stringify({ sub: '7', exp: Math.floor(Date.now() / 1000) + 86400 }))}.signature`;
    localStorage.setItem('pp_token', token);
    vi.spyOn(authService, 'ensureFreshSession').mockResolvedValue(token);
    const request = vi.fn<typeof fetch>().mockImplementation(async () => {
      if (phase === 'headers') localStorage.setItem('pp.auth-session-epoch.v1', 'new-session');
      return { ok: true, status: 200, blob: async () => {
        localStorage.setItem('pp.auth-session-epoch.v1', 'new-session'); return new Blob(['former private document']);
      } } as Response;
    });
    vi.stubGlobal('fetch', request);
    localStorage.setItem('pp.auth-session-epoch.v1', 'old-session');
    await expect(apiService.prepareDocumentPrint('invoices', 7, flags)).rejects.toThrow('Сессия изменилась');
    expect(request).toHaveBeenCalledTimes(1);
  });
  it('does not return a prepared copy cancelled while its body is read', async () => {
    const cancellation = new AbortController();
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue({ ok: true, status: 200, blob: async () => {
      cancellation.abort(); return new Blob(['late cancelled copy']);
    } } as Response));
    await expect(apiService.prepareDocumentPrint('contracts', 7, { ...flags, signal: cancellation.signal })).rejects.toMatchObject({ name: 'AbortError' });
  });
});
