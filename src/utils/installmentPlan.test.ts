import { describe, expect, it } from 'vitest';

import type { Payment } from '../types';
import {
  INSTALLMENT_PAYMENT_MARKER,
  deriveInstallmentPlanFromPayments,
} from './installmentPlan';

function createPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 1,
    date: '2026-05-10',
    plannedDate: '2026-05-10',
    amount: 5600,
    type: 'Income',
    status: 'Pending',
    isPaid: false,
    description: 'Installment payment',
    createdAt: '2026-05-01T00:00:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    systemNotes: '',
    ...overrides,
  } as Payment;
}

describe('installmentPlan', () => {
  it('returns null when payments do not contain installment metadata', () => {
    const result = deriveInstallmentPlanFromPayments([
      createPayment({
        id: 10,
        description: 'Regular payment',
      }),
    ]);

    expect(result).toBeNull();
  });

  it('rebuilds the installment plan from sibling installment payments and ignores the invoice record', () => {
    const invoiceRecord = createPayment({
      id: 24,
      documentKind: 'Invoice',
      amount: 10000,
      description: 'Invoice #24',
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} {"sequence":99}`,
    });

    const firstInstallment = createPayment({
      id: 25,
      date: '2026-05-10',
      plannedDate: '2026-05-10',
      amount: 5600,
      description: 'Installment 1/2',
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} ${JSON.stringify({
        sequence: 1,
        total: 10000,
        downPayment: 0,
        annualRate: 0,
        months: 2,
        startDate: '2026-05-10',
        roundingMode: 'none',
        roundingStep: null,
        toPay: 11200,
        overpay: 1200,
        loanAmount: 10000,
        baseMonthlyPayment: 5600,
        roundedMonthlyPayment: 5600,
        item: {
          sequence: 1,
          date: '2026-05-10',
          principal: 5000,
          interest: 600,
          payment: 5600,
          balance: 5000,
        },
      })}`,
    });

    const secondInstallment = createPayment({
      id: 26,
      date: '2026-06-10',
      plannedDate: '2026-06-10',
      amount: 5600,
      description: 'Installment 2/2',
      systemNotes: `${INSTALLMENT_PAYMENT_MARKER} ${JSON.stringify({
        sequence: 2,
        total: 10000,
        downPayment: 0,
        annualRate: 0,
        months: 2,
        startDate: '2026-05-10',
        roundingMode: 'none',
        roundingStep: null,
        toPay: 11200,
        overpay: 1200,
        loanAmount: 10000,
        baseMonthlyPayment: 5600,
        roundedMonthlyPayment: 5600,
        item: {
          sequence: 2,
          date: '2026-06-10',
          principal: 5000,
          interest: 600,
          payment: 5600,
          balance: 0,
        },
      })}`,
    });

    const result = deriveInstallmentPlanFromPayments([
      secondInstallment,
      invoiceRecord,
      firstInstallment,
    ]);

    expect(result).not.toBeNull();
    expect(result).toMatchObject({
      total: 10000,
      months: 2,
      startDate: '2026-05-10',
      toPay: 11200,
      overpay: 1200,
      items: [
        expect.objectContaining({
          sequence: 1,
          date: '2026-05-10',
          payment: 5600,
        }),
        expect.objectContaining({
          sequence: 2,
          date: '2026-06-10',
          payment: 5600,
        }),
      ],
    });
  });
});
