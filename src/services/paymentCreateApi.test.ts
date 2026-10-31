import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { webcrypto } from 'node:crypto';
import { apiService, getApiBaseUrl } from './api';
import { authService } from './authService';
import { acknowledgeCreatedPayment, bindPaymentCreateOwner, pendingPaymentCreates } from './paymentCreateAttempts';
import type { PaymentPayload } from '../types';

const id = '11111111-1111-4111-8111-111111111111';
const token = (actor: number, nonce = 'old') => `header.${btoa(JSON.stringify({ sub: String(actor), nonce }))}.signature`;
const payload: PaymentPayload = { date: '2026-10-26', plannedDate: '2026-10-26', amount: 1.123456,
  paidAmount: 0, type: 'Income', status: 'Pending', description: '', isPaid: false, notes: '', systemNotes: '', rescheduleCount: 0 };
const state = { attemptId: id, state: 'Draft', operation: 'primary', sellerCompanyId: null, sellerBranchId: null,
  primaryPaymentId: null, createdAtUtc: '2026-10-26T00:00:00Z', expiresAtUtc: '2026-10-27T00:00:00Z',
  canonicalPaymentId: null, identityVersion: 'payment-create-v1' };
function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
beforeEach(() => {
  vi.stubGlobal('crypto', webcrypto);
  localStorage.clear(); localStorage.setItem('pp_token', token(1)); localStorage.setItem('pp.auth-session-epoch.v1', 'A-original');
  bindPaymentCreateOwner(1, getApiBaseUrl());
  vi.stubGlobal('navigator', { locks: { request: async (_name: string, _options: LockOptions,
    callback: (lease: Lock | null) => Promise<unknown>) => callback({ name: 'owned', mode: 'exclusive' } as Lock) } });
});
afterEach(() => { vi.restoreAllMocks(); bindPaymentCreateOwner(null, getApiBaseUrl()); localStorage.clear(); vi.unstubAllGlobals(); });

