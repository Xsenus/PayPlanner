import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useClientCases } from './useClientCases';
import { useInvoices } from './useInvoices';
import { useActs } from './useActs';
import { usePaymentsPaged } from './usePaymentsPaged';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { documentCopyTestSession } from '../test/documentCopySession';
import { apiService } from '../services/api';
import { StaleUiOperationError } from '../utils/StaleUiOperationError';
import { createElement, StrictMode } from 'react';
import type { Act, ClientCase, Invoice, Payment } from '../types';

vi.mock('../services/api', () => ({ apiService: { getCasesV1: vi.fn(), getInvoices: vi.fn(), getInvoicesSummary: vi.fn(), getActs: vi.fn(), getActsSummary: vi.fn(), getPaymentsV2: vi.fn() } }));
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
function useRelated(scope: string, enabled = true, page = 1) {
  const owner = useDocumentDeliveryOwner(scope);
  return {
    owner,
    cases: useClientCases(undefined, { legalEntityId: 9, enabled }, owner),
    invoices: useInvoices({ legalEntityId: 9, enabled, page, pageSize: 5 }, owner),
    acts: useActs({ legalEntityId: 9, enabled, page, pageSize: 5 }, owner),
    payments: usePaymentsPaged({ legalEntityId: 9, page, pageSize: 5, enabled }, owner),
  };
}
const areas = ['case', 'invoice', 'invoice-summary', 'act', 'act-summary', 'payment'] as const;
type Area = typeof areas[number];
const apiFor = (area: Area) => apiService[({ case: 'getCasesV1', invoice: 'getInvoices', 'invoice-summary': 'getInvoicesSummary', act: 'getActs', 'act-summary': 'getActsSummary', payment: 'getPaymentsV2' } as const)[area]] as unknown as ReturnType<typeof vi.fn>;
const response = (area: Area, id: number) => area === 'case' ? [{ id, title: 'Visible case ' + id } as ClientCase] : area === 'invoice-summary' ? { total: { amount: id, count: 1 }, pending: { amount: id, count: 1 }, paid: { amount: 0, count: 0 }, overdue: { amount: 0, count: 0 } }
  : area === 'act-summary' ? { created: { amount: id, count: 1 }, transferred: { amount: 0, count: 0 }, signed: { amount: 0, count: 0 }, terminated: { amount: 0, count: 0 }, totalAmount: id, totalCount: 1 }
    : { items: [area === 'invoice' ? { id, number: String(id) } as Invoice : area === 'act' ? { id, number: String(id), amount: id, status: 'Created' } as Act : { id, amount: id, description: 'Payment ' + id } as Payment], total: 1, page: 1, pageSize: 5, ...(area === 'payment' ? { summary: { income: id, expense: 0, balance: id, incomeCount: 1, expenseCount: 0, total: 1 } } : {}) };
const value = (state: ReturnType<typeof useRelated>, area: Area) => area === 'case' ? state.cases.cases[0]?.id ?? 0 : area === 'invoice' ? state.invoices.invoices[0]?.id ?? 0 : area === 'act' ? state.acts.acts[0]?.id ?? 0 : area === 'invoice-summary' ? state.invoices.summary?.total.amount ?? 0 : area === 'act-summary' ? state.acts.summary?.totalAmount ?? 0 : state.payments.payments[0]?.id ?? 0;
const loading = (state: ReturnType<typeof useRelated>, area: Area) => area === 'case' ? state.cases.loading : area === 'invoice' ? state.invoices.loading : area === 'act' ? state.acts.loading : area === 'invoice-summary' ? state.invoices.summaryLoading : area === 'act-summary' ? state.acts.summaryLoading : state.payments.loading;
const errors = (state: ReturnType<typeof useRelated>) => [state.cases.error, state.invoices.error, state.acts.error, state.payments.error];
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => {
  session = documentCopyTestSession(); vi.resetAllMocks();
  vi.mocked(apiService.getCasesV1).mockResolvedValue([]);
  vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
  vi.mocked(apiService.getInvoicesSummary).mockResolvedValue({ total: { amount: 0, count: 0 }, pending: { amount: 0, count: 0 }, paid: { amount: 0, count: 0 }, overdue: { amount: 0, count: 0 } });
  vi.mocked(apiService.getActs).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
  vi.mocked(apiService.getActsSummary).mockResolvedValue({ created: { amount: 0, count: 0 }, transferred: { amount: 0, count: 0 }, signed: { amount: 0, count: 0 }, terminated: { amount: 0, count: 0 }, totalAmount: 0, totalCount: 0 });
  vi.mocked(apiService.getPaymentsV2).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5, summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 0 } });
});
afterEach(() => session.restore());

