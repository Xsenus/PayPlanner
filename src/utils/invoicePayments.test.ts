import { describe, expect, it } from 'vitest';
import type { Invoice, Payment } from '../types';
import { filterRelatedPaymentsForInvoice, getInvoiceLegacyKey, isPaymentLinkedToInvoice } from './invoicePayments';

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
    ...overrides,
  };
}

function createPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 900,
    date: '2026-04-09',
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

describe('invoicePayments utilities', () => {
  it('links payments by canonical invoice id', () => {
    const invoice = createInvoice();
    const payment = createPayment({ invoiceId: 30 });

    expect(isPaymentLinkedToInvoice(payment, invoice)).toBe(true);
  });

  it('does not treat the current invoice record as a related payment', () => {
    const invoice = createInvoice({ id: 547, invoiceEntityId: 30 });
    const payment = createPayment({
      id: 547,
      invoiceId: 30,
      documentKind: 'Invoice',
    });

    expect(isPaymentLinkedToInvoice(payment, invoice)).toBe(false);
  });

  it('keeps sibling invoice rows with the same invoice number and year', () => {
    const invoice = createInvoice({ id: 547, invoiceEntityId: null });
    const siblingInvoiceRow = createPayment({
      id: 548,
      invoiceId: null,
      documentKind: 'Invoice',
      account: '30',
      accountDate: '2026-04-09',
    });

    expect(getInvoiceLegacyKey(invoice.number, invoice.date)).toBe('30::2026');
    expect(isPaymentLinkedToInvoice(siblingInvoiceRow, invoice)).toBe(true);
  });

  it('filters, deduplicates and sorts linked payments for the invoice', () => {
    const invoice = createInvoice();
    const currentInvoiceRecord = createPayment({
      id: 547,
      invoiceId: 30,
      documentKind: 'Invoice',
    });
    const laterPayment = createPayment({
      id: 902,
      date: '2026-05-09',
      plannedDate: '2026-05-09',
    });
    const earlierPayment = createPayment({
      id: 901,
      date: '2026-04-09',
      plannedDate: '2026-04-09',
    });

    const result = filterRelatedPaymentsForInvoice(
      [laterPayment, currentInvoiceRecord, earlierPayment, earlierPayment],
      invoice,
    );

    expect(result.map((payment) => payment.id)).toEqual([901, 902]);
  });
});
