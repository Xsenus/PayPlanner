import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('saved company requisites check transport', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('uses the existing read route without caching and with cancellation', async () => {
    const value = { legalEntityId: 5, issues: [] };
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request); const controller = new AbortController();
    await expect(apiService.getCompanyRequisitesCheck(5, controller.signal)).resolves.toEqual(value);
    expect(request).toHaveBeenCalledOnce(); const [url, init] = request.mock.calls[0];
    expect(String(url)).toMatch(/\/legal-entities\/5\/requisites-check$/);
    expect(init?.method ?? 'GET').toBe('GET'); expect(init?.cache).toBe('no-store'); expect(init?.signal).toBe(controller.signal); expect(init?.body).toBeUndefined();
  });
  it('propagates cancellation without a retry', async () => {
    const controller = new AbortController();
    let start!: () => void; const started = new Promise<void>(resolve => { start = resolve; });
    const request = vi.fn<typeof fetch>().mockImplementation((_url, init) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
      start();
    }));
    vi.stubGlobal('fetch', request); const pending = apiService.getCompanyRequisitesCheck(5, controller.signal);
    await started; controller.abort(); await expect(pending).rejects.toHaveProperty('name', 'AbortError'); expect(request).toHaveBeenCalledOnce();
  });
  it('cancels before fetch without starting a network request', async () => {
    const request = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', request); const controller = new AbortController(); controller.abort();
    await expect(apiService.getCompanyRequisitesCheck(5, controller.signal)).rejects.toHaveProperty('name', 'AbortError'); expect(request).not.toHaveBeenCalled();
  });
  it('retains a readable refusal from the existing API', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({message:'Компания недоступна'}), {status:404,headers:{'Content-Type':'application/json'}})));
    await expect(apiService.getCompanyRequisitesCheck(5)).rejects.toThrow('Компания недоступна');
  });
});
