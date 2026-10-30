import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useClientCases } from './useClientCases';
import { useInvoices } from './useInvoices';
import { useActs } from './useActs';
import { usePaymentsPaged } from './usePaymentsPaged';
import { apiService } from '../services/api';
import { StaleUiOperationError } from '../utils/StaleUiOperationError';
import type { ActInput, ClientCase, InvoiceInput } from '../types';

vi.mock('../services/api', () => ({ apiService: {
  getCasesV1: vi.fn(), createCase: vi.fn(), updateCase: vi.fn(), deleteCase: vi.fn(),
  getInvoices: vi.fn(), getInvoicesSummary: vi.fn(), createInvoice: vi.fn(), updateInvoice: vi.fn(), deleteInvoice: vi.fn(),
  getActs: vi.fn(), getActsSummary: vi.fn(), createAct: vi.fn(), updateAct: vi.fn(), deleteAct: vi.fn(), getPaymentsV2: vi.fn(),
} }));
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const caseRow = (id = 1) => ({ id, legalEntityId: 9, clientId: null, title: `Owned case ${id}`, status: 'Open', createdAt: '2026-10-31' } as ClientCase);
const casePayload = { legalEntityId: 9, clientId: null, title: 'Owned case', status: 'Open' };
const invoicePayload = { legalEntityId: 9, type: 'Income', number: 'Owned invoice' } as InvoiceInput;
const actPayload = { legalEntityId: 9, number: 'Owned act', date: '2026-10-31', amount: 215.5, status: 'Created' } as ActInput;
function useAllRelated(company = 9) {
  const cases = useClientCases(undefined, { legalEntityId: company });
  const invoices = useInvoices({ legalEntityId: company });
  const acts = useActs({ legalEntityId: company });
  const payments = usePaymentsPaged({ legalEntityId: company, page: 1, pageSize: 5 });
  return { cases, invoices, acts, payments };
}
type HookState = ReturnType<typeof useAllRelated>;
type Kind = 'case' | 'invoice' | 'act';
type Action = 'create' | 'update' | 'delete';
function mutate(state: HookState, kind: Kind, action: Action, current?: () => boolean): Promise<unknown> {
  if (kind === 'case') return action === 'create' ? state.cases.createCase(casePayload, current) : action === 'update' ? state.cases.updateCase(1, casePayload, current) : state.cases.deleteCase(1, current);
  if (kind === 'invoice') return action === 'create' ? state.invoices.createInvoice(invoicePayload, current) : action === 'update' ? state.invoices.updateInvoice(1, invoicePayload, current) : state.invoices.deleteInvoice(1, current);
  return action === 'create' ? state.acts.createAct(actPayload, current) : action === 'update' ? state.acts.updateAct(1, actPayload, current) : state.acts.deleteAct(1, current);
}
const apiWrite = (kind: Kind, action: Action) => apiService[`${action}${kind[0].toUpperCase()}${kind.slice(1)}` as 'createCase'] as unknown as ReturnType<typeof vi.fn>;
const readCounts = () => [apiService.getCasesV1, apiService.getInvoices, apiService.getInvoicesSummary, apiService.getActs, apiService.getActsSummary, apiService.getPaymentsV2].map(fn => vi.mocked(fn).mock.calls.length);
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(apiService.getCasesV1).mockResolvedValue([]);
  vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
  vi.mocked(apiService.getInvoicesSummary).mockResolvedValue({ total: { amount: 0, count: 0 }, pending: { amount: 0, count: 0 }, paid: { amount: 0, count: 0 }, overdue: { amount: 0, count: 0 } });
  vi.mocked(apiService.getActs).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
  vi.mocked(apiService.getActsSummary).mockResolvedValue({ created: { amount: 0, count: 0 }, transferred: { amount: 0, count: 0 }, signed: { amount: 0, count: 0 }, terminated: { amount: 0, count: 0 }, totalAmount: 0, totalCount: 0 });
  vi.mocked(apiService.getPaymentsV2).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5, summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 0 } });
});

