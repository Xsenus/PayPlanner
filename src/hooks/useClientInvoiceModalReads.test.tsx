import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode } from 'react';
import { useClientInvoiceModalReads } from './useClientInvoiceModalReads';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { apiService } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';
import { StaleUiOperationError } from '../utils/StaleUiOperationError';
import type { Invoice, Payment } from '../types';

vi.mock('../services/api', () => ({ apiService: { getInvoice: vi.fn(), getPayments: vi.fn(), getPaymentsV2: vi.fn() } }));
const invoice = (id = 91): Invoice => ({ id, invoiceEntityId: id * 10, clientId: 1, number: `СЧ-${id}`, date: '2026-11-02', amount: 1000, type: 'Income', status: 'Pending', isPaid: false, createdAt: '2026-11-02' });
const payment = (id = 301, invoiceId = 910): Payment => ({ id, invoiceId, clientId: 1, date: '2026-11-02', plannedDate: '2026-11-02', amount: 100,
  type: 'Income', status: 'Pending', isPaid: false, description: `Оплата ${id}`, createdAt: '2026-11-02' } as Payment);
const page = (rows = [payment()]) => ({ items: rows, total: rows.length, page: 1, pageSize: 500,
  summary: { income: 100, expense: 0, balance: 100, incomeCount: rows.length, expenseCount: 0, total: rows.length } });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type Props = { enabled: boolean; scope: string; clientId: number; invoice: Invoice; invoiceAllowed: boolean; paymentsAllowed: boolean };
const defaults: Props = { enabled: true, scope: 'role1-form1', clientId: 1, invoice: invoice(), invoiceAllowed: true, paymentsAllowed: true };
const rights = { invoice: true, payments: true };
const refreshInvoices = vi.fn(), refreshPayments = vi.fn(), acceptedInvoice = vi.fn();
function useSubject(p: Props) {
  const owner = useDocumentDeliveryOwner(JSON.stringify([p.scope, p.enabled, p.invoice.id, p.invoiceAllowed, p.paymentsAllowed]));
  return useClientInvoiceModalReads(p.clientId, p.invoice, { owner, enabled: p.enabled, canReadInvoice: () => p.invoiceAllowed && rights.invoice,
    canReadPayments: () => p.paymentsAllowed && rights.payments, refreshInvoices, refreshPayments, onInvoiceRefreshed: acceptedInvoice });
}
function setup(p: Partial<Props> = {}) { return renderHook(useSubject, { initialProps: { ...defaults, ...p } }); }
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => {
  session = documentCopyTestSession(); vi.resetAllMocks(); rights.invoice = true; rights.payments = true;
  vi.mocked(apiService.getInvoice).mockImplementation(async id => invoice(id));
  vi.mocked(apiService.getPaymentsV2).mockImplementation(async params => page([payment(301, (params?.invoiceId ?? 91) * 10)]));
  vi.mocked(apiService.getPayments).mockResolvedValue([]); refreshInvoices.mockResolvedValue(undefined); refreshPayments.mockResolvedValue(undefined);
});
afterEach(() => session.restore());

