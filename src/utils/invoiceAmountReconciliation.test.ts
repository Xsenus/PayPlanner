import { describe, expect, it } from 'vitest';
import type { Payment } from '../types';
import { INSTALLMENT_PAYMENT_MARKER } from './invoicePayments';
import { buildInvoiceAmountReconciliation } from './invoiceAmountReconciliation';

function createPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 100,
    date: '2026-04-15',
    plannedDate: '2026-04-15',
    amount: 10_000,
    type: 'Income',
    status: 'Pending',
    description: 'Платеж по счету',
    isPaid: false,
    notes: '',
    systemNotes: '',
    rescheduleCount: 0,
    paidAmount: 0,
    outstandingAmount: 10_000,
    hasPartialPayment: false,
    createdAt: '2026-04-15T00:00:00Z',
    documentKind: 'Payment',
    invoiceId: 30,
    account: '30',
    accountDate: '2026-04-08',
    ...overrides,
  } as Payment;
}

describe('buildInvoiceAmountReconciliation', () => {
  it('does not warn when invoice and payment totals match', () => {
    const result = buildInvoiceAmountReconciliation(
      [
        createPayment({ id: 1, amount: 15_000, outstandingAmount: 15_000 }),
        createPayment({ id: 2, amount: 15_000, outstandingAmount: 15_000 }),
      ],
      30_000,
      30_000,
    );

    expect(result).toBeNull();
  });

  it('returns an increase warning when invoice amount is greater than issued payments', () => {
    const result = buildInvoiceAmountReconciliation(
      [
        createPayment({ id: 1, amount: 15_000, outstandingAmount: 15_000 }),
        createPayment({ id: 2, amount: 15_000, outstandingAmount: 15_000 }),
      ],
      30_000,
      45_000,
    );

    expect(result).toMatchObject({
      direction: 'increase',
      invoiceAmount: 45_000,
      plannedAmount: 30_000,
      paidAmount: 0,
      difference: 15_000,
      sourcePaymentCount: 2,
    });
  });

  it('returns a decrease warning when payments or actual receipts exceed the invoice amount', () => {
    const result = buildInvoiceAmountReconciliation(
      [
        createPayment({
          id: 1,
          amount: 20_000,
          outstandingAmount: 0,
          paidAmount: 20_000,
          status: 'Completed',
          isPaid: true,
        }),
      ],
      20_000,
      12_000,
    );

    expect(result).toMatchObject({
      direction: 'decrease',
      invoiceAmount: 12_000,
      plannedAmount: 20_000,
      paidAmount: 20_000,
      difference: 8_000,
    });
  });

  it('uses status source payments and ignores the representative invoice row when installments exist', () => {
    const result = buildInvoiceAmountReconciliation(
      [
        createPayment({
          id: 30,
          documentKind: 'Invoice',
          amount: 165_000,
          outstandingAmount: 165_000,
        }),
        createPayment({
          id: 31,
          amount: 15_000,
          outstandingAmount: 15_000,
          systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1}`,
        }),
        createPayment({
          id: 32,
          amount: 15_000,
          outstandingAmount: 15_000,
          systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":2}`,
        }),
      ],
      165_000,
      30_000,
    );

    expect(result).toBeNull();
  });

  it('compares installment rows by principal so interest overpayment does not block save', () => {
    const firstMetadata = `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1,"total":10000,"downPayment":0,"annualRate":12,"months":2,"startDate":"2026-05-10","roundingMode":"none","toPay":11200,"overpay":1200,"item":{"sequence":1,"date":"2026-05-10","principal":5000,"interest":600,"payment":5600,"balance":5000}}`;
    const secondMetadata = `${INSTALLMENT_PAYMENT_MARKER} {"sequence":2,"total":10000,"downPayment":0,"annualRate":12,"months":2,"startDate":"2026-05-10","roundingMode":"none","toPay":11200,"overpay":1200,"item":{"sequence":2,"date":"2026-06-10","principal":5000,"interest":600,"payment":5600,"balance":0}}`;

    const result = buildInvoiceAmountReconciliation(
      [
        createPayment({ id: 1, amount: 5_600, outstandingAmount: 5_600, systemNotes: firstMetadata }),
        createPayment({ id: 2, amount: 5_600, outstandingAmount: 5_600, systemNotes: secondMetadata }),
      ],
      10_000,
      10_000,
    );

    expect(result).toBeNull();
  });
});
