import { StrictMode } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useClientBalanceReads } from './useClientBalanceReads';
import { apiService } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';
import type { ClientBalanceSummary, ClientBalanceTransaction } from '../types';

vi.mock('../services/api', () => ({ apiService: { getClientBalance: vi.fn(), getClientBalanceTransactions: vi.fn() } }));
type Props = { id: number; enabled: boolean; ownerScope: string; historyOpen: boolean };
const defaults: Props = { id: 1, enabled: true, ownerScope: 'client-1-role-1', historyOpen: false };
const transaction = (id = 11, clientId = 1): ClientBalanceTransaction => ({ id, clientId, amount: 25, type: 'Overpayment', direction: 'Credit', description: `Операция ${id}`, createdAt: '2026-11-02T00:00:00Z' });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function setup(initial: Partial<Props> = {}) { return renderHook((p: Props) => useClientBalanceReads(p.id, p), { initialProps: { ...defaults, ...initial } }); }
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getClientBalance).mockImplementation(async id => ({ clientId: id, balance: 100 })); vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValue([]); });
afterEach(() => session.restore());

describe('client balance owned reads', () => {
  it('loads one summary with cancellation and waits for its confirmation before an opened journal', async () => {
    const pending = deferred<ClientBalanceSummary>(); vi.mocked(apiService.getClientBalance).mockReturnValueOnce(pending.promise);
    const view = setup({ historyOpen: true }); await waitFor(() => expect(apiService.getClientBalance).toHaveBeenCalledOnce());
    expect(apiService.getClientBalanceTransactions).not.toHaveBeenCalled(); expect(view.result.current.transactionsLoading).toBe(true);
    await act(async () => pending.resolve({ clientId: 1, balance: 0 })); await waitFor(() => expect(view.result.current.transactionsLoading).toBe(false));
    expect(apiService.getClientBalance).toHaveBeenCalledExactlyOnceWith(1, { signal: expect.any(AbortSignal) });
    expect(apiService.getClientBalanceTransactions).toHaveBeenCalledExactlyOnceWith(1, { signal: expect.any(AbortSignal) }); expect(view.result.current.balance?.balance).toBe(0);
  });
  it('keeps history lazy and ignores dictionary events and same-session token refresh', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.balance?.balance).toBe(100));
    act(() => { window.dispatchEvent(new Event('dicts:changed')); session.refresh(); }); view.rerender({ ...defaults });
    await act(async () => {}); expect(apiService.getClientBalance).toHaveBeenCalledOnce(); expect(apiService.getClientBalanceTransactions).not.toHaveBeenCalled();
  });
  it.each(['success', 'error'] as const)('a late initial summary %s cannot replace a newer refresh', async outcome => {
    const old = deferred<ClientBalanceSummary>(); vi.mocked(apiService.getClientBalance).mockReturnValueOnce(old.promise);
    const view = setup(); await waitFor(() => expect(apiService.getClientBalance).toHaveBeenCalledOnce()); const signal = vi.mocked(apiService.getClientBalance).mock.calls[0][1]?.signal;
    await act(async () => view.result.current.refreshBalance()); expect(view.result.current.balance?.balance).toBe(100);
    await act(async () => { if (outcome === 'success') old.resolve({ clientId: 1, balance: 999 }); else old.reject(new Error('Старая ошибка')); });
    expect(signal?.aborted).toBe(true); expect(view.result.current.balance?.balance).toBe(100); expect(view.result.current.balanceError).toBeNull();
  });
  it.each(['success', 'error'] as const)('a closed journal rejects its old %s and callbacks after reopen', async outcome => {
    const old = deferred<ClientBalanceTransaction[]>(); vi.mocked(apiService.getClientBalanceTransactions).mockReturnValueOnce(old.promise).mockResolvedValue([transaction(12)]);
    const view = setup({ historyOpen: true }); await waitFor(() => expect(apiService.getClientBalanceTransactions).toHaveBeenCalledOnce());
    const previous = view.result.current; const signal = vi.mocked(apiService.getClientBalanceTransactions).mock.calls[0][1]?.signal;
    view.rerender({ ...defaults }); view.rerender({ ...defaults, historyOpen: true }); await waitFor(() => expect(view.result.current.transactions[0]?.id).toBe(12));
    const count = vi.mocked(apiService.getClientBalanceTransactions).mock.calls.length;
    await act(async () => { await previous.refreshTransactions(); await previous.retryTransactions(); if (outcome === 'success') old.resolve([transaction(77)]); else old.reject(new Error('Старая история')); });
    expect(previous.captureHistory()).toBeNull(); expect(signal?.aborted).toBe(true); expect(apiService.getClientBalanceTransactions).toHaveBeenCalledTimes(count);
    expect(view.result.current.transactions.map(row => row.id)).toEqual([12]); expect(view.result.current.transactionsError).toBeNull(); expect(apiService.getClientBalance).toHaveBeenCalledOnce();
  });
  for (const kind of ['summary', 'history'] as const) for (const outcome of ['success', 'error'] as const) {
    it.each(['client', 'scope', 'readonly', 'login', 'actor', 'logout', 'unmount'] as const)(`${kind} pending ${outcome} cannot follow %s`, async change => {
      const pending = deferred<unknown>();
      if (kind === 'summary') vi.mocked(apiService.getClientBalance).mockReturnValueOnce(pending.promise as Promise<ClientBalanceSummary>);
      else vi.mocked(apiService.getClientBalanceTransactions).mockReturnValueOnce(pending.promise as Promise<ClientBalanceTransaction[]>);
      const view = setup({ historyOpen: kind === 'history' }); const get = kind === 'summary' ? apiService.getClientBalance : apiService.getClientBalanceTransactions;
      await waitFor(() => expect(get).toHaveBeenCalledOnce()); const previous = view.result.current;
      if (change === 'client') view.rerender({ ...defaults, id: 2, historyOpen: kind === 'history' });
      if (change === 'scope') view.rerender({ ...defaults, ownerScope: 'role-2', historyOpen: kind === 'history' });
      if (change === 'readonly') view.rerender({ ...defaults, enabled: false, historyOpen: kind === 'history' });
      if (change === 'unmount') view.unmount();
      if (change === 'login' || change === 'actor' || change === 'logout') session.replace(change);
      await act(async () => { if (outcome === 'success') pending.resolve(kind === 'summary' ? { clientId: 1, balance: 999 } : [transaction(77)]); else pending.reject(new Error('Старый отказ')); });
      const count = vi.mocked(get).mock.calls.length;
      await act(async () => { if (kind === 'summary') await previous.refreshBalance(); else await previous.retryTransactions(); }); expect(get).toHaveBeenCalledTimes(count);
      if (change !== 'unmount') { expect(view.result.current.balance?.balance).not.toBe(999); expect(view.result.current.transactions.some(row => row.id === 77)).toBe(false); }
    });
  }
  it('an old explicit summary refresh cannot cancel a new scope request', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.balance?.balance).toBe(100)); const previous = view.result.current;
    const next = deferred<ClientBalanceSummary>(); vi.mocked(apiService.getClientBalance).mockReturnValueOnce(next.promise);
    view.rerender({ ...defaults, ownerScope: 'role-2' }); await waitFor(() => expect(apiService.getClientBalance).toHaveBeenCalledTimes(2));
    const signal = vi.mocked(apiService.getClientBalance).mock.calls[1][1]?.signal;
    await act(async () => previous.refreshBalance()); expect(signal?.aborted).toBe(false); expect(apiService.getClientBalance).toHaveBeenCalledTimes(2);
    await act(async () => next.resolve({ clientId: 1, balance: 200 })); expect(view.result.current.balance?.balance).toBe(200);
  });
  it.each([401, 403])('a summary HTTP%s stops history until an explicit retry succeeds', async status => {
    vi.mocked(apiService.getClientBalance).mockRejectedValueOnce(new Error(`HTTP ${status} - denied`)); const view = setup({ historyOpen: true });
    await waitFor(() => expect(view.result.current.balanceError).not.toBeNull()); expect(view.result.current.balance).toBeNull(); expect(apiService.getClientBalanceTransactions).not.toHaveBeenCalled();
    await act(async () => { window.dispatchEvent(new Event('dicts:changed')); }); expect(apiService.getClientBalance).toHaveBeenCalledOnce();
    await act(async () => view.result.current.retryTransactions()); await waitFor(() => expect(view.result.current.transactionsLoading).toBe(false));
    expect(view.result.current.balance?.balance).toBe(100); expect(apiService.getClientBalance).toHaveBeenCalledTimes(2); expect(apiService.getClientBalanceTransactions).toHaveBeenCalledOnce();
  });
  it.each([401, 403])('a journal HTTP%s removes the cached summary even when the window closes', async status => {
    vi.mocked(apiService.getClientBalanceTransactions).mockRejectedValueOnce(new Error(`HTTP ${status} - denied`)); const view = setup({ historyOpen: true });
    await waitFor(() => expect(view.result.current.balanceError).not.toBeNull()); expect(view.result.current.balance).toBeNull();
    const retry = view.result.current.retryTransactions; view.rerender({ ...defaults }); expect(view.result.current.balance).toBeNull();
    await act(async () => retry()); expect(apiService.getClientBalance).toHaveBeenCalledOnce(); expect(apiService.getClientBalanceTransactions).toHaveBeenCalledOnce();
    await act(async () => view.result.current.refreshBalance()); expect(view.result.current.balance?.balance).toBe(100);
  });
  it('a transient summary error hides the prior value and allows an explicit retry', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.balance?.balance).toBe(100));
    vi.mocked(apiService.getClientBalance).mockRejectedValueOnce(new Error('offline'));
    await act(async () => view.result.current.refreshBalance()); expect(view.result.current.balance).toBeNull(); expect(view.result.current.balanceError).toBe('offline');
    await act(async () => view.result.current.refreshBalance()); expect(view.result.current.balance?.balance).toBe(100);
  });
  it('a transient history failure does not fabricate an empty successful journal', async () => {
    vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValueOnce([transaction()]); const view = setup({ historyOpen: true });
    await waitFor(() => expect(view.result.current.transactions).toHaveLength(1)); vi.mocked(apiService.getClientBalanceTransactions).mockRejectedValueOnce(new Error('offline'));
    await act(async () => view.result.current.refreshTransactions()); expect(view.result.current.transactionsError).toBe('offline'); expect(view.result.current.transactions).toEqual([]);
    expect(view.result.current.balance?.balance).toBe(100); await act(async () => view.result.current.retryTransactions()); expect(view.result.current.transactionsError).toBeNull();
    expect(apiService.getClientBalance).toHaveBeenCalledOnce();
  });
  it.each([
    { name: 'null', value: null }, { name: 'array', value: [] }, { name: 'foreign client', value: { clientId: 2, balance: 100 } },
    { name: 'missing client', value: { balance: 100 } }, { name: 'string amount', value: { clientId: 1, balance: '100' } },
    { name: 'NaN', value: { clientId: 1, balance: NaN } }, { name: 'infinity', value: { clientId: 1, balance: Infinity } },
  ])('invalid summary $name never supplies a balance or opens a history request', async ({ value }) => {
    vi.mocked(apiService.getClientBalance).mockResolvedValueOnce(value as ClientBalanceSummary); const view = setup({ historyOpen: true });
    await waitFor(() => expect(view.result.current.balanceError).not.toBeNull()); expect(view.result.current.balance).toBeNull(); expect(apiService.getClientBalanceTransactions).not.toHaveBeenCalled();
  });
  it.each([
    { name: 'object', value: {} }, { name: 'foreign client', value: [transaction(11, 2)] }, { name: 'duplicate IDs', value: [transaction(), transaction()] },
    { name: 'string amount', value: [{ ...transaction(), amount: '25' }] }, { name: 'infinity', value: [{ ...transaction(), amount: Infinity }] },
    { name: 'direction', value: [{ ...transaction(), direction: 'other' }] }, { name: 'date', value: [{ ...transaction(), createdAt: 'invalid' }] },
    { name: 'description', value: [{ ...transaction(), description: {} }] }, { name: 'type', value: [{ ...transaction(), type: '' }] },
    { name: 'ID', value: [{ ...transaction(), id: 0 }] }, { name: 'link', value: [{ ...transaction(), paymentId: '7' }] },
  ])('invalid journal $name never enters the rendered snapshot', async ({ value }) => {
    vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValueOnce(value as ClientBalanceTransaction[]); const view = setup({ historyOpen: true });
    await waitFor(() => expect(view.result.current.transactionsError).not.toBeNull()); expect(view.result.current.transactions).toEqual([]);
  });
  it.each([0, -50.25, 1234567.89])('keeps the valid server amount %s without new calculations', async balance => {
    vi.mocked(apiService.getClientBalance).mockResolvedValueOnce({ clientId: 1, balance }); const view = setup();
    await waitFor(() => expect(view.result.current.balance).not.toBeNull()); expect(view.result.current.balance?.balance).toBe(balance);
  });
  it('preserves server ordering, signed amounts and an unknown future transaction label', async () => {
    const rows = [{ ...transaction(12), amount: -2.5, type: 'FutureAdjustment', paymentId: 0 }, transaction(11)] as unknown as ClientBalanceTransaction[];
    vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValueOnce(rows); const view = setup({ historyOpen: true });
    await waitFor(() => expect(view.result.current.transactions).toHaveLength(2)); expect(view.result.current.transactions).toBe(rows);
  });
  it.each([0, -1, 1.5])('never requests an invalid client ID %s', async id => { const view = setup({ id, historyOpen: true }); await act(async () => {}); expect(apiService.getClientBalance).not.toHaveBeenCalled(); expect(view.result.current.capture()).toBeNull(); });
  it('StrictMode accepts only its live summary and leaves no history request when closed', async () => {
    const view = renderHook(() => useClientBalanceReads(1, { enabled: true, ownerScope: 'strict', historyOpen: false }), { wrapper: StrictMode });
    await waitFor(() => expect(view.result.current.balance?.balance).toBe(100)); expect(apiService.getClientBalanceTransactions).not.toHaveBeenCalled(); view.unmount();
  });
});