describe('owned client invoice modal reads', () => {
  it('is lazy, forwards cancellation and confirms one current payment snapshot', async () => {
    const view = setup({ enabled: false }); await act(async () => {}); expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
    view.rerender(defaults); await waitFor(() => expect(view.result.current.ready).toBe(true));
    expect(view.result.current.rows[0].id).toBe(301); expect(apiService.getInvoice).not.toHaveBeenCalled(); expect(apiService.getPayments).not.toHaveBeenCalled();
    expect(vi.mocked(apiService.getPaymentsV2).mock.calls[0][1]?.signal).toBeInstanceOf(AbortSignal);
  });
  it('distinguishes confirmed empty payments and does not reload for render, dictionaries or token refresh', async () => {
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue(page([])); const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true));
    expect(view.result.current.rows).toEqual([]); expect(view.result.current.error).toBeNull(); expect(apiService.getPayments).toHaveBeenCalledOnce();
    act(() => { session.refresh(); window.dispatchEvent(new Event('dicts:changed')); }); view.rerender({ ...defaults, invoice: { ...defaults.invoice } });
    await act(async () => {}); expect(apiService.getPaymentsV2).toHaveBeenCalledOnce(); expect(apiService.getPayments).toHaveBeenCalledOnce();
  });
  it.each(['success', 'error'] as const)('a late initial %s cannot replace an explicit refresh', async outcome => {
    const old = deferred<ReturnType<typeof page>>(); vi.mocked(apiService.getPaymentsV2).mockReturnValueOnce(old.promise).mockResolvedValue(page([payment(302)]));
    const view = setup(); await waitFor(() => expect(apiService.getPaymentsV2).toHaveBeenCalledOnce()); const signal = vi.mocked(apiService.getPaymentsV2).mock.calls[0][1]?.signal;
    await act(async () => view.result.current.refresh()); expect(view.result.current.rows[0]?.id).toBe(302);
    await act(async () => { if (outcome === 'success') old.resolve(page([payment(999)])); else old.reject(new Error('Старая ошибка')); });
    expect(signal?.aborted).toBe(true); expect(view.result.current.rows[0]?.id).toBe(302); expect(view.result.current.error).toBeNull(); expect(acceptedInvoice).toHaveBeenCalledOnce();
  });
  it.each(['success', 'error'] as const)('only the latest explicit invoice %s can be adopted', async outcome => {
    const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true));
    const old = deferred<Invoice>(); vi.mocked(apiService.getInvoice).mockReturnValueOnce(old.promise);
    let first!: Promise<unknown>; act(() => { first = view.result.current.refresh().catch(error => error); }); await waitFor(() => expect(apiService.getInvoice).toHaveBeenCalledOnce());
    await act(async () => view.result.current.refresh());
    await act(async () => { if (outcome === 'success') old.resolve({ ...invoice(), amount: 999 }); else old.reject(new Error('Старый счёт')); expect(await first).toBeInstanceOf(StaleUiOperationError); });
    expect(acceptedInvoice).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ amount: 1000 })); expect(view.result.current.error).toBeNull();
  });
  it.each(['client', 'invoice', 'role', 'closed', 'invoiceDenied', 'paymentsDenied', 'login', 'actor', 'logout', 'unmount', 'association'].flatMap(change => ['success', 'error'].map(outcome => ({ change, outcome })) ))(
    'drops old $outcome after $change and cannot cancel the new request through held callbacks', async ({ change, outcome }) => {
      const old = deferred<ReturnType<typeof page>>(); vi.mocked(apiService.getPaymentsV2).mockReturnValueOnce(old.promise);
      const view = setup(); await waitFor(() => expect(apiService.getPaymentsV2).toHaveBeenCalledOnce()); const held = view.result.current;
      const signal = vi.mocked(apiService.getPaymentsV2).mock.calls[0][1]?.signal;
      const next = { ...defaults };
      if (change === 'client') { next.clientId = 2; next.scope = 'client2'; }
      if (change === 'invoice') next.invoice = invoice(93);
      if (change === 'role') next.scope = 'role2';
      if (change === 'closed') next.enabled = false;
      if (change === 'invoiceDenied') next.invoiceAllowed = false;
      if (change === 'paymentsDenied') next.paymentsAllowed = false;
      if (change === 'association') next.invoice = { ...invoice(), invoiceEntityId: 999 };
      if (change === 'login' || change === 'actor' || change === 'logout') session.replace(change);
      if (change === 'unmount') view.unmount(); else view.rerender(next);
      await act(async () => { if (outcome === 'success') old.resolve(page([payment(999)])); else old.reject(new Error('Старый ответ')); });
      expect(signal?.aborted).toBe(true); expect(view.result.current.rows.some(row => row.id === 999)).toBe(false); expect(acceptedInvoice).not.toHaveBeenCalled();
      const count = vi.mocked(apiService.getPaymentsV2).mock.calls.length;
      await act(async () => { await held.refresh().catch(() => undefined); await held.retry().catch(() => undefined); held.cancel(); });
      expect(apiService.getPaymentsV2).toHaveBeenCalledTimes(count);
    });
  it.each([401, 403, 404])('HTTP %s clears previous rows, never falls back, and waits for explicit retry', async status => {
    const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true));
    vi.mocked(apiService.getPaymentsV2).mockRejectedValueOnce(new Error(`HTTP ${status} - Доступ закрыт`));
    await act(async () => { await expect(view.result.current.retry()).rejects.toThrow(`HTTP ${status}`); });
    expect(view.result.current.rows).toEqual([]); expect(view.result.current.ready).toBe(false); expect(view.result.current.error).toBeTruthy(); expect(apiService.getPayments).not.toHaveBeenCalled();
    await act(async () => {}); expect(apiService.getPaymentsV2).toHaveBeenCalledTimes(2);
    await act(async () => view.result.current.retry()); expect(view.result.current.ready).toBe(true); expect(view.result.current.error).toBeNull();
  });
  it('coalesces explicit retries while preserving a visible error until fresh confirmation', async () => {
    vi.mocked(apiService.getPaymentsV2).mockRejectedValueOnce(new Error('network')); vi.mocked(apiService.getPayments).mockRejectedValueOnce(new Error('network'));
    const view = setup(); await waitFor(() => expect(view.result.current.error).toBeTruthy()); expect(view.result.current.rows).toEqual([]);
    const next = deferred<ReturnType<typeof page>>(); vi.mocked(apiService.getPaymentsV2).mockReturnValueOnce(next.promise);
    let first!: Promise<void>; let second!: Promise<void>; act(() => { first = view.result.current.retry(); second = view.result.current.retry(); });
    expect(first).toBe(second); expect(apiService.getPaymentsV2).toHaveBeenCalledTimes(2);
    await act(async () => { next.resolve(page()); await first; }); expect(view.result.current.ready).toBe(true);
  });
  it('does not dispatch later refresh steps after the invoice owner is revoked', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true)); const pending = deferred<void>(); refreshInvoices.mockReturnValueOnce(pending.promise);
    let result!: Promise<unknown>; act(() => { result = view.result.current.refresh().catch(error => error); }); rights.invoice = false;
    await act(async () => { pending.resolve(); expect(await result).toBeInstanceOf(StaleUiOperationError); });
    expect(refreshPayments).not.toHaveBeenCalled(); expect(apiService.getInvoice).not.toHaveBeenCalled(); expect(view.result.current.rows).toEqual([]);
  });
  it('keeps invoice refresh independent of revoked Payments view without exposing or requesting payments', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true)); const count = vi.mocked(apiService.getPaymentsV2).mock.calls.length;
    const pending = deferred<void>(); refreshInvoices.mockReturnValueOnce(pending.promise); let result!: Promise<void>;
    act(() => { result = view.result.current.refresh(); }); rights.payments = false;
    await act(async () => { pending.resolve(); await result; });
    expect(apiService.getInvoice).toHaveBeenCalledOnce(); expect(refreshPayments).not.toHaveBeenCalled(); expect(apiService.getPaymentsV2).toHaveBeenCalledTimes(count);
    expect(acceptedInvoice).toHaveBeenCalledOnce(); expect(view.result.current.rows).toEqual([]); expect(view.result.current.error).toContain('Недостаточно прав');
  });
  it('never starts a payment request without its separate view permission', async () => {
    const view = setup({ paymentsAllowed: false }); await act(async () => {}); expect(apiService.getPaymentsV2).not.toHaveBeenCalled(); expect(apiService.getPayments).not.toHaveBeenCalled();
    expect(view.result.current.loading).toBe(false); expect(view.result.current.ready).toBe(false); expect(view.result.current.error).toContain('Недостаточно прав');
  });
  it('survives StrictMode effect replay without adopting the cancelled lifetime', async () => {
    const view = renderHook(useSubject, { initialProps: defaults, wrapper: StrictMode }); await waitFor(() => expect(view.result.current.ready).toBe(true)); expect(view.result.current.rows[0]?.id).toBe(301);
    expect(vi.mocked(apiService.getPaymentsV2).mock.calls.at(-1)?.[1]?.signal?.aborted).toBe(false);
  });
  it('read boundaries: a known foreign invoice cannot start a payment lookup for this client', async () => {
    const view = setup({ clientId: 2 }); await waitFor(() => expect(view.result.current.error).toContain('другому счёту или клиенту'));
    expect(apiService.getPaymentsV2).not.toHaveBeenCalled(); expect(apiService.getPayments).not.toHaveBeenCalled(); expect(view.result.current.ready).toBe(false);
  });
  it.each([0, -1, NaN])('read boundaries: invalid client %s cannot start an unscoped request', async clientId => {
    const view = setup({ clientId }); await waitFor(() => expect(view.result.current.error).toBeTruthy()); expect(apiService.getPaymentsV2).not.toHaveBeenCalled(); expect(apiService.getPayments).not.toHaveBeenCalled();
  });
  it('read boundaries: a malformed current response is an error without cache or broad fallback', async () => {
    vi.mocked(apiService.getPaymentsV2).mockResolvedValueOnce(page([payment(0)])); const view = setup(); await waitFor(() => expect(view.result.current.error).toBeTruthy());
    expect(view.result.current.ready).toBe(false); expect(view.result.current.rows).toEqual([]); expect(apiService.getPayments).not.toHaveBeenCalled();
  });

  it('read boundaries: retry after a denied document must revalidate that document before payment reads', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true)); const heldRetry = view.result.current.retry;
    vi.mocked(apiService.getInvoice).mockRejectedValue(new Error('HTTP 403 - Счёт недоступен')); const count = vi.mocked(apiService.getPaymentsV2).mock.calls.length;
    await act(async () => { await expect(view.result.current.refresh()).rejects.toThrow('HTTP 403'); });
    await act(async () => { await expect(heldRetry()).rejects.toThrow('HTTP 403'); });
    expect(apiService.getInvoice).toHaveBeenCalledTimes(2); expect(apiService.getPaymentsV2).toHaveBeenCalledTimes(count); expect(refreshPayments).not.toHaveBeenCalled(); expect(view.result.current.ready).toBe(false);
    vi.mocked(apiService.getInvoice).mockResolvedValue(invoice()); await act(async () => view.result.current.retry()); expect(apiService.getInvoice).toHaveBeenCalledTimes(3); expect(view.result.current.ready).toBe(true);
  });

  it('read boundaries: refresh revokes confirmation synchronously before the next render', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.ready).toBe(true)); const held = view.result.current.isReady;
    const pending = deferred<Invoice>(); vi.mocked(apiService.getInvoice).mockReturnValueOnce(pending.promise); let result!: Promise<void>;
    act(() => { result = view.result.current.refresh(); expect(held()).toBe(false); });
    await act(async () => { pending.resolve(invoice()); await result; }); expect(view.result.current.isReady()).toBe(true);
  });

});
