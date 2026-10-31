import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { API_BASE_URL } from './apiBaseUrl';
import { authService } from './authService';
import { bindPaymentCreateOwner, paymentCreateOwner, type PaymentCreateOwner } from './paymentCreateAttempts';
import { parseManagedBalanceApplicationResult, prepareBalanceApplicationCommand } from './balanceApplicationInspection';
import type { BalanceApplicationCommand } from '../types';

const requestId = '114a38a7-bf68-4ec7-bd34-7dd217101337';
const otherId = '2a4faebd-0710-4e44-b861-6d2930dd3e78';
const result = (isReplay = false) => ({ requestId, appliedAmount: 25, balance: 75, isReplay });
const command = (): BalanceApplicationCommand => ({ paymentId: 19, amount: 25 });
function login(actor = 1, epoch = 'managed-first', scope = API_BASE_URL) {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', epoch); bindPaymentCreateOwner(actor, scope);
  return paymentCreateOwner();
}
function deferred<T>() {
  let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve };
}
beforeEach(() => {
  localStorage.clear(); sessionStorage.clear(); login();
  vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async () => localStorage.getItem('pp_token'));
});
afterEach(() => { bindPaymentCreateOwner(null, API_BASE_URL); localStorage.clear(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe.each(['client', 'legalEntity'] as const)('%s managed balance application transport', kind => {
  const apply = (actor: PaymentCreateOwner, payload = command(), signal?: AbortSignal) => kind === 'client'
    ? apiService.applyClientBalanceManaged(7, requestId, payload, actor, signal)
    : apiService.applyLegalEntityBalanceManaged(7, requestId, payload, actor, signal);

  it.each([false, true])('uses only the new route and accepts its exact ACK; replay=%s', async replay => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ ...result(replay), privateField: 'ignored' }));
    vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    const before = JSON.stringify({ ...localStorage });
    await expect(apply(paymentCreateOwner(), { ...command(), amount: 25.56789, description: '  исходное  ', sourcePaymentId: 8 }, controller.signal)).resolves.toEqual(result(replay));
    expect(fetch).toHaveBeenCalledOnce(); const [url, init] = fetch.mock.calls[0];
    expect(String(url)).toMatch(new RegExp(`/${kind === 'client' ? 'clients' : 'legal-entities'}/7/balance/applications/${requestId}/apply$`));
    expect(init?.method).toBe('POST'); expect(init?.cache).toBe('no-store'); expect(init?.signal).toBe(controller.signal);
    expect(JSON.parse(String(init?.body))).toEqual({ paymentId: 19, amount: 25.56789, description: 'исходное', sourcePaymentId: 8 });
    expect(JSON.stringify({ ...localStorage })).toBe(before);
  });
  it.each([403, 404, 409, 500, 202, 204])('never falls back to legacy Apply after HTTP %s', async status => {
    const response = status === 204 ? new Response(null, { status }) : Response.json({ message: 'Недоступно', ...result() }, { status });
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(response); vi.stubGlobal('fetch', fetch);
    await expect(apply(paymentCreateOwner())).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
    expect(String(fetch.mock.calls[0][0])).toContain(`/applications/${requestId}/apply`);
  });
  it('retains the same key and command through a same-actor 401 refresh', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(Response.json({ message: 'refresh' }, { status: 401 }))
      .mockResolvedValueOnce(Response.json(result(true)));
    vi.stubGlobal('fetch', fetch); await expect(apply(paymentCreateOwner())).resolves.toEqual(result(true));
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch.mock.calls[0][0]).toBe(fetch.mock.calls[1][0]);
    expect(fetch.mock.calls[0][1]?.body).toBe(fetch.mock.calls[1][1]?.body);
  });
  it('never replays a 401 under a different actor', async () => {
    const actor = paymentCreateOwner(); const oldToken = localStorage.getItem('pp_token');
    vi.mocked(authService.ensureFreshSession).mockResolvedValueOnce(oldToken).mockImplementationOnce(async () => {
      login(2, 'managed-401-switch'); return localStorage.getItem('pp_token');
    });
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ message: 'refresh' }, { status: 401 }));
    vi.stubGlobal('fetch', fetch); await expect(apply(actor)).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
  it('rejects a legacy-looking ACK without its key instead of retrying money', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ appliedAmount: 25, balance: 75 }));
    vi.stubGlobal('fetch', fetch); await expect(apply(paymentCreateOwner())).rejects.toThrow('не подтверждён'); expect(fetch).toHaveBeenCalledOnce();
  });
  it('rejects a wrong receipt key without any secondary request', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ ...result(), requestId: otherId }));
    vi.stubGlobal('fetch', fetch); await expect(apply(paymentCreateOwner())).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
  it('fences a changed actor before dispatch', async () => {
    const actor = paymentCreateOwner(); login(2, 'managed-second');
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    await expect(apply(actor)).rejects.toThrow(); expect(fetch).not.toHaveBeenCalled();
  });
  it('fences a wrong API scope even if that owner is currently bound', async () => {
    const actor = login(1, 'wrong-api', '/another-api');
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    await expect(apply(actor)).rejects.toThrow(); expect(fetch).not.toHaveBeenCalled();
  });
  it('does not dispatch after an actor switch during session preparation', async () => {
    const actor = paymentCreateOwner(); const token = deferred<string | null>(); const entered = deferred<void>();
    vi.mocked(authService.ensureFreshSession).mockImplementation(async () => { entered.resolve(); return token.promise; });
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    const pending = apply(actor); await entered.promise; login(2, 'managed-refresh-switch'); token.resolve(localStorage.getItem('pp_token'));
    await expect(pending).rejects.toThrow(); expect(fetch).not.toHaveBeenCalled();
  });
  it('rejects an owner generation ABA while the response body is pending', async () => {
    const actor = paymentCreateOwner(); const body = deferred<unknown>(); const entered = deferred<void>();
    const response = Response.json(result()); vi.spyOn(response, 'json').mockImplementation(async () => { entered.resolve(); return body.promise; });
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(response); vi.stubGlobal('fetch', fetch);
    const pending = apply(actor); await entered.promise;
    bindPaymentCreateOwner(null, API_BASE_URL); bindPaymentCreateOwner(1, API_BASE_URL); body.resolve(result());
    await expect(pending).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
  it('does not dispatch invalid money or an already aborted command', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    await expect(apply(paymentCreateOwner(), { ...command(), amount: NaN })).rejects.toThrow();
    const controller = new AbortController(); controller.abort();
    await expect(apply(paymentCreateOwner(), command(), controller.signal)).rejects.toHaveProperty('name', 'AbortError'); expect(fetch).not.toHaveBeenCalled();
  });
  it('snapshots only known scalar fields and reads a mutable getter once', async () => {
    const id = vi.fn().mockReturnValueOnce(19).mockReturnValue(88);
    const payload = { ...command(), paidAmount: 999, token: 'private', requestId: otherId };
    Object.defineProperty(payload, 'paymentId', { get: id });
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(result())); vi.stubGlobal('fetch', fetch);
    await apply(paymentCreateOwner(), payload); expect(id).toHaveBeenCalledOnce();
    expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({ paymentId: 19, amount: 25, description: '', sourcePaymentId: null });
  });
});

