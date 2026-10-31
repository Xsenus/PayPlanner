import { webcrypto } from 'node:crypto';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { API_BASE_URL } from './apiBaseUrl';
import { authService } from './authService';
import { bindPaymentCreateOwner } from './paymentCreateAttempts';
import { BalanceApplicationRecoveryError, type PendingBalanceApplication } from './balanceApplicationAttempts';

function login(actor = 1, epoch = 'durable-api') {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', epoch); bindPaymentCreateOwner(actor, API_BASE_URL);
}
function metadata(): PendingBalanceApplication {
  const name = Object.keys(localStorage).find(key => key.startsWith('pp.balance-application.v1:'));
  if (!name) throw new Error('Expected pending attempt'); return JSON.parse(localStorage.getItem(name)!);
}
function deferred<T>() {
  let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve };
}
beforeEach(() => {
  localStorage.clear(); sessionStorage.clear(); login();
  vi.stubGlobal('crypto', webcrypto);
  const held = new Set<string>();
  vi.stubGlobal('navigator', { locks: { request: async (name: string, _options: LockOptions, callback: (lock: Lock | null) => unknown) => {
    if (held.has(name)) return callback(null); held.add(name);
    try { return await callback({ name, mode: 'exclusive' } as Lock); } finally { held.delete(name); }
  } } });
  vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async () => localStorage.getItem('pp_token'));
});
afterEach(() => { bindPaymentCreateOwner(null, API_BASE_URL); localStorage.clear(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe.each(['client', 'legalEntity'] as const)('%s durable balance API', kind => {
  const apply = () => kind === 'client' ? apiService.applyClientBalanceGuarded(7, { paymentId: 19, amount: 25 })
    : apiService.applyLegalEntityBalanceGuarded(7, { paymentId: 19, amount: 25 });
  const owned = () => kind === 'client' ? { clientId: 7, legalEntityId: null } : { clientId: null, legalEntityId: 7 };
  const ack = (entry = metadata(), isReplay = false) => ({ requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay });
  function responder() {
    return vi.fn<typeof fetch>().mockImplementation(async (input, init) => {
      const url = String(input); const entry = metadata();
      expect(entry.actorId).toBe(1); expect(entry.apiScope).toBe(API_BASE_URL);
      expect(new Headers(init?.headers).get('Authorization')).toBe(localStorage.getItem('pp_token') && `Bearer ${localStorage.getItem('pp_token')}`);
      expect(init?.cache).toBe('no-store'); expect(init?.signal).toBeInstanceOf(AbortSignal);
      if (url.endsWith('/apply')) return Response.json(ack());
      if (url.includes(`/applications/${entry.requestId}`)) return Response.json({ requestId: entry.requestId, paymentId: 19,
        state: url.endsWith('/cancel') ? 'Cancelled' : 'Committed', result: url.endsWith('/cancel') ? null : ack(entry, true) });
      return Response.json(url.endsWith('/payments/19') ? { id: 19, ...owned(), amount: 100, paidAmount: 45 } : { ...owned(), balance: 65 });
    });
  }
  async function unknown() {
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new TypeError('lost response')); vi.stubGlobal('fetch', fetch);
    await expect(apply()).rejects.toBeInstanceOf(BalanceApplicationRecoveryError); expect(fetch).toHaveBeenCalledOnce();
    return apiService.pendingBalanceApplications()[0];
  }
  it('persists metadata before the managed POST and reads canonical totals before adoption', async () => {
    const fetch = responder(); vi.stubGlobal('fetch', fetch); const value = await apply();
    expect(fetch).toHaveBeenCalledTimes(3); const [url, init] = fetch.mock.calls[0];
    expect(String(url)).toContain(`/${kind === 'client' ? 'clients' : 'legal-entities'}/7/balance/applications/${value.requestId}/apply`);
    expect(JSON.parse(String(init?.body))).toEqual({ paymentId: 19, amount: 25, description: '', sourcePaymentId: null });
    expect(value.canonicalPayment.paidAmount).toBe(45); expect(value.operationBalance).toBe(75); expect(value.balance).toBe(65);
    expect(apiService.pendingBalanceApplication(kind, 7, 19)?.requestId).toBe(value.requestId);
    expect(apiService.canContinueBalanceApplication({ ...value })).toBe(false);
    expect(await apiService.acknowledgeBalanceApplication(value)).toBe(true); expect(apiService.pendingBalanceApplications()).toEqual([]);
  });
  it.each([404, 409, 500, 202])('preserves unknown HTTP %s and forbids a second key or legacy fallback', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ message: 'unknown' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(apply()).rejects.toBeInstanceOf(BalanceApplicationRecoveryError); const entry = metadata();
    await expect(apply()).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
    expect(metadata()).toEqual(entry); expect(fetch).toHaveBeenCalledOnce(); expect(String(fetch.mock.calls[0][0])).toContain('/applications/');
  });
  it.each([false, true])('uses the original key for explicit resolution without posting money again; cancel=%s', async cancel => {
    const entry = await unknown(); const fetch = responder(); vi.stubGlobal('fetch', fetch);
    const value = await (cancel ? apiService.cancelPendingBalanceApplication(entry) : apiService.inspectPendingBalanceApplication(entry));
    expect(value.state).toBe(cancel ? 'Cancelled' : 'Committed'); expect(value.requestId).toBe(entry.requestId);
    expect(fetch).toHaveBeenCalledTimes(cancel ? 1 : 3);
    expect(fetch.mock.calls.every(([url]) => !String(url).endsWith('/apply'))).toBe(true);
    expect(fetch.mock.calls[0][1]?.method).toBe(cancel ? 'POST' : undefined);
    if (cancel) expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({ paymentId: 19 });
    expect(await apiService.acknowledgeBalanceApplication(value)).toBe(true); expect(apiService.pendingBalanceApplications()).toEqual([]);
  });
  it('keeps an HTTP202 inspection unconfirmed without new POST', async () => {
    const entry = await unknown(); const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ requestId: entry.requestId,
      paymentId: 19, state: 'Unconfirmed', result: null }, { status: 202 })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.inspectPendingBalanceApplication(entry)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
    expect(metadata()).toEqual(entry); expect(fetch).toHaveBeenCalledOnce(); expect(fetch.mock.calls[0][1]?.method).toBeUndefined();
  });
  it('returns canonical committed data when cancellation discovers an earlier commit', async () => {
    const entry = await unknown(); const fallback = responder();
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (url, init) => String(url).endsWith('/cancel')
      ? Response.json({ requestId: entry.requestId, paymentId: 19, state: 'Committed', result: ack(entry, true) }) : fallback(url, init));
    vi.stubGlobal('fetch', fetch); const value = await apiService.cancelPendingBalanceApplication(entry);
    expect(value.state).toBe('Committed'); if (value.state !== 'Committed') throw new Error('Expected commit');
    expect(value.canonicalPayment.paidAmount).toBe(45); expect(value.isReplay).toBe(true); expect(fetch).toHaveBeenCalledTimes(3);
  });
  it.each(['inspect', 'cancel'] as const)('rejects a foreign actor before %s transport', async action => {
    const entry = await unknown(); login(2, 'actor-b'); const fetch = responder(); vi.stubGlobal('fetch', fetch);
    await expect(action === 'inspect' ? apiService.inspectPendingBalanceApplication(entry) : apiService.cancelPendingBalanceApplication(entry)).rejects.toThrow();
    expect(fetch).not.toHaveBeenCalled(); expect(apiService.pendingBalanceApplications()).toEqual([]);
    login(1, 'actor-a-again'); expect(apiService.pendingBalanceApplications()).toEqual([entry]);
  });
  it.each(['inspect', 'cancel'] as const)('does not send %s after actor switch during session preparation', async action => {
    const entry = await unknown(); const prepared = deferred<void>(); const token = deferred<string | null>();
    vi.mocked(authService.ensureFreshSession).mockImplementation(async () => { prepared.resolve(); return token.promise; });
    const fetch = responder(); vi.stubGlobal('fetch', fetch);
    const pending = action === 'inspect' ? apiService.inspectPendingBalanceApplication(entry) : apiService.cancelPendingBalanceApplication(entry);
    const rejected = expect(pending).rejects.toThrow(); await prepared.promise; login(2, 'session-switch'); token.resolve(localStorage.getItem('pp_token'));
    await rejected; expect(fetch).not.toHaveBeenCalled(); login(1, 'back'); expect(metadata()).toEqual(entry);
  });
  it('rejects a token switch before actor binding without writing metadata', async () => {
    localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '2' }))}.signature`);
    const fetch = responder(); vi.stubGlobal('fetch', fetch);
    await expect(apply()).rejects.toThrow(); expect(fetch).not.toHaveBeenCalled();
    expect(Object.keys(localStorage).some(key => key.startsWith('pp.balance-application.v1:'))).toBe(false);
  });
  it('keeps receipt and rejects late canonical response after a session ABA', async () => {
    const body = deferred<unknown>(); const entered = deferred<void>(); const fallback = responder();
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (url, init) => {
      const response = await fallback(url, init);
      if (String(url).endsWith('/payments/19')) vi.spyOn(response, 'json').mockImplementation(async () => { entered.resolve(); return body.promise; });
      return response;
    });
    vi.stubGlobal('fetch', fetch); const pending = apply(); const rejected = expect(pending).rejects.toThrow(); await entered.promise;
    const entry = metadata(); login(2, 'away'); login(1, 'back'); body.resolve({ id: 19, ...owned(), amount: 100, paidAmount: 45 });
    await rejected; expect(metadata()).toEqual(entry); expect(apiService.balanceApplicationInFlight(kind, 7, 19)).toBe(false);
  });
  it('preserves a committed attempt when the canonical API returns a foreign payment', async () => {
    const fallback = responder(); const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (url, init) => String(url).endsWith('/payments/19')
      ? Response.json({ id: 88, ...owned(), amount: 100, paidAmount: 45 }) : fallback(url, init));
    vi.stubGlobal('fetch', fetch); await expect(apply()).rejects.toThrow('подтверждён');
    expect(apiService.pendingBalanceApplications()).toHaveLength(1); expect(fetch).toHaveBeenCalledTimes(3);
  });
});
