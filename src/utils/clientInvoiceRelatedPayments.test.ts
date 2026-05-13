import { describe, expect, it, vi } from 'vitest';

import type { Invoice, Payment } from '../types';
import {
  CLIENT_INVOICE_LOOKUP_FROM,
  CLIENT_INVOICE_LOOKUP_TO,
  CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
  collectClientInvoiceRelatedPayments,
  loadClientInvoiceRelatedPayments,
  resolveClientInvoiceCaseId,
} from './clientInvoiceRelatedPayments';

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
    description: 'Платеж по счету',
    createdAt: '2026-04-09T00:00:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    account: '30',
    accountDate: '2026-04-08',
    invoiceId: 30,
    ...overrides,
  } as Payment;
}

describe('clientInvoiceRelatedPayments', () => {
  it('uses the only client case id from clientCaseIds for invoice payment lookup', () => {
    expect(resolveClientInvoiceCaseId(createInvoice({ clientCaseIds: [123], clientCaseId: undefined }))).toBe(123);
  });

  it('falls back to clientCaseId when invoice has no single case in clientCaseIds', () => {
    expect(
      resolveClientInvoiceCaseId(
        createInvoice({
          clientCaseIds: [111, 222],
          clientCaseId: 333,
        }),
      ),
    ).toBe(333);
  });

  it('returns undefined when invoice has no usable client case id', () => {
    expect(
      resolveClientInvoiceCaseId(
        createInvoice({
          clientCaseIds: [],
          clientCaseId: undefined,
        }),
      ),
    ).toBeUndefined();
  });

  it('keeps the representative row and sibling operations for the same invoice', () => {
    const invoice = createInvoice({ id: 528, invoiceEntityId: 24, number: '24' });
    const representativePayment = createPayment({
      id: 528,
      invoiceId: 24,
      amount: 10_000,
      description: 'Представляющая запись по счету №24',
      plannedDate: '2026-04-02',
      date: '2026-04-02',
    });
    const siblingPayment = createPayment({
      id: 529,
      invoiceId: 24,
      amount: 5_000,
      description: 'Доплата по счету №24',
      plannedDate: '2026-04-03',
      date: '2026-04-03',
    });
    const unrelatedPayment = createPayment({
      id: 530,
      invoiceId: 99,
      account: '99',
      description: 'Другой счет',
    });

    const result = collectClientInvoiceRelatedPayments(invoice, [
      siblingPayment,
      representativePayment,
      unrelatedPayment,
    ]);

    expect(result.map((item) => item.id)).toEqual([528, 529]);
  });

  it('keeps sibling operations linked only by legacy account/date when invoiceId is missing', () => {
    const invoice = createInvoice({ id: 528, invoiceEntityId: 24, number: '24', date: '2026-04-02' });
    const representativePayment = createPayment({
      id: 528,
      invoiceId: 24,
      account: '24',
      accountDate: '2026-04-02',
      description: 'Representative payment for invoice 24',
      plannedDate: '2026-04-02',
      date: '2026-04-02',
    });
    const legacySiblingPayment = createPayment({
      id: 529,
      invoiceId: undefined,
      account: '24',
      accountDate: '2026-04-02',
      description: 'Legacy sibling payment for invoice 24',
      plannedDate: '2026-04-03',
      date: '2026-04-03',
    });

    const result = collectClientInvoiceRelatedPayments(invoice, [
      legacySiblingPayment,
      representativePayment,
    ]);

    expect(result.map((item) => item.id)).toEqual([528, 529]);
  });

  it('returns an empty list when invoice is missing', () => {
    expect(collectClientInvoiceRelatedPayments(null, [createPayment()])).toEqual([]);
  });

  it('hides the representative invoice row when installment schedule payments exist', () => {
    const invoice = createInvoice({ id: 551, invoiceEntityId: 111, number: '30' });
    const representativeInvoice = createPayment({
      id: 551,
      invoiceId: 111,
      documentKind: 'Invoice',
      description: 'Представляющая запись счета №30',
      plannedDate: '2026-04-08',
      date: '2026-04-08',
    });
    const installmentPayment = createPayment({
      id: 552,
      invoiceId: 111,
      amount: 6_000,
      description: 'Рассрочка 1/3',
      plannedDate: '2026-04-09',
      date: '2026-04-09',
      systemNotes: '[pp:installment-payment] {"sequence":1}',
    });

    const result = collectClientInvoiceRelatedPayments(invoice, [
      representativeInvoice,
      installmentPayment,
    ]);

    expect(result.map((item) => item.id)).toEqual([552]);
  });

  it('loads related payments by invoice id before falling back to the client lookup window', async () => {
    const invoice = createInvoice({ id: 528, invoiceEntityId: 24, number: '24', clientCaseIds: [701] });
    const representativePayment = createPayment({
      id: 528,
      invoiceId: 24,
      description: 'Представляющая запись по счету №24',
    });
    const siblingPayment = createPayment({
      id: 529,
      invoiceId: 24,
      description: 'Доплата по счету №24',
    });
    const fetchPayments = vi.fn().mockResolvedValue([siblingPayment, representativePayment]);

    const result = await loadClientInvoiceRelatedPayments({
      invoice,
      clientId: 42,
      visiblePayments: [],
      fetchPayments,
    });

    expect(fetchPayments).toHaveBeenCalledTimes(1);
    expect(fetchPayments).toHaveBeenCalledWith({
      invoiceId: 528,
      excludeInvoiceDocuments: false,
      page: 1,
      pageSize: CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
      sortBy: 'date',
      sortDir: 'asc',
    });
    expect(result.map((item) => item.id)).toEqual([528, 529]);
  });

  it('does not over-filter the remote lookup by caseId when invoice is linked to several cases', async () => {
    const invoice = createInvoice({
      id: 540,
      invoiceEntityId: 27,
      number: '27',
      clientCaseIds: [701, 702],
      clientCaseId: undefined,
    });
    const fetchPayments = vi.fn().mockResolvedValueOnce([]).mockResolvedValueOnce([]);

    await loadClientInvoiceRelatedPayments({
      invoice,
      clientId: 42,
      visiblePayments: [],
      fetchPayments,
    });

    expect(fetchPayments).toHaveBeenNthCalledWith(1, {
      invoiceId: 540,
      excludeInvoiceDocuments: false,
      page: 1,
      pageSize: CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
      sortBy: 'date',
      sortDir: 'asc',
    });
    expect(fetchPayments).toHaveBeenNthCalledWith(2, {
      from: CLIENT_INVOICE_LOOKUP_FROM,
      to: CLIENT_INVOICE_LOOKUP_TO,
      clientId: 42,
      caseId: undefined,
    });
  });

  it('falls back to currently visible payments when the remote lookup fails', async () => {
    const invoice = createInvoice({ id: 530, invoiceEntityId: 25, number: '25', clientCaseIds: [702] });
    const representativePayment = createPayment({
      id: 530,
      invoiceId: 25,
      account: '25',
      description: 'Представляющая запись по счету №25',
    });
    const siblingPayment = createPayment({
      id: 531,
      invoiceId: 25,
      account: '25',
      description: 'Доплата по счету №25',
    });
    const fetchPayments = vi.fn().mockRejectedValue(new Error('network'));

    const result = await loadClientInvoiceRelatedPayments({
      invoice,
      clientId: 42,
      visiblePayments: [siblingPayment, representativePayment],
      fetchPayments,
    });

    expect(fetchPayments).toHaveBeenCalledTimes(2);
    expect(result.map((item) => item.id)).toEqual([530, 531]);
  });

  it('hides the representative invoice row after remote lookup when installment payments exist', async () => {
    const invoice = createInvoice({ id: 551, invoiceEntityId: 111, number: '30', clientCaseIds: [701] });
    const representativePayment = createPayment({
      id: 551,
      invoiceId: 111,
      documentKind: 'Invoice',
      description: 'Представляющая запись счета №30',
      plannedDate: '2026-04-08',
      date: '2026-04-08',
    });
    const installmentPayment = createPayment({
      id: 552,
      invoiceId: 111,
      amount: 6_000,
      description: 'Рассрочка 1/3',
      systemNotes: '[pp:installment-payment] {"sequence":1}',
    });
    const fetchPayments = vi.fn().mockResolvedValue([representativePayment, installmentPayment]);

    const result = await loadClientInvoiceRelatedPayments({
      invoice,
      clientId: 42,
      visiblePayments: [],
      fetchPayments,
    });

    expect(result.map((item) => item.id)).toEqual([552]);
  });
});
