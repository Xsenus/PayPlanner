import { describe, expect, it, vi } from 'vitest';

import type { Invoice, Payment } from '../types';
import {
  CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
} from './clientInvoiceRelatedPayments';
import { refreshClientInvoiceModalData } from './clientInvoiceModalData';

function createInvoice(overrides: Partial<Invoice> = {}): Invoice {
  return {
    id: 547,
    invoiceEntityId: 30,
    number: '30',
    date: '2026-04-08',
    amount: 18_000,
    type: 'Income',
    status: 'Pending',
    isPaid: false,
    createdAt: '2026-04-08T00:00:00Z',
    clientCaseId: 701,
    clientCaseIds: [701],
    ...overrides,
  };
}

function createPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 900,
    date: '2026-04-09',
    plannedDate: '2026-04-09',
    amount: 18_000,
    type: 'Income',
    status: 'Pending',
    isPaid: false,
    description: 'Payment linked to invoice',
    createdAt: '2026-04-09T00:00:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    account: '30',
    accountDate: '2026-04-08',
    invoiceId: 30,
    ...overrides,
  } as Payment;
}

describe('refreshClientInvoiceModalData', () => {
  it.each([401, 403, 404])('invoice refresh baseline: HTTP %s cannot use the expired invoice as fallback', async status => {
    const fetchInvoice = vi.fn().mockRejectedValue(new Error(`HTTP ${status} - Доступ закрыт`)); const fetchPayments = vi.fn().mockResolvedValue([]);
    await expect(refreshClientInvoiceModalData({ selectedInvoice: createInvoice(), clientId: 42, visiblePayments: [], refreshInvoices: vi.fn(), refreshPayments: vi.fn(), fetchInvoice, fetchPayments })).rejects.toThrow(`HTTP ${status}`);
    expect(fetchPayments).not.toHaveBeenCalled();
  });
  it('invoice refresh baseline: a different document response cannot replace the current invoice', async () => {
    await expect(refreshClientInvoiceModalData({ selectedInvoice: createInvoice(), clientId: 42, visiblePayments: [], refreshInvoices: vi.fn(), refreshPayments: vi.fn(), fetchInvoice: vi.fn().mockResolvedValue(createInvoice({ id: 999, invoiceEntityId: 999 })), fetchPayments: vi.fn().mockResolvedValue([]) })).rejects.toThrow();
  });
  it('refreshes invoice data and reloads related payments using the fresh invoice snapshot', async () => {
    const selectedInvoice = createInvoice({
      id: 528,
      invoiceEntityId: 24,
      number: '24',
      date: '2026-04-02',
      clientCaseIds: [701],
    });
    const freshInvoice = createInvoice({
      id: 528,
      invoiceEntityId: 24,
      number: '24',
      date: '2026-04-02',
      clientCaseIds: [701, 702],
      clientCaseId: undefined,
    });
    const representativePayment = createPayment({
      id: 528,
      invoiceId: 24,
      account: '24',
      accountDate: '2026-04-02',
      description: 'Representative payment',
      plannedDate: '2026-04-02',
      date: '2026-04-02',
      amount: 10_000,
    });
    const siblingPayment = createPayment({
      id: 529,
      invoiceId: undefined,
      account: '24',
      accountDate: '2026-04-02',
      description: 'Sibling payment',
      plannedDate: '2026-04-03',
      date: '2026-04-03',
      amount: 5_000,
    });
    const refreshInvoices = vi.fn().mockResolvedValue(undefined);
    const refreshPayments = vi.fn().mockResolvedValue(undefined);
    const fetchInvoice = vi.fn().mockResolvedValue(freshInvoice);
    const fetchPayments = vi.fn().mockResolvedValue([siblingPayment, representativePayment]);

    const result = await refreshClientInvoiceModalData({
      selectedInvoice,
      clientId: 42,
      visiblePayments: [],
      refreshInvoices,
      refreshPayments,
      fetchInvoice,
      fetchPayments,
    });

    expect(refreshInvoices).toHaveBeenCalledTimes(1);
    expect(refreshPayments).toHaveBeenCalledTimes(1);
    expect(fetchInvoice).toHaveBeenCalledWith(528);
    expect(fetchPayments).toHaveBeenCalledWith({
      invoiceId: 528,
      excludeInvoiceDocuments: false,
      page: 1,
      pageSize: CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
      sortBy: 'date',
      sortDir: 'asc',
    });
    expect(result.invoice).toBe(freshInvoice);
    expect(result.relatedPayments.map((item) => item.id)).toEqual([528, 529]);
  });

  it('reports a failed invoice read without loading payments for the expired snapshot', async () => {
    const selectedInvoice = createInvoice({
      id: 529,
      invoiceEntityId: 25,
      number: '25',
      date: '2026-04-02',
      clientCaseIds: [702],
    });
    const representativePayment = createPayment({
      id: 529,
      invoiceId: 25,
      account: '25',
      accountDate: '2026-04-02',
      description: 'Representative payment',
      plannedDate: '2026-04-02',
      date: '2026-04-02',
      amount: 10_000,
    });
    const siblingPayment = createPayment({
      id: 530,
      invoiceId: 25,
      account: '25',
      accountDate: '2026-04-02',
      description: 'Sibling payment',
      plannedDate: '2026-04-03',
      date: '2026-04-03',
      amount: 5_000,
    });
    const refreshInvoices = vi.fn().mockResolvedValue(undefined);
    const refreshPayments = vi.fn().mockResolvedValue(undefined);
    const fetchInvoice = vi.fn().mockRejectedValue(new Error('invoice fetch failed'));
    const fetchPayments = vi.fn().mockResolvedValue([siblingPayment, representativePayment]);

    await expect(refreshClientInvoiceModalData({
      selectedInvoice,
      clientId: 43,
      visiblePayments: [],
      refreshInvoices,
      refreshPayments,
      fetchInvoice,
      fetchPayments,
    })).rejects.toThrow('invoice fetch failed');

    expect(refreshInvoices).toHaveBeenCalledTimes(1);
    expect(refreshPayments).not.toHaveBeenCalled();
    expect(fetchInvoice).toHaveBeenCalledWith(529);
    expect(fetchPayments).not.toHaveBeenCalled();
    expect(selectedInvoice.id).toBe(529);
  });

  it('returns an empty state when the modal has no selected invoice', async () => {
    const refreshInvoices = vi.fn().mockResolvedValue(undefined);
    const refreshPayments = vi.fn().mockResolvedValue(undefined);
    const fetchInvoice = vi.fn();
    const fetchPayments = vi.fn();

    const result = await refreshClientInvoiceModalData({
      selectedInvoice: null,
      clientId: 44,
      visiblePayments: [],
      refreshInvoices,
      refreshPayments,
      fetchInvoice,
      fetchPayments,
    });

    expect(refreshInvoices).toHaveBeenCalledTimes(1);
    expect(refreshPayments).toHaveBeenCalledTimes(1);
    expect(fetchInvoice).not.toHaveBeenCalled();
    expect(fetchPayments).not.toHaveBeenCalled();
    expect(result).toEqual({
      invoice: null,
      relatedPayments: [],
    });
  });
  it('owned refresh helper: revocation after list refresh prevents every later step', async () => {
    let current = true; const refreshPayments = vi.fn(), fetchInvoice = vi.fn(), fetchPayments = vi.fn();
    await expect(refreshClientInvoiceModalData({ selectedInvoice: createInvoice(), clientId: 42, visiblePayments: [], refreshInvoices: vi.fn().mockImplementation(async () => { current = false; }),
      refreshPayments, fetchInvoice, fetchPayments, read: { isCurrent: () => current } })).rejects.toBeInstanceOf(Error);
    expect(refreshPayments).not.toHaveBeenCalled(); expect(fetchInvoice).not.toHaveBeenCalled(); expect(fetchPayments).not.toHaveBeenCalled();
  });
  it('owned refresh helper: Accounts-only view refreshes the document without any payment request', async () => {
    const refreshPayments = vi.fn(), fetchPayments = vi.fn(); const fresh = createInvoice();
    const result = await refreshClientInvoiceModalData({ selectedInvoice: fresh, clientId: 42, visiblePayments: [createPayment()], refreshInvoices: vi.fn(), refreshPayments,
      fetchInvoice: vi.fn().mockResolvedValue(fresh), fetchPayments, read: { canReadPayments: () => false } });
    expect(result).toEqual({ invoice: fresh, relatedPayments: [] }); expect(refreshPayments).not.toHaveBeenCalled(); expect(fetchPayments).not.toHaveBeenCalled();
  });
  it('owned refresh helper: a new representative of the same canonical invoice remains valid', async () => {
    const fresh = createInvoice({ id: 999 }); const result = await refreshClientInvoiceModalData({ selectedInvoice: createInvoice(), clientId: 42, visiblePayments: [], refreshInvoices: vi.fn(), refreshPayments: vi.fn(),
      fetchInvoice: vi.fn().mockResolvedValue(fresh), fetchPayments: vi.fn().mockResolvedValue([]) }); expect(result.invoice).toBe(fresh);
  });
  it.each([{ id: 0 }, { amount: NaN }, { clientId: 99 }, { clientId: '42' as unknown as number }])('owned refresh helper: malformed or foreign fresh metadata %# cannot trigger payments', async overrides => {
    const fetchPayments = vi.fn(); await expect(refreshClientInvoiceModalData({ selectedInvoice: createInvoice(), clientId: 42, visiblePayments: [], refreshInvoices: vi.fn(), refreshPayments: vi.fn(),
      fetchInvoice: vi.fn().mockResolvedValue(createInvoice(overrides)), fetchPayments })).rejects.toThrow('Ответ относится'); expect(fetchPayments).not.toHaveBeenCalled();
  });

});
