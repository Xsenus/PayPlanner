import { StrictMode } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../services/api';
import type { ClientBalanceSummary, MoneyOperation, MoneyOperationsSummary } from '../types';
import { documentCopyTestSession } from '../test/documentCopySession';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { useInvoiceFinancialReads } from './useInvoiceFinancialReads';

vi.mock('../services/api', () => ({ apiService: { getClientBalance: vi.fn(), getLegalEntityBalance: vi.fn(), getMoneyOperationsPaged: vi.fn() } }));
type Props = { context: string; balanceEnabled: boolean; movementsEnabled: boolean; clientId: number | null; legalEntityId: number | null; invoiceId: number | null };
const defaults: Props = { context: 'window-1-role-4', balanceEnabled: true, movementsEnabled: true, clientId: 1, legalEntityId: null, invoiceId: 910 };
const operation = (id = 701, linkedInvoiceId = 910): MoneyOperation => ({ id, linkedInvoiceId, amount: 25, currency: 'RUB', source: 'Bank', direction: 'Income', accountingType: 'Fact',
  date: '2026-11-02', createdAt: '2026-11-02T00:00:00Z', isAdvancePayment: false, purpose: `Движение ${id}` });
const page = (items = [operation()], total = items.length, current = 1) => ({ items, total, page: current, pageSize: 20, summary: {} as MoneyOperationsSummary });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function setup(initial: Partial<Props> = {}) { return renderHook((p: Props) => { const owner = useDocumentDeliveryOwner(p.context); return { ...useInvoiceFinancialReads({ ...p, owner }), owner }; }, { initialProps: { ...defaults, ...initial } }); }
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => {
  vi.resetAllMocks(); session = documentCopyTestSession();
  vi.mocked(apiService.getClientBalance).mockImplementation(async clientId => ({ clientId, balance: 100 }));
  vi.mocked(apiService.getLegalEntityBalance).mockImplementation(async legalEntityId => ({ legalEntityId, balance: 200 }));
  vi.mocked(apiService.getMoneyOperationsPaged).mockImplementation(async params => page([operation(702, params?.linkedInvoiceId)]));
});
afterEach(() => session.restore());

