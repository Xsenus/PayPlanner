import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { authService } from './authService';

function login(actor = 7) {
  localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', `invoice-financial-read-${actor}`);
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
const params = { linkedInvoiceId: 910, page: 2, pageSize: 20 };
const body = { items: [], total: 0, page: 2, pageSize: 20, summary: {} };
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); login(); vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async () => localStorage.getItem('pp_token')); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); localStorage.clear(); });

describe('invoice money read HTTP contract', () => {
  it('forwards the owned signal, exact canonical/page query and currency/no-store contract without a write', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(body)); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await expect(apiService.getMoneyOperationsPaged(params, { signal: controller.signal, cache: 'no-store', expectedStatus: 200 })).resolves.toEqual(body);
    expect(fetch).toHaveBeenCalledOnce(); const [url, init] = fetch.mock.calls[0];
    const query = new URL(String(url), 'https://test.local').searchParams;
    expect([...query.entries()]).toEqual([['linkedInvoiceId', '910'], ['page', '2'], ['pageSize', '20']]);
    expect(init?.signal).toBe(controller.signal); expect(init?.cache).toBe('no-store'); expect(init?.method).toBeUndefined(); expect(init?.body).toBeUndefined();
    expect((init?.headers as Record<string, string>)['X-PayPlanner-Currency-Contract']).toBe('1');
  });
  it('preserves the existing no-options caller and currency header', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(body)); vi.stubGlobal('fetch', fetch);
    await expect(apiService.getMoneyOperationsPaged(params)).resolves.toEqual(body); expect(fetch).toHaveBeenCalledOnce();
    expect(fetch.mock.calls[0][1]?.cache).toBeUndefined(); expect((fetch.mock.calls[0][1]?.headers as Record<string, string>)['X-PayPlanner-Currency-Contract']).toBe('1');
  });
  it('rejects an unconfirmed HTTP202 without trying a broader read', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(body, { status: 202 })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.getMoneyOperationsPaged(params, { expectedStatus: 200 })).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
  it('does not dispatch an already aborted read', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); controller.abort();
    await expect(apiService.getMoneyOperationsPaged(params, { signal: controller.signal })).rejects.toHaveProperty('name', 'AbortError'); expect(fetch).not.toHaveBeenCalled();
  });
  it('rejects a replacement login while preparing transport', async () => {
    const entered = deferred<void>(); const token = deferred<string | null>(); vi.mocked(authService.ensureFreshSession).mockImplementation(async () => { entered.resolve(); return token.promise; });
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const pending = apiService.getMoneyOperationsPaged(params, { expectedStatus: 200 });
    const rejected = expect(pending).rejects.toThrow(); await entered.promise; login(8); token.resolve(localStorage.getItem('pp_token')); await rejected; expect(fetch).not.toHaveBeenCalled();
  });
  it('rejects a late JSON body from a previous login', async () => {
    const entered = deferred<void>(); const json = deferred<typeof body>(); const response = Response.json(body);
    vi.spyOn(response, 'json').mockImplementation(async () => { entered.resolve(); return json.promise; }); vi.stubGlobal('fetch', vi.fn<typeof globalThis.fetch>().mockResolvedValue(response));
    const pending = apiService.getMoneyOperationsPaged(params, { expectedStatus: 200 }); const rejected = expect(pending).rejects.toThrow();
    await entered.promise; login(8); json.resolve(body); await rejected;
  });
});
