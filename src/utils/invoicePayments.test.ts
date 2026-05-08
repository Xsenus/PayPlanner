import { describe, expect, it } from 'vitest';
import type { Invoice, Payment } from '../types';
import {
  filterRelatedPaymentsForInvoice,
  filterRelatedPaymentsForInvoiceWithOptions,
  filterRepresentativeInvoiceWhenInstallmentsExist,
  getStatusSourcePaymentsForInvoice,
  getInvoiceLegacyKey,
  INSTALLMENT_PAYMENT_MARKER,
  PARTIAL_REMAINDER_PAYMENT_MARKER,
  PARTIAL_REMAINDER_SOURCE_MARKER,
  isPartialRemainderSourcePayment,
  isInvoiceManagedPayment,
  isPaymentLinkedToInvoice,
  isTransferredRemainderPayment,
  needsPartialRemainderTransfer,
  parsePartialRemainderTransferMetadata,
} from './invoicePayments';

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

  it('can include the current invoice record when UI needs the same operations view as the registry', () => {
    const invoice = createInvoice({ id: 547, invoiceEntityId: 30 });
    const currentInvoiceRecord = createPayment({
      id: 547,
      invoiceId: 30,
      documentKind: 'Payment',
      status: 'Completed',
      isPaid: true,
      paidAmount: 18_000,
      paidDate: '2026-04-09',
    });

    const result = filterRelatedPaymentsForInvoiceWithOptions(
      [currentInvoiceRecord],
      invoice,
      { includeCurrentInvoiceRecord: true },
    );

    expect(result.map((payment) => payment.id)).toEqual([547]);
  });

  it('treats representative invoice, generated installment and transferred remainder payments as invoice-managed', () => {
    const invoicePayment = createPayment({ documentKind: 'Invoice' });
    const installmentPayment = createPayment({
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1}`,
    });
    const remainderPayment = createPayment({
      invoiceId: 30,
      systemNotes: `${PARTIAL_REMAINDER_PAYMENT_MARKER} {"sourcePaymentId":548}`,
    });
    const standalonePayment = createPayment({ id: 901, systemNotes: 'manual note' });

    expect(isInvoiceManagedPayment(invoicePayment)).toBe(true);
    expect(isInvoiceManagedPayment(installmentPayment)).toBe(true);
    expect(isInvoiceManagedPayment(remainderPayment)).toBe(true);
    expect(isInvoiceManagedPayment(standalonePayment)).toBe(false);
  });

  it('detects transferred remainder payments even without an invoice link', () => {
    const remainderPayment = createPayment({
      invoiceId: null,
      systemNotes: `${PARTIAL_REMAINDER_PAYMENT_MARKER} {"sourcePaymentId":548}`,
    });

    expect(isTransferredRemainderPayment(remainderPayment)).toBe(true);
    expect(isInvoiceManagedPayment(remainderPayment)).toBe(false);
  });

  it('detects and parses partial remainder source payments', () => {
    const sourcePayment = createPayment({
      systemNotes: `${PARTIAL_REMAINDER_SOURCE_MARKER} {"sourcePaymentId":547,"sourceOriginalAmount":10000,"paidAmount":4000,"remainderAmount":6000,"remainderDate":"2026-05-20"}`,
    });

    expect(isPartialRemainderSourcePayment(sourcePayment)).toBe(true);
    expect(parsePartialRemainderTransferMetadata(sourcePayment)).toEqual({
      sourcePaymentId: 547,
      sourceOriginalAmount: 10_000,
      paidAmount: 4_000,
      remainderAmount: 6_000,
      remainderDate: '2026-05-20',
    });
  });

  it('detects partial payments that still need a separate remainder payment', () => {
    const partialPayment = createPayment({
      amount: 10_000,
      paidAmount: 4_000,
      status: 'Overdue',
      documentKind: 'Payment',
    });
    const alreadySplitSource = createPayment({
      amount: 10_000,
      paidAmount: 4_000,
      status: 'Overdue',
      systemNotes: `${PARTIAL_REMAINDER_SOURCE_MARKER} {"sourcePaymentId":900}`,
    });
    const invoiceDocument = createPayment({
      amount: 10_000,
      paidAmount: 4_000,
      status: 'Overdue',
      documentKind: 'Invoice',
    });

    expect(needsPartialRemainderTransfer(partialPayment)).toBe(true);
    expect(needsPartialRemainderTransfer(alreadySplitSource)).toBe(false);
    expect(needsPartialRemainderTransfer(invoiceDocument)).toBe(false);
  });

  it('hides the representative invoice row when an installment schedule exists', () => {
    const representativeInvoice = createPayment({
      id: 547,
      documentKind: 'Invoice',
      invoiceId: 30,
    });
    const installmentPayment = createPayment({
      id: 548,
      invoiceId: 30,
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1}`,
    });

    const result = filterRepresentativeInvoiceWhenInstallmentsExist([
      representativeInvoice,
      installmentPayment,
    ]);

    expect(result.map((payment) => payment.id)).toEqual([548]);
  });

  it('hides the representative invoice row when regular linked payments exist', () => {
    const representativeInvoice = createPayment({
      id: 547,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 135_000,
    });
    const linkedPayment = createPayment({
      id: 548,
      documentKind: 'Payment',
      invoiceId: 30,
      amount: 35_000,
    });

    const result = filterRepresentativeInvoiceWhenInstallmentsExist([
      representativeInvoice,
      linkedPayment,
    ]);

    expect(result.map((payment) => payment.id)).toEqual([548]);
  });

  it('keeps the representative row when converted legacy installments exceed it', () => {
    const firstInstallment = createPayment({
      id: 548,
      invoiceId: 30,
      amount: 50_000,
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1}`,
    });
    const secondInstallment = createPayment({
      id: 549,
      invoiceId: 30,
      amount: 50_000,
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":2}`,
    });
    const representativeInvoice = createPayment({
      id: 547,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 62_500,
    });

    const payments = [firstInstallment, secondInstallment, representativeInvoice];

    expect(filterRepresentativeInvoiceWhenInstallmentsExist(payments).map((payment) => payment.id)).toEqual([548, 549, 547]);
    expect(getStatusSourcePaymentsForInvoice(payments).map((payment) => payment.id)).toEqual([548, 549, 547]);
  });

  it('hides the representative row for a normal installment plan with overpay metadata', () => {
    const firstInstallment = createPayment({
      id: 548,
      invoiceId: 30,
      amount: 5_600,
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"Sequence":1,"ToPay":11200}`,
    });
    const secondInstallment = createPayment({
      id: 549,
      invoiceId: 30,
      amount: 5_600,
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"Sequence":2,"ToPay":11200}`,
    });
    const representativeInvoice = createPayment({
      id: 547,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 10_000,
    });

    const payments = [firstInstallment, secondInstallment, representativeInvoice];

    expect(filterRepresentativeInvoiceWhenInstallmentsExist(payments).map((payment) => payment.id)).toEqual([548, 549]);
    expect(getStatusSourcePaymentsForInvoice(payments).map((payment) => payment.id)).toEqual([548, 549]);
  });

  it('hides the representative invoice row for a legacy broken installment aggregate', () => {
    const representativeInvoice = createPayment({
      id: 547,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 205_000,
      date: '2026-08-25',
      plannedDate: '2025-10-24',
      accountDate: '2025-10-24',
    });
    const firstInstallment = createPayment({
      id: 548,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 18_000,
      plannedDate: '2025-11-27',
      date: '2025-11-27',
    });
    const secondInstallment = createPayment({
      id: 549,
      documentKind: 'Payment',
      invoiceId: 30,
      amount: 18_000,
      plannedDate: '2025-12-25',
      date: '2025-12-25',
    });

    const result = filterRepresentativeInvoiceWhenInstallmentsExist([
      representativeInvoice,
      firstInstallment,
      secondInstallment,
    ]);

    expect(result.map((payment) => payment.id)).toEqual([548, 549]);
  });

  it('uses the full legacy broken installment aggregate as the status source', () => {
    const representativeInvoice = createPayment({
      id: 547,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 205_000,
      date: '2026-08-25',
      plannedDate: '2025-10-24',
      accountDate: '2025-10-24',
      status: 'Pending',
    });
    const firstInstallment = createPayment({
      id: 548,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 18_000,
      date: '2025-11-27',
      plannedDate: '2025-11-27',
      status: 'Completed',
      paidAmount: 18_000,
      paidDate: '2025-11-27',
    });
    const secondInstallment = createPayment({
      id: 549,
      documentKind: 'Payment',
      invoiceId: 30,
      amount: 18_000,
      date: '2025-12-25',
      plannedDate: '2025-12-25',
      status: 'Completed',
      paidAmount: 18_000,
      paidDate: '2025-12-25',
    });
    const thirdInstallment = createPayment({
      id: 550,
      documentKind: 'Invoice',
      invoiceId: 30,
      amount: 18_000,
      date: '2026-01-30',
      plannedDate: '2026-01-30',
      status: 'Pending',
    });

    const result = getStatusSourcePaymentsForInvoice([
      representativeInvoice,
      firstInstallment,
      secondInstallment,
      thirdInstallment,
    ]);

    expect(result.map((payment) => payment.id)).toEqual([548, 549, 550]);
  });
});