describe('related mutation hook ownership', () => {
  it('owned hooks microtask: revocation between the helper and its caller prevents case adoption', async () => {
    let current = true; vi.mocked(apiService.createCase).mockImplementation(async () => { queueMicrotask(() => queueMicrotask(() => { current = false; })); return caseRow(); });
    const { result } = renderHook(() => useClientCases(undefined, { legalEntityId: 9 })); await waitFor(() => expect(result.current.loading).toBe(false));
    await act(async () => { await expect(result.current.createCase(casePayload, () => current)).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(result.current.cases).toEqual([]);
  });
  it.each(['update', 'delete'] as const)('owned hooks microtask: late %s revocation cannot change the accepted case list', async action => {
    let current = true; vi.mocked(apiService.getCasesV1).mockResolvedValue([caseRow()]); apiWrite('case', action).mockImplementation(async () => { queueMicrotask(() => queueMicrotask(() => { current = false; })); return action === 'delete' ? undefined : { ...caseRow(), title: 'Private unaccepted update' }; });
    const { result } = renderHook(() => useClientCases(undefined, { legalEntityId: 9 })); await waitFor(() => expect(result.current.loading).toBe(false));
    await act(async () => { await expect(action === 'update' ? result.current.updateCase(1, casePayload, () => current) : result.current.deleteCase(1, () => current)).rejects.toBeInstanceOf(StaleUiOperationError); });
    expect(result.current.cases).toEqual([caseRow()]);
  });
  it('owned hooks: late case creation cannot enter a revoked caller list', async () => {
    const pending = deferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValue(pending.promise); let current = true;
    const { result } = renderHook(() => useClientCases(undefined, { legalEntityId: 9 })); await waitFor(() => expect(result.current.loading).toBe(false));
    let saved!: Promise<ClientCase>; act(() => { saved = result.current.createCase(casePayload, () => current); }); current = false;
    await act(async () => { pending.resolve(caseRow()); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(result.current.cases).toEqual([]);
  });
  it('owned hooks: late invoice creation never refreshes a revoked caller', async () => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.createInvoice>>>(); vi.mocked(apiService.createInvoice).mockReturnValue(pending.promise); let current = true;
    const { result } = renderHook(() => useInvoices({ legalEntityId: 9 })); await waitFor(() => expect(result.current.loading).toBe(false)); const reads = vi.mocked(apiService.getInvoices).mock.calls.length;
    let saved!: Promise<unknown>; act(() => { saved = result.current.createInvoice(invoicePayload, () => current); }); current = false;
    await act(async () => { pending.resolve({ id: 81 } as Awaited<ReturnType<typeof apiService.createInvoice>>); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(apiService.getInvoices).toHaveBeenCalledTimes(reads);
  });
  it('owned hooks: an act revoked before dispatch sends no write', async () => {
    vi.mocked(apiService.createAct).mockResolvedValue({ id: 82 } as Awaited<ReturnType<typeof apiService.createAct>>);
    const { result } = renderHook(() => useActs({ legalEntityId: 9 })); await waitFor(() => expect(result.current.loading).toBe(false));
    await expect(result.current.createAct(actPayload, () => false)).rejects.toBeInstanceOf(StaleUiOperationError); expect(apiService.createAct).not.toHaveBeenCalled();
  });

  for (const kind of ['case', 'invoice', 'act'] as const) {
    for (const action of ['create', 'update', 'delete'] as const) {
      it(`owned hooks matrix: ${kind} ${action} refuses a revoked dispatch`, async () => {
        const { result } = renderHook(() => useAllRelated()); await waitFor(() => expect(result.current.invoices.loading || result.current.acts.loading || result.current.cases.loading || result.current.payments.loading).toBe(false));
        await expect(mutate(result.current, kind, action, () => false)).rejects.toBeInstanceOf(StaleUiOperationError); expect(apiWrite(kind, action)).not.toHaveBeenCalled();
      });
      it(`owned hooks matrix: ${kind} ${action} cannot adopt or refresh after revocation`, async () => {
        vi.mocked(apiService.getCasesV1).mockResolvedValue([caseRow(1), caseRow(2)]); const pending = deferred<unknown>(); apiWrite(kind, action).mockReturnValue(pending.promise); let current = true;
        const { result } = renderHook(() => useAllRelated()); await waitFor(() => expect(result.current.invoices.loading || result.current.acts.loading || result.current.cases.loading || result.current.payments.loading).toBe(false)); const before = readCounts();
        let saved!: Promise<unknown>; act(() => { saved = mutate(result.current, kind, action, () => current); }); current = false;
        await act(async () => { pending.resolve(action === 'delete' ? undefined : { ...caseRow(1), title: 'Private mutation result' }); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); });
        expect(readCounts()).toEqual(before); expect(result.current.cases.cases.map(x => x.title)).toEqual(['Owned case 1', 'Owned case 2']);
      });
      it(`owned hooks matrix: current ${kind} ${action} preserves its API payload and refresh policy`, async () => {
        apiWrite(kind, action).mockResolvedValue(action === 'delete' ? undefined : { ...caseRow(1), number: 'Owned result' });
        const { result } = renderHook(() => useAllRelated()); await waitFor(() => expect(result.current.invoices.loading || result.current.acts.loading || result.current.cases.loading || result.current.payments.loading).toBe(false)); const before = readCounts();
        await act(async () => { await mutate(result.current, kind, action, () => true); });
        const payload = kind === 'case' ? casePayload : kind === 'invoice' ? invoicePayload : actPayload;
        if (action === 'create') expect(apiWrite(kind, action)).toHaveBeenCalledExactlyOnceWith(payload);
        else if (action === 'update') expect(apiWrite(kind, action)).toHaveBeenCalledExactlyOnceWith(1, payload);
        else expect(apiWrite(kind, action)).toHaveBeenCalledExactlyOnceWith(1);
        expect(readCounts()).toEqual(before.map((n, i) => n + (kind === 'invoice' && (i === 1 || i === 2) || kind === 'act' && (i === 3 || i === 4) ? 1 : 0)));
      });
    }
    it(`owned hooks matrix: late ${kind} write errors hide old server details`, async () => {
      const pending = deferred<unknown>(); apiWrite(kind, 'create').mockReturnValue(pending.promise); let current = true;
      const { result } = renderHook(() => useAllRelated()); await waitFor(() => expect(result.current.invoices.loading || result.current.acts.loading || result.current.cases.loading || result.current.payments.loading).toBe(false));
      let saved!: Promise<unknown>; act(() => { saved = mutate(result.current, kind, 'create', () => current); }); current = false;
      await act(async () => { pending.reject(new Error('Private old write error')); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); });
    });
    it(`owned hooks matrix: legacy ${kind} callers keep the optional-guard behavior`, async () => {
      apiWrite(kind, 'create').mockResolvedValue({ ...caseRow(), number: 'Legacy result' }); const { result } = renderHook(() => useAllRelated());
      await waitFor(() => expect(result.current.invoices.loading || result.current.acts.loading || result.current.cases.loading || result.current.payments.loading).toBe(false));
      await act(async () => { await mutate(result.current, kind, 'create'); }); expect(apiWrite(kind, 'create')).toHaveBeenCalledOnce();
    });
  }

  for (const area of ['invoices', 'acts', 'payments'] as const) {
    const getList = () => area === 'invoices' ? apiService.getInvoices : area === 'acts' ? apiService.getActs : apiService.getPaymentsV2;
    it.each(['success', 'failure'] as const)(`owned refresh: pending ${area} %s cannot replace current data or error`, async outcome => {
      const { result } = renderHook(() => useAllRelated()); await waitFor(() => expect(result.current[area].loading).toBe(false));
      const pending = deferred<unknown>(); (getList() as unknown as ReturnType<typeof vi.fn>).mockReturnValueOnce(pending.promise); let current = true;
      let refreshed!: Promise<void>; act(() => { refreshed = result.current[area].refreshOwned(() => current); }); current = false;
      await act(async () => {
        if (outcome === 'success') pending.resolve({ items: [{ id: 77, number: 'Private old list' }], total: 77, page: 1, pageSize: 5 }); else pending.reject(new Error('Private old refresh error'));
        await expect(refreshed).rejects.toBeInstanceOf(StaleUiOperationError);
      });
      expect(result.current[area].error).toBeNull(); expect(result.current[area].refreshing).toBe(false);
      if (area === 'invoices') expect(result.current.invoices.invoices).toEqual([]); else if (area === 'acts') expect(result.current.acts.acts).toEqual([]); else expect(result.current.payments.payments).toEqual([]);
    });
    it(`owned refresh: revoked ${area} callback cannot abort a newer pending query`, async () => {
      const { result, rerender } = renderHook(({ company }) => useAllRelated(company), { initialProps: { company: 9 } }); await waitFor(() => expect(result.current[area].loading).toBe(false));
      const oldRefresh = result.current[area].refreshOwned; const pending = deferred<unknown>(); (getList() as unknown as ReturnType<typeof vi.fn>).mockReturnValueOnce(pending.promise); rerender({ company: 8 });
      const signal = vi.mocked(getList()).mock.lastCall![1]?.signal as AbortSignal; const before = readCounts();
      await expect(oldRefresh(() => false)).rejects.toBeInstanceOf(StaleUiOperationError); expect(signal.aborted).toBe(false); expect(readCounts()).toEqual(before);
      await act(async () => pending.resolve({ items: [], total: 0, page: 1, pageSize: 5 }));
    });
  }
  for (const area of ['invoices', 'acts'] as const) {
    it.each(['success', 'failure'] as const)(`owned summary: pending ${area} %s cannot replace current totals`, async outcome => {
      const { result } = renderHook(() => useAllRelated()); await waitFor(() => expect(result.current[area].loading || result.current[area].summaryLoading).toBe(false));
      const before = result.current[area].summary; const pending = deferred<unknown>(); let current = true;
      const getter = area === 'invoices' ? apiService.getInvoicesSummary : apiService.getActsSummary; (getter as unknown as ReturnType<typeof vi.fn>).mockReturnValueOnce(pending.promise);
      let refreshed!: Promise<void>; act(() => { refreshed = result.current[area].refreshOwned(() => current); }); current = false;
      await act(async () => {
        if (outcome === 'success') pending.resolve({ total: { amount: 999999, count: 99 }, totalAmount: 999999, totalCount: 99 }); else pending.reject(new Error('Private old summary error'));
        await expect(refreshed).rejects.toBeInstanceOf(StaleUiOperationError);
      });
      expect(result.current[area].summary).toEqual(before); expect(result.current[area].summaryLoading).toBe(false);
    });
  }
  it('owned hooks matrix: confirmed case creation cancels an earlier read without losing the case', async () => {
    const pending = deferred<ClientCase[]>(); vi.mocked(apiService.getCasesV1).mockReturnValue(pending.promise); vi.mocked(apiService.createCase).mockResolvedValue(caseRow());
    const { result } = renderHook(() => useClientCases(undefined, { legalEntityId: 9 })); const signal = vi.mocked(apiService.getCasesV1).mock.lastCall![1]?.signal as AbortSignal;
    await act(async () => { await result.current.createCase(casePayload, () => true); }); expect(signal.aborted).toBe(true);
    await act(async () => pending.resolve([])); expect(result.current.cases).toEqual([caseRow()]); expect(result.current.loading).toBe(false);
  });
});
