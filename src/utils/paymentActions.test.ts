// @vitest-environment node

import { describe, expect, it } from 'vitest';

import type { Payment } from '../types';
import { buildCancelledPaymentPayload } from './paymentActions';

function createPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 339,
    date: '2026-01-30',
    plannedDate: '2026-01-30',
    amount: 15_000,
    paidAmount: 0,
    outstandingAmount: 15_000,
    hasPartialPayment: false,
    type: 'Expense',
    status: 'Overdue',
    description: 'Расход по делу',
    isPaid: false,
    notes: '',
    systemNotes: '',
    rescheduleCount: 0,
    createdAt: '2026-01-30T00:00:00Z',
    clientId: 42,
    clientCaseId: 53,
    ...overrides,
  };
}

describe('buildCancelledPaymentPayload', () => {
  it('keeps expense attribution when cancelling a legal-entity expense payment', () => {
    const payload = buildCancelledPaymentPayload(createPayment({
      clientId: 77,
      legalEntityId: 7,
      expenseLegalEntityId: 7,
    }));

    expect(payload.status).toBe('Cancelled');
    expect(payload.expenseClientId).toBeUndefined();
    expect(payload.expenseLegalEntityId).toBe(7);
    expect(payload.legalEntityId).toBe(7);
  });

  it('uses legacy clientId as expenseClientId when an expense payment has a case but no expense client', () => {
    const payload = buildCancelledPaymentPayload(createPayment({
      expenseClientId: null,
    }));

    expect(payload.status).toBe('Cancelled');
    expect(payload.expenseClientId).toBe(42);
    expect(payload.clientCaseId).toBe(53);
  });
});