describe('payment attempt actual fetch boundary', () => {
  it('checks the live token actor after session refresh, before any register/final financial fetch', async () => {
    const refreshed = deferred<string | null>();
    vi.spyOn(authService, 'ensureFreshSession').mockReturnValueOnce(refreshed.promise);
    const request = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', request);
    const saving = apiService.createPayment(payload);
    await vi.waitFor(() => expect(authService.ensureFreshSession).toHaveBeenCalledTimes(1));
    localStorage.setItem('pp_token', token(2)); localStorage.setItem('pp.auth-session-epoch.v1', 'B');
    refreshed.resolve(token(2));
    await expect(saving).rejects.toThrow('Сессия изменилась');
    expect(request).not.toHaveBeenCalled();
  });
  it('also rejects A→B→A before fetch without waiting for an AuthContext effect', async () => {
    const refreshed = deferred<string | null>();
    vi.spyOn(authService, 'ensureFreshSession').mockReturnValueOnce(refreshed.promise);
    const request = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', request);
    const saving = apiService.createPayment(payload);
    await vi.waitFor(() => expect(authService.ensureFreshSession).toHaveBeenCalledTimes(1));
    localStorage.setItem('pp_token', token(2)); localStorage.setItem('pp.auth-session-epoch.v1', 'B');
    localStorage.setItem('pp_token', token(1, 'new-login')); localStorage.setItem('pp.auth-session-epoch.v1', 'A-new');
    refreshed.resolve(token(1, 'new-login'));
    await expect(saving).rejects.toThrow('Сессия изменилась'); expect(request).not.toHaveBeenCalled();
  });
  it('source snapshot reaches the dedicated commit unchanged and stays out of registration', async () => {
    vi.spyOn(authService, 'ensureFreshSession').mockResolvedValue(token(1));
    const sourceConfirmationToken = 'remainder-v2:' + 'A'.repeat(64);
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(json({ ...state, operation: 'remainder', primaryPaymentId: 81 }, 201))
      .mockResolvedValueOnce(json({ ...payload, id: 82 }, 201)); vi.stubGlobal('fetch', request);
    const saved = await apiService.createPayment(payload, { operation: 'remainder', primaryPaymentId: 81, sourceConfirmationToken });
    expect(saved.id).toBe(82);
    expect(JSON.parse(request.mock.calls[0][1]!.body as string)).toEqual({ operation: 'remainder', primaryPaymentId: 81, sellerCompanyId: null, sellerBranchId: null });
    expect(JSON.parse(request.mock.calls[1][1]!.body as string)).toEqual({ ...payload, sourceConfirmationToken });
    expect(request.mock.calls.map(([url]) => String(url))).toEqual([
      `${getApiBaseUrl()}/payments/create-attempts`, `${getApiBaseUrl()}/payments/create-attempts/${id}/commit`,
    ]);
  });

  it('allows same-session token refresh, dedicated register+commit, and no profile/keyless request', async () => {
    vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async () => {
      localStorage.setItem('pp_token', token(1, 'refreshed')); return token(1, 'refreshed');
    });
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(json(state, 201))
      .mockResolvedValueOnce(json({ ...payload, id: 81 }, 201)); vi.stubGlobal('fetch', request);
    const saved = await apiService.createPayment(payload); expect(saved.id).toBe(81);
    expect(await acknowledgeCreatedPayment(saved)).toBe(true);
    expect(request.mock.calls.map(([url]) => String(url))).toEqual([
      `${getApiBaseUrl()}/payments/create-attempts`, `${getApiBaseUrl()}/payments/create-attempts/${id}/commit`,
    ]);
    expect(new Headers(request.mock.calls[1][1]?.headers).get('Authorization')).toBe(`Bearer ${token(1, 'refreshed')}`);
    expect(JSON.parse(request.mock.calls[1][1]!.body as string)).toEqual(payload);
  });
  it('reconciles a post-commit 500 with the same UUID, canonical GET and no fallback POST', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(json(state, 201))
      .mockResolvedValueOnce(json({ message: 'Notify failed' }, 500))
      .mockResolvedValueOnce(json({ ...state, state: 'Committed', canonicalPaymentId: 81 }))
      .mockResolvedValueOnce(json({ ...payload, id: 81 })); vi.stubGlobal('fetch', request);
    const saved = await apiService.createPayment(payload); expect(saved.id).toBe(81);
    expect(await acknowledgeCreatedPayment(saved)).toBe(true);
    expect(request.mock.calls.map(([url]) => String(url).replace(getApiBaseUrl(), ''))).toEqual([
      '/payments/create-attempts', `/payments/create-attempts/${id}/commit`, `/payments/create-attempts/${id}`, '/payments/81',
    ]);
    expect(request.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(2);
    expect(request.mock.calls[2][1]?.cache).toBe('no-store');
    expect(request.mock.calls[3][1]?.cache).toBe('no-store');
    expect(pendingPaymentCreates()).toEqual([]);
  });
  it.each(['replacement', 'logout', 'A-B-A'] as const)('does not retarget an ordinary PUT after non-forced refresh: %s', async (change) => {
    const expired = `header.${btoa(JSON.stringify({ sub: '1', exp: 1 }))}.signature`;
    localStorage.setItem('pp_token', expired);
    const refreshed = deferred<Response>();
    const request = vi.fn<typeof fetch>().mockReturnValueOnce(refreshed.promise); vi.stubGlobal('fetch', request);
    const pending = apiService.updatePayment(81, payload); const rejected = expect(pending).rejects.toThrow('Сессия изменилась');
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    if (change === 'logout') authService.logout();
    else {
      await authService.completeSignIn(token(2), { id: 2 } as Awaited<ReturnType<typeof authService.getCurrentUser>>);
      if (change === 'A-B-A') await authService.completeSignIn(token(1, 'new-login'), { id: 1 } as Awaited<ReturnType<typeof authService.getCurrentUser>>);
    }
    refreshed.resolve(json({ token: token(1, 'old-refresh') })); await rejected;
    expect(request.mock.calls.map(([url]) => String(url))).toEqual([`${getApiBaseUrl()}/auth/refresh`]);
  });
  it('does not retry an ordinary 401 PUT as a replacement login after forced refresh', async () => {
    const refreshed = deferred<Response>();
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(json({ message: 'expired' }, 401)).mockReturnValueOnce(refreshed.promise);
    vi.stubGlobal('fetch', request);
    const pending = apiService.updatePayment(81, payload); const rejected = expect(pending).rejects.toThrow('Сессия изменилась');
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(2));
    await authService.completeSignIn(token(2), { id: 2 } as Awaited<ReturnType<typeof authService.getCurrentUser>>);
    refreshed.resolve(json({ token: token(1, 'old-refresh') })); await rejected;
    expect(request.mock.calls.filter(([url]) => String(url).endsWith('/payments/81'))).toHaveLength(1);
    expect(localStorage.getItem('pp_token')).toBe(token(2));
  });
});