describe('invoice financial owned reads', () => {
  it('loads independent client balance and one canonical movement page with owned read-only options', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.balance?.balance).toBe(100));
    await waitFor(() => expect(view.result.current.movements[0]?.id).toBe(702));
    expect(apiService.getClientBalance).toHaveBeenCalledExactlyOnceWith(1, { signal: expect.any(AbortSignal) });
    expect(apiService.getLegalEntityBalance).not.toHaveBeenCalled();
    expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledExactlyOnceWith({ linkedInvoiceId: 910, page: 1, pageSize: 20 }, { signal: expect.any(AbortSignal), cache: 'no-store', expectedStatus: 200 });
  });
  it('keeps independent reads lazy and avoids dictionary/token-refresh rereads', async () => {
    const view = setup({ balanceEnabled: false, movementsEnabled: false }); await act(async () => {});
    expect(apiService.getClientBalance).not.toHaveBeenCalled(); expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled();
    view.rerender(defaults); await waitFor(() => expect(view.result.current.movements).toHaveLength(1));
    act(() => { session.refresh(); window.dispatchEvent(new Event('dicts:changed')); }); view.rerender({ ...defaults }); await act(async () => {});
    expect(apiService.getClientBalance).toHaveBeenCalledOnce(); expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce();
  });
  for (const kind of ['balance', 'movements'] as const) {
    it.each(['document', 'role', 'disabled', 'login', 'actor', 'logout', 'unmount'].flatMap(change => ['success', 'error'].map(outcome => ({ change, outcome }))))(`${kind} late $outcome cannot follow $change`, async ({ change, outcome }) => {
      const pending = deferred<unknown>(); const get = kind === 'balance' ? apiService.getClientBalance : apiService.getMoneyOperationsPaged;
      vi.mocked(get).mockReturnValueOnce(pending.promise as never);
      const view = setup(); await waitFor(() => expect(get).toHaveBeenCalledOnce()); const previous = view.result.current;
      const signal = kind === 'balance' ? vi.mocked(apiService.getClientBalance).mock.calls[0][1]?.signal : vi.mocked(apiService.getMoneyOperationsPaged).mock.calls[0][1]?.signal;
      if (change === 'login') session.replace();
      if (change === 'actor') session.replace('actor');
      if (change === 'logout') session.replace('logout');
      if (change === 'unmount') view.unmount(); else view.rerender({ ...defaults,
        ...(change === 'document' ? { context: 'window-2', invoiceId: 920, clientId: 2 } : {}),
        ...(change === 'role' ? { context: 'window-1-role-8' } : {}),
        ...(change === 'disabled' ? { balanceEnabled: false, movementsEnabled: false } : {}) });
      const count = vi.mocked(get).mock.calls.length;
      await act(async () => { await previous.retryBalance(); await previous.retryMovements(); if (outcome === 'error') pending.reject(new Error('Чужая старая ошибка')); else pending.resolve(kind === 'balance' ? { clientId: 1, balance: 99999 } : page([operation(99999)])); });
      expect(signal?.aborted).toBe(true); expect(get).toHaveBeenCalledTimes(count);
      if (change !== 'unmount') { expect(view.result.current.balance?.balance).not.toBe(99999); expect(view.result.current.movements.map(row => row.id)).not.toContain(99999);
        expect(view.result.current.balanceError).not.toBe('Чужая старая ошибка'); expect(view.result.current.movementsError).not.toBe('Чужая старая ошибка'); }
    });
  }
  it('switches a client balance to a legal entity with the same numeric ID without accepting the old client response', async () => {
    const pending = deferred<ClientBalanceSummary>(); vi.mocked(apiService.getClientBalance).mockReturnValueOnce(pending.promise);
    const view = setup(); await waitFor(() => expect(apiService.getClientBalance).toHaveBeenCalledOnce());
    view.rerender({ ...defaults, clientId: null, legalEntityId: 1 }); await waitFor(() => expect(view.result.current.balance?.legalEntityId).toBe(1));
    await act(async () => pending.resolve({ clientId: 1, balance: 99999 })); expect(view.result.current.balance).toEqual({ legalEntityId: 1, balance: 200 });
  });
  it('never dispatches financial reads from an unauthenticated owner', async () => {
    session.replace('logout'); const view = setup(); await act(async () => {});
    expect(view.result.current.balance).toBeNull(); expect(view.result.current.movements).toEqual([]);
    await act(async () => { await view.result.current.retryBalance(); await view.result.current.retryMovements(); });
    expect(apiService.getClientBalance).not.toHaveBeenCalled(); expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled();
  });
  it('single-flights explicit retries and never uses the former successful snapshot during a failed refresh', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.movements).toHaveLength(1));
    const summary = deferred<ClientBalanceSummary>(); const movements = deferred<ReturnType<typeof page>>();
    vi.mocked(apiService.getClientBalance).mockReturnValueOnce(summary.promise); vi.mocked(apiService.getMoneyOperationsPaged).mockReturnValueOnce(movements.promise);
    let balanceA!: Promise<void>; let balanceB!: Promise<void>; let moneyA!: Promise<void>; let moneyB!: Promise<void>;
    act(() => { balanceA = view.result.current.retryBalance(); balanceB = view.result.current.retryBalance(); moneyA = view.result.current.retryMovements(); moneyB = view.result.current.retryMovements(); });
    expect(balanceA).toBe(balanceB); expect(moneyA).toBe(moneyB); expect(view.result.current.balance).toBeNull(); expect(view.result.current.movements).toEqual([]);
    await act(async () => { summary.reject(new Error('HTTP 403 - баланс')); movements.reject(new Error('HTTP 404 - движения')); await Promise.all([balanceA, moneyA]); });
    expect(view.result.current.balance).toBeNull(); expect(view.result.current.movements).toEqual([]); expect(view.result.current.balanceError).toBe('Недостаточно прав для выполнения действия.'); expect(view.result.current.movementsError).toBeTruthy();
    expect(apiService.getClientBalance).toHaveBeenCalledTimes(2); expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledTimes(2);
  });
  it('cancels both reads before the parent closes and refuses old cancellation after a new window opens', async () => {
    const summary = deferred<ClientBalanceSummary>(); const movements = deferred<ReturnType<typeof page>>();
    vi.mocked(apiService.getClientBalance).mockReturnValueOnce(summary.promise); vi.mocked(apiService.getMoneyOperationsPaged).mockReturnValueOnce(movements.promise);
    const view = setup(); await waitFor(() => expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce()); const previous = view.result.current;
    const oldSignal = vi.mocked(apiService.getMoneyOperationsPaged).mock.calls[0][1]?.signal;
    act(() => { previous.cancel(); previous.owner.invalidate(); }); expect(oldSignal?.aborted).toBe(true);
    view.rerender({ ...defaults, context: 'window-2' }); await waitFor(() => expect(view.result.current.movements).toHaveLength(1));
    const signal = vi.mocked(apiService.getMoneyOperationsPaged).mock.lastCall![1]?.signal;
    act(() => previous.cancel()); expect(signal?.aborted).toBe(false);
    await act(async () => { summary.resolve({ clientId: 1, balance: 999 }); movements.resolve(page([operation(999)])); }); expect(view.result.current.movements[0].id).toBe(702);
  });
  it.each([
    { name: 'wrong owner', value: { clientId: 2, balance: 5 } }, { name: 'opposite owner', value: { clientId: 1, legalEntityId: 2, balance: 5 } },
    { name: 'string balance', value: { clientId: 1, balance: '5' } }, { name: 'infinity', value: { clientId: 1, balance: Infinity } },
    { name: 'missing balance', value: { clientId: 1 } }, { name: 'array', value: [] },
  ])('rejects an unconfirmed balance: $name', async ({ value }) => {
    vi.mocked(apiService.getClientBalance).mockResolvedValueOnce(value as ClientBalanceSummary); const view = setup();
    await waitFor(() => expect(view.result.current.balanceError).not.toBeNull()); expect(view.result.current.balance).toBeNull(); expect(apiService.getClientBalance).toHaveBeenCalledOnce();
  });
  it.each([{ legalEntityId: 2, balance: 5 }, { legalEntityId: 1, clientId: 2, balance: 5 }])('rejects a foreign legal-entity summary %j without a client fallback', async value => {
    vi.mocked(apiService.getLegalEntityBalance).mockResolvedValueOnce(value); const view = setup({ clientId: null, legalEntityId: 1 });
    await waitFor(() => expect(view.result.current.balanceError).not.toBeNull()); expect(view.result.current.balance).toBeNull(); expect(apiService.getClientBalance).not.toHaveBeenCalled();
  });
  it.each([0, -10.5])('keeps the confirmed signed balance %s without replacing it by zero or a missing response', async balance => {
    vi.mocked(apiService.getClientBalance).mockResolvedValueOnce({ clientId: 1, balance }); const view = setup();
    await waitFor(() => expect(view.result.current.balance?.balance).toBe(balance)); expect(view.result.current.balanceError).toBeNull();
  });
  it('uses financial error messages for non-Error rejections', async () => {
    vi.mocked(apiService.getClientBalance).mockRejectedValueOnce('network'); vi.mocked(apiService.getMoneyOperationsPaged).mockRejectedValueOnce(null);
    const view = setup(); await waitFor(() => expect(view.result.current.movementsError).not.toBeNull());
    expect(view.result.current.balanceError).toContain('баланс контрагента'); expect(view.result.current.movementsError).toContain('движения денег');
  });
  it.each([
    { name: 'foreign invoice', value: page([operation(701, 911)]) }, { name: 'duplicate', value: page([operation(), operation()]) },
    { name: 'truncated page', value: page([operation()], 21) }, { name: 'wrong page', value: page([operation()], 1, 2) },
    { name: 'wrong page size', value: { ...page(), pageSize: 50 } }, { name: 'missing total', value: { ...page(), total: undefined } },
    { name: 'overflow total', value: { ...page(), total: Number.MAX_SAFE_INTEGER + 1 } },
    { name: 'invalid amount', value: page([{ ...operation(), amount: NaN }]) },
    { name: 'wrong source', value: page([{ ...operation(), source: 'unknown' } as unknown as MoneyOperation]) },
    { name: 'invalid date', value: page([{ ...operation(), date: 'invalid' }]) },
    { name: 'object currency', value: page([{ ...operation(), currency: {} } as MoneyOperation]) },
    { name: 'object purpose', value: page([{ ...operation(), purpose: {} } as MoneyOperation]) },
    { name: 'array direction', value: page([{ ...operation(), direction: ['Income'] } as unknown as MoneyOperation]) },
    { name: 'array source', value: page([{ ...operation(), source: ['Bank'] } as unknown as MoneyOperation]) },
    { name: 'negative real operation ID', value: page([{ ...operation(), id: -701 }]) },
    { name: 'mismatched readonly payment ID', value: page([{ ...operation(), id: -701, isReadonly: true, relatedPaymentId: 702 }]) },
  ])('rejects unconfirmed movements: $name', async ({ value }) => {
    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce(value as never); const view = setup();
    await waitFor(() => expect(view.result.current.movementsError).not.toBeNull()); expect(view.result.current.movements).toEqual([]); expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce();
  });
  it('preserves a signed amount, unknown currency and independent payment/operation IDs without recalculation', async () => {
    const rows = [{ ...operation(), amount: -25.75, currency: null }, { ...operation(), id: -701, relatedPaymentId: 701, currency: 'usd', isReadonly: true }];
    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce(page(rows)); const view = setup(); await waitFor(() => expect(view.result.current.movements).toHaveLength(2));
    expect(view.result.current.movements).toBe(rows); expect(view.result.current.movements[0].amount).toBe(-25.75);
  });
  it.each([0, -1, 1.5])('never dispatches invalid IDs %s', async value => {
    const view = setup({ clientId: value, invoiceId: value }); await act(async () => {}); expect(view.result.current.balance).toBeNull();
    expect(apiService.getClientBalance).not.toHaveBeenCalled(); expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled();
  });
  it('keeps the current page explicit, handles shrinking totals and resets pagination for another invoice', async () => {
    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce(page(Array.from({ length: 20 }, (_, i) => operation(701 + i)), 21))
      .mockResolvedValueOnce(page([operation(721)], 21, 2)).mockResolvedValueOnce(page([], 0, 2));
    const view = setup(); await waitFor(() => expect(view.result.current.hasNext).toBe(true)); act(() => view.result.current.movePage(1));
    await waitFor(() => expect(view.result.current.movements[0]?.id).toBe(721)); expect(view.result.current.page).toBe(2);
    await act(async () => view.result.current.retryMovements()); expect(view.result.current.movements).toEqual([]); expect(view.result.current.movementsError).toBeNull();
    act(() => view.result.current.movePage(-1)); await waitFor(() => expect(view.result.current.page).toBe(1));
    view.rerender({ ...defaults, context: 'another-invoice', invoiceId: 920 }); await waitFor(() => expect(view.result.current.movements[0]?.linkedInvoiceId).toBe(920)); expect(view.result.current.page).toBe(1);
  });
  it('does not advance using a cached total after the current page is denied', async () => {
    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce(page(Array.from({ length: 20 }, (_, i) => operation(701 + i)), 21)).mockRejectedValueOnce(new Error('HTTP 403 - denied'));
    const view = setup(); await waitFor(() => expect(view.result.current.hasNext).toBe(true));
    await act(async () => view.result.current.retryMovements()); act(() => view.result.current.movePage(1));
    expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledTimes(2); expect(view.result.current.page).toBe(1); expect(view.result.current.movements).toEqual([]);
  });
  it('directly resets page two when another invoice opens and ignores its former navigation callbacks', async () => {
    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce(page(Array.from({ length: 20 }, (_, i) => operation(701 + i)), 21))
      .mockResolvedValueOnce(page([operation(721)], 21, 2));
    const view = setup(); await waitFor(() => expect(view.result.current.hasNext).toBe(true)); act(() => view.result.current.movePage(1));
    await waitFor(() => expect(view.result.current.movements[0]?.id).toBe(721)); expect(view.result.current.page).toBe(2); const old = view.result.current;
    view.rerender({ ...defaults, context: 'direct-new-invoice', invoiceId: 920 });
    await waitFor(() => expect(view.result.current.movements[0]?.linkedInvoiceId).toBe(920)); expect(view.result.current.page).toBe(1);
    const calls = vi.mocked(apiService.getMoneyOperationsPaged).mock.calls.length;
    await act(async () => { old.movePage(-1); await old.retryMovements(); }); expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledTimes(calls); expect(view.result.current.page).toBe(1);
  });
  it('StrictMode keeps only current pending reads and cleans their signals on unmount', async () => {
    const pending = deferred<ReturnType<typeof page>>(); vi.mocked(apiService.getMoneyOperationsPaged).mockReturnValue(pending.promise);
    const view = renderHook(() => { const owner = useDocumentDeliveryOwner('strict'); return useInvoiceFinancialReads({ ...defaults, owner }); }, { wrapper: StrictMode });
    await waitFor(() => expect(view.result.current.balance?.balance).toBe(100)); const signal = vi.mocked(apiService.getMoneyOperationsPaged).mock.lastCall![1]?.signal;
    expect(signal?.aborted).toBe(false); view.unmount(); expect(signal?.aborted).toBe(true); await act(async () => pending.resolve(page()));
  });
});