describe('related automatic read ownership', () => {
  for (const area of areas) {
    for (const outcome of ['success', 'error'] as const) it.each(['login', 'actor', 'logout'] as const)(`automatic matrix: ${area} ${outcome} cannot follow %s without render`, async change => {
      const pending = deferred<unknown>(); apiFor(area).mockReturnValue(pending.promise); const { result } = renderHook(() => useRelated('all')); await waitFor(() => expect(apiFor(area)).toHaveBeenCalledOnce()); await act(async () => undefined); session.replace(change);
      await act(async () => { if (outcome === 'success') pending.resolve(response(area, 81)); else pending.reject(new Error('Private old read failed')); }); expect(apiFor(area)).toHaveBeenCalledOnce(); expect(value(result.current, area)).toBe(0); expect(errors(result.current)).toEqual([null, null, null, null]);
    });
    it(`automatic cache: ${area} resets cached data and totals before reading its new scope`, async () => {
      apiFor(area).mockResolvedValue(response(area, 81)); const { result, rerender } = renderHook(({ scope }) => useRelated(scope), { initialProps: { scope: 'all' } }); await waitFor(() => expect(value(result.current, area)).toBe(81));
      apiFor(area).mockReturnValue(new Promise(() => undefined)); rerender({ scope: 'own' }); expect(value(result.current, area)).toBe(0); expect(loading(result.current, area)).toBe(true); expect(apiFor(area)).toHaveBeenCalledTimes(2);
      if (area === 'invoice') expect(result.current.invoices.pagination.total).toBe(0); if (area === 'act') expect(result.current.acts.pagination.total).toBe(0); if (area === 'payment') expect(result.current.payments.summary.total).toBe(0);
    });
    it.each(['success', 'error'] as const)(`automatic race: ${area} old %s cannot finish the newer read or overwrite its result`, async outcome => {
      const old = deferred<unknown>(); const next = deferred<unknown>(); apiFor(area).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
      const { result, rerender } = renderHook(({ scope }) => useRelated(scope), { initialProps: { scope: 'all' } }); await waitFor(() => expect(apiFor(area)).toHaveBeenCalledOnce()); const oldSignal = apiFor(area).mock.calls[0][1].signal as AbortSignal;
      rerender({ scope: 'own' }); expect(oldSignal.aborted).toBe(true); expect(apiFor(area)).toHaveBeenCalledTimes(2); const nextSignal = apiFor(area).mock.calls[1][1].signal as AbortSignal;
      await act(async () => { if (outcome === 'success') old.resolve(response(area, 81)); else old.reject(new Error('Private obsolete read')); }); expect(loading(result.current, area)).toBe(true); expect(nextSignal.aborted).toBe(false);
      await act(async () => next.resolve(response(area, 82))); expect(value(result.current, area)).toBe(82); expect(loading(result.current, area)).toBe(false); expect(errors(result.current)).toEqual([null, null, null, null]);
    });
    it(`automatic current: ${area} failure permits an explicit refresh with the same API filters`, async () => {
      apiFor(area).mockRejectedValueOnce(new Error('Current read refused')); const { result } = renderHook(() => useRelated('all')); await waitFor(() => expect(loading(result.current, area)).toBe(false)); apiFor(area).mockResolvedValue(response(area, 83));
      await act(async () => { if (area === 'case') await result.current.cases.refresh(); else if (area.startsWith('invoice')) await result.current.invoices.refresh(); else if (area.startsWith('act')) await result.current.acts.refresh(); else await result.current.payments.refresh(); });
      expect(value(result.current, area)).toBe(83); expect(apiFor(area)).toHaveBeenCalledTimes(2); expect(apiFor(area).mock.calls[1][0]).toMatchObject({ legalEntityId: 9 });
    });
  }
  it('automatic context: same owner rerenders and a refreshed token do not cause duplicate reads', async () => {
    const { result, rerender } = renderHook(({ scope }) => useRelated(scope), { initialProps: { scope: 'all' } }); await waitFor(() => expect(result.current.invoices.loading).toBe(false)); const before = areas.map(area => apiFor(area).mock.calls.length);
    session.refresh(); rerender({ scope: 'all' }); expect(areas.map(area => apiFor(area).mock.calls.length)).toEqual(before);
  });
  it('automatic context: ordinary pagination reloads the lists but keeps scope-independent summaries', async () => {
    const { result, rerender } = renderHook(({ page }) => useRelated('all', true, page), { initialProps: { page: 1 } }); await waitFor(() => expect(result.current.invoices.loading).toBe(false)); rerender({ page: 2 });
    expect(apiService.getInvoices).toHaveBeenCalledTimes(2); expect(apiService.getActs).toHaveBeenCalledTimes(2); expect(apiService.getPaymentsV2).toHaveBeenCalledTimes(2); expect(apiService.getCasesV1).toHaveBeenCalledOnce(); expect(apiService.getInvoicesSummary).toHaveBeenCalledOnce(); expect(apiService.getActsSummary).toHaveBeenCalledOnce();
  });
  it('automatic context: old explicit callbacks cannot abort a current read', async () => {
    const { result, rerender } = renderHook(({ scope }) => useRelated(scope), { initialProps: { scope: 'all' } }); await waitFor(() => expect(result.current.invoices.loading).toBe(false)); const old = result.current; const owner = old.owner.capture()!;
    for (const area of areas) apiFor(area).mockReturnValue(new Promise(() => undefined)); rerender({ scope: 'own' }); const before = areas.map(area => apiFor(area).mock.calls.length);
    await act(async () => { await old.cases.refresh(); for (const hook of [old.invoices, old.acts, old.payments]) await expect(hook.refreshOwned(owner.isCurrent)).rejects.toBeInstanceOf(StaleUiOperationError); });
    expect(areas.map(area => apiFor(area).mock.calls.length)).toEqual(before); for (const area of areas) expect((apiFor(area).mock.lastCall![1].signal as AbortSignal).aborted).toBe(false);
  });
  it('automatic context: disabled reads clear all cached values and send no new requests', async () => {
    for (const area of areas) apiFor(area).mockResolvedValue(response(area, 81)); const { result, rerender } = renderHook(({ enabled }) => useRelated('all', enabled), { initialProps: { enabled: true } }); await waitFor(() => expect(value(result.current, 'payment')).toBe(81)); const before = areas.map(area => apiFor(area).mock.calls.length);
    rerender({ enabled: false }); for (const area of areas) { expect(value(result.current, area)).toBe(0); expect(loading(result.current, area)).toBe(false); } expect(areas.map(area => apiFor(area).mock.calls.length)).toEqual(before);
  });
  it('automatic context: StrictMode still loads current data and rejects post-unmount results', async () => {
    for (const area of areas) apiFor(area).mockResolvedValue(response(area, 81)); const { result, unmount } = renderHook(() => useRelated('all'), { wrapper: ({ children }) => createElement(StrictMode, null, children) }); await waitFor(() => expect(value(result.current, 'invoice')).toBe(81)); for (const area of areas) expect(value(result.current, area)).toBe(81);
    const pending = deferred<unknown>(); apiFor('invoice').mockReturnValue(pending.promise); let refresh!: Promise<void>; act(() => { refresh = result.current.invoices.refresh(); }); const signal = apiFor('invoice').mock.lastCall![1].signal as AbortSignal; unmount(); expect(signal.aborted).toBe(true); await act(async () => { pending.resolve(response('invoice', 82)); await refresh; });
  });
  it('automatic owner: late invoice read never adopts another actor without rerender', async () => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.getInvoices>>>(); vi.mocked(apiService.getInvoices).mockReturnValue(pending.promise);
    const { result } = renderHook(() => useRelated('all')); await waitFor(() => expect(apiService.getInvoices).toHaveBeenCalledOnce()); session.replace('actor');
    await act(async () => pending.resolve({ items: [{ id: 81, number: 'Private old invoice' } as Invoice], total: 1, page: 1, pageSize: 5 })); expect(result.current.invoices.invoices).toEqual([]);
  });
  it('automatic owner: an already loaded case cannot remain in a new visibility scope while its read waits', async () => {
    vi.mocked(apiService.getCasesV1).mockResolvedValue([{ id: 81, title: 'Private old case' } as ClientCase]);
    const { result, rerender } = renderHook(({ scope }) => useRelated(scope), { initialProps: { scope: 'all' } }); await waitFor(() => expect(result.current.cases.cases).toHaveLength(1));
    vi.mocked(apiService.getCasesV1).mockReturnValue(new Promise(() => undefined)); rerender({ scope: 'own' }); expect(result.current.cases.cases).toEqual([]); expect(apiService.getCasesV1).toHaveBeenCalledTimes(2);
  });
});
