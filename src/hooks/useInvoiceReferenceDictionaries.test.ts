import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useInvoiceReferenceDictionaries } from './useInvoiceReferenceDictionaries';
import { apiService } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';

vi.mock('../services/api', () => ({ apiService: { getInvoiceReferenceDictionaries: vi.fn() } }));
const row = (id = 1, name = 'Текущая запись', isActive = true) => ({ id, name, description: '', colorHex: '', createdAt: '2026-11-02T00:00:00Z', isActive });
const data = (id = 1) => ({ invoiceUnits: [row(id)], services: [{ ...row(id), defaultPrice: 500, unit: 'шт', categoryId: id }], serviceCategories: [row(id)] });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }

describe('invoice references', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { session = documentCopyTestSession(); vi.resetAllMocks(); vi.mocked(apiService.getInvoiceReferenceDictionaries).mockResolvedValue(data()); });
  afterEach(() => session.restore());

  it('reference baseline: changing the invoice scope immediately hides all previous catalogues', async () => {
    const view = renderHook((scope: string) => useInvoiceReferenceDictionaries({ ownerScope: scope }), { initialProps: 'invoice:1' });
    await waitFor(() => expect(view.result.current.services).toHaveLength(1));
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockReturnValue(new Promise(() => {})); view.rerender('invoice:2');
    expect(view.result.current.services).toEqual([]); expect(view.result.current.invoiceUnits).toEqual([]); expect(view.result.current.serviceCategories).toEqual([]);
  });
  it('reference baseline: a closed or forbidden caller does not fetch even after a dictionary event', async () => {
    const view = renderHook(() => useInvoiceReferenceDictionaries({ enabled: false }));
    act(() => window.dispatchEvent(new Event('dicts:changed'))); await act(async () => {});
    expect(apiService.getInvoiceReferenceDictionaries).not.toHaveBeenCalled(); expect(view.result.current.loading).toBe(false);
  });
  it('reference baseline: a replaced login cannot execute a held refresh before a render', async () => {
    const view = renderHook(() => useInvoiceReferenceDictionaries()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const refresh = view.result.current.refresh; session.replace('actor'); await act(async () => { await refresh(); });
    expect(apiService.getInvoiceReferenceDictionaries).toHaveBeenCalledOnce();
  });
  it('reference pending: a scope change aborts the old read and rejects its late response', async () => {
    const old = deferred<ReturnType<typeof data>>(); vi.mocked(apiService.getInvoiceReferenceDictionaries).mockReturnValueOnce(old.promise).mockResolvedValueOnce(data(2));
    const view = renderHook((scope: string) => useInvoiceReferenceDictionaries({ ownerScope: scope }), { initialProps: 'one' });
    const signal = vi.mocked(apiService.getInvoiceReferenceDictionaries).mock.calls[0][0]?.signal; view.rerender('two'); await waitFor(() => expect(view.result.current.services[0]?.id).toBe(2));
    expect(signal?.aborted).toBe(true); await act(async () => old.resolve(data(1))); expect(view.result.current.services[0]?.id).toBe(2);
  });
  it('reference refresh order: a slower older refresh cannot replace the newest catalogue', async () => {
    const view = renderHook(() => useInvoiceReferenceDictionaries()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const old = deferred<ReturnType<typeof data>>(); vi.mocked(apiService.getInvoiceReferenceDictionaries).mockReturnValueOnce(old.promise).mockResolvedValueOnce(data(3));
    let refresh!: Promise<void>; act(() => { refresh = view.result.current.refresh(); });
    const signal = vi.mocked(apiService.getInvoiceReferenceDictionaries).mock.calls[1][0]?.signal;
    await act(async () => view.result.current.refresh()); await act(async () => { old.resolve(data(2)); await refresh; });
    expect(signal?.aborted).toBe(true); expect(view.result.current.services[0]?.id).toBe(3);
  });
  it('reference current errors: a temporary failure retains data but a denial clears it and waits for explicit retry', async () => {
    const view = renderHook(() => useInvoiceReferenceDictionaries()); await waitFor(() => expect(view.result.current.services).toHaveLength(1));
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockRejectedValueOnce(new Error('Сеть недоступна')); await act(async () => view.result.current.refresh());
    expect(view.result.current.services).toHaveLength(1); expect(view.result.current.error).toContain('Сеть');
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт')); await act(async () => view.result.current.refresh());
    expect(view.result.current.services).toEqual([]); expect(view.result.current.invoiceUnits).toEqual([]); expect(view.result.current.serviceCategories).toEqual([]);
    await act(async () => {}); expect(apiService.getInvoiceReferenceDictionaries).toHaveBeenCalledTimes(3);
    await act(async () => view.result.current.refresh()); expect(view.result.current.services).toHaveLength(1); expect(view.result.current.error).toBeNull();
  });
  it('reference current event: a dictionary edit refreshes once and an empty successful response does not loop', async () => {
    const view = renderHook(() => useInvoiceReferenceDictionaries()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockResolvedValueOnce({ invoiceUnits: [], services: [], serviceCategories: [] });
    act(() => window.dispatchEvent(new Event('dicts:changed'))); await waitFor(() => expect(view.result.current.services).toEqual([]));
    await act(async () => {}); expect(apiService.getInvoiceReferenceDictionaries).toHaveBeenCalledTimes(2);
  });
  it('reference stable scope: a parent render and ordinary token refresh retain the catalogue without refetching', async () => {
    const view = renderHook(() => useInvoiceReferenceDictionaries({ ownerScope: 'same' })); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const original = view.result.current.services; session.refresh(); view.rerender(); await act(async () => {});
    expect(view.result.current.services).toBe(original); expect(apiService.getInvoiceReferenceDictionaries).toHaveBeenCalledOnce();
  });
  it('reference unmount: cleanup aborts the current read and removes the dictionary event listener', async () => {
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockReturnValue(new Promise(() => {})); const view = renderHook(() => useInvoiceReferenceDictionaries());
    const signal = vi.mocked(apiService.getInvoiceReferenceDictionaries).mock.calls[0][0]?.signal; view.unmount(); expect(signal?.aborted).toBe(true);
    window.dispatchEvent(new Event('dicts:changed')); expect(apiService.getInvoiceReferenceDictionaries).toHaveBeenCalledOnce();
  });
  it.each(['invoiceUnits', 'services', 'serviceCategories'] as const)('reference invalid identity: duplicate IDs in %s cannot replace a trusted snapshot', async group => {
    const view = renderHook(() => useInvoiceReferenceDictionaries()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const original = data(); const invalid = { ...original, [group]: [original[group][0], original[group][0]] } as ReturnType<typeof data>;
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockResolvedValueOnce(invalid); await act(async () => view.result.current.refresh());
    expect(view.result.current.error).toContain('подтвердить'); expect(view.result.current.services).toHaveLength(1);
  });
  it.each([Number.NaN, Number.POSITIVE_INFINITY])('reference invalid price: non-finite price %s is rejected before use', async defaultPrice => {
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockResolvedValueOnce({ ...data(), services: [{ ...data().services[0], defaultPrice }] });
    const view = renderHook(() => useInvoiceReferenceDictionaries()); await waitFor(() => expect(view.result.current.error).toContain('подтвердить'));
    expect(view.result.current.services).toEqual([]); expect(apiService.getInvoiceReferenceDictionaries).toHaveBeenCalledOnce();
  });
  it('reference active filter: inactive rows are removed in all three groups without altering valid prices', async () => {
    const current = data(); current.invoiceUnits.push(row(2, 'Неактивная', false)); current.serviceCategories.push(row(2, 'Неактивная', false)); current.services.push({ ...current.services[0], id: 2, isActive: false });
    vi.mocked(apiService.getInvoiceReferenceDictionaries).mockResolvedValue(current); const view = renderHook(() => useInvoiceReferenceDictionaries());
    await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.invoiceUnits).toHaveLength(1); expect(view.result.current.serviceCategories).toHaveLength(1); expect(view.result.current.services).toEqual([data().services[0]]);
  });
});
