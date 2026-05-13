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

  it('falls back to the selected invoice when fetching the fresh invoice fails', async () => {
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

    const result = await refreshClientInvoiceModalData({
      selectedInvoice,
      clientId: 43,
      visiblePayments: [],
      refreshInvoices,
      refreshPayments,
      fetchInvoice,
      fetchPayments,
    });

    expect(refreshInvoices).toHaveBeenCalledTimes(1);
    expect(refreshPayments).toHaveBeenCalledTimes(1);
    expect(fetchInvoice).toHaveBeenCalledWith(529);
    expect(fetchPayments).toHaveBeenCalledWith({
      invoiceId: 529,
      excludeInvoiceDocuments: false,
      page: 1,
      pageSize: CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
      sortBy: 'date',
      sortDir: 'asc',
    });
    expect(result.invoice).toBe(selectedInvoice);
    expect(result.relatedPayments.map((item) => item.id)).toEqual([529, 530]);
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
});
