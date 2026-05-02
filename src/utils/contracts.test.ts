import { describe, expect, it } from 'vitest';
import { getContractReconciliationWarnings } from './contracts';

describe('getContractReconciliationWarnings', () => {
  it('returns warnings when fixed contract differs from invoices or acts by more than five percent', () => {
    const warnings = getContractReconciliationWarnings({
      billingMode: 'Fixed',
      amount: 100_000,
      invoiceAmountTotal: 112_000,
      actAmountTotal: 90_000,
    });

    expect(warnings).toEqual([
      {
        kind: 'invoices',
        label: 'Сумма счетов выше суммы договора на 12%',
        amount: 112_000,
        difference: 12_000,
        percent: 12,
      },
      {
        kind: 'acts',
        label: 'Сумма актов ниже суммы договора на 10%',
        amount: 90_000,
        difference: -10_000,
        percent: 10,
      },
    ]);
  });

  it('does not warn for actual contracts or differences within threshold', () => {
    expect(
      getContractReconciliationWarnings({
        billingMode: 'Actual',
        amount: 100_000,
        invoiceAmountTotal: 200_000,
        actAmountTotal: 0,
      }),
    ).toEqual([]);

    expect(
      getContractReconciliationWarnings({
        billingMode: 'Fixed',
        amount: 100_000,
        invoiceAmountTotal: 105_000,
        actAmountTotal: 95_000,
      }),
    ).toEqual([]);
  });
});