describe('managed balance command and ACK boundaries', () => {
  it.each([
    { paymentId: 0 }, { paymentId: 2147483648 }, { amount: 0 }, { amount: -1 }, { amount: NaN }, { amount: Infinity },
    { amount: '25' }, { description: {} }, { description: 'x'.repeat(501) }, { sourcePaymentId: 0 },
    { sourcePaymentId: 19 }, { sourcePaymentId: 1.5 },
  ])('rejects invalid command %j before serialization', invalid => {
    expect(() => prepareBalanceApplicationCommand({ ...command(), ...invalid } as BalanceApplicationCommand)).toThrow();
  });
  it('keeps full precision, trims descriptions and freezes the canonical command', () => {
    const value = prepareBalanceApplicationCommand({ ...command(), amount: 25.56789, description: '  исходное  ' });
    expect(value).toEqual({ paymentId: 19, amount: 25.56789, description: 'исходное', sourcePaymentId: null });
    expect(Object.isFrozen(value)).toBe(true);
  });
  it.each([
    null, [], {}, { appliedAmount: 25, balance: 75 }, { ...result(), requestId: otherId },
    { ...result(), isReplay: 'false' }, { ...result(), appliedAmount: 0 }, { ...result(), appliedAmount: NaN },
    { ...result(), balance: Infinity }, { ...result(), balance: '75' },
  ])('rejects an ambiguous ACK %j', value => {
    expect(() => parseManagedBalanceApplicationResult(value, requestId)).toThrow();
  });
});
