import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { authService } from './authService';
function login(actor = 1) {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', `balance-read-${actor}`);
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); login();
  vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async () => localStorage.getItem('pp_token')); });
afterEach(() => { localStorage.clear(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
describe.each(['client', 'legalEntity'] as const)('%s balance reads', kind => {
  describe.each([false, true])('transactions=%s', transactions => {
    const read = (signal?: AbortSignal) => kind === 'client'
      ? transactions ? apiService.getClientBalanceTransactions(12, { signal }) : apiService.getClientBalance(12, { signal })
      : transactions ? apiService.getLegalEntityBalanceTransactions(77, { signal }) : apiService.getLegalEntityBalance(77, { signal });
    const body = () => transactions ? [] : { clientId: kind === 'client' ? 12 : null, legalEntityId: kind === 'legalEntity' ? 77 : null, balance: 80 };
    it('uses the owned signal, no-store and a read-only HTTP200 request', async () => {
      const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(body())); vi.stubGlobal('fetch', fetch);
      const controller = new AbortController(); await expect(read(controller.signal)).resolves.toEqual(body());
      expect(fetch).toHaveBeenCalledOnce(); const [url, init] = fetch.mock.calls[0];
      expect(String(url)).toContain(`${kind === 'client' ? '/clients/12' : '/legal-entities/77'}/balance${transactions ? '/transactions' : ''}`);
      expect(init?.signal).toBe(controller.signal); expect(init?.cache).toBe('no-store'); expect(init?.method).toBeUndefined(); expect(init?.body).toBeUndefined();
    });
    it('does not interpret HTTP202 as a confirmed current balance', async () => {
      const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(body(), { status: 202 })); vi.stubGlobal('fetch', fetch);
      await expect(read()).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
    });
    it('does not dispatch an already aborted read', async () => {
      const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); controller.abort();
      await expect(read(controller.signal)).rejects.toHaveProperty('name', 'AbortError'); expect(fetch).not.toHaveBeenCalled();
    });
    it('rejects a different login during session preparation before transport', async () => {
      const entered = deferred<void>(); const token = deferred<string | null>();
      vi.mocked(authService.ensureFreshSession).mockImplementation(async () => { entered.resolve(); return token.promise; });
      const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
      const pending = read(); const rejected = expect(pending).rejects.toThrow(); await entered.promise; login(2); token.resolve(localStorage.getItem('pp_token'));
      await rejected; expect(fetch).not.toHaveBeenCalled();
    });
    it('rejects a late body belonging to the previous login', async () => {
      const entered = deferred<void>(); const json = deferred<unknown>(); const response = Response.json(body());
      vi.spyOn(response, 'json').mockImplementation(async () => { entered.resolve(); return json.promise; });
      vi.stubGlobal('fetch', vi.fn<typeof globalThis.fetch>().mockResolvedValue(response));
      const pending = read(); const rejected = expect(pending).rejects.toThrow(); await entered.promise; login(2); json.resolve(body()); await rejected;
    });
  });
});
