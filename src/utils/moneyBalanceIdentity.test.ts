// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { formatConfirmedMoney, isCashBookCurrencyConfirmed, moneyBalanceComparisonKey, moneyBalanceCurrencyLabel } from './moneyBalanceIdentity';

const row = { date: '2026-10-16', source: 'Bank', accountName: 'Основной' };

describe('daily balance identity', () => {
  it('formats confirmed totals in their own currency and hides unconfirmed data', () => {
    expect(formatConfirmedMoney(80, 'single', 'USD')).toBe('80,00 USD');
    expect(formatConfirmedMoney(80, 'single', 'RUB')).toBe('80,00 ₽');
    expect(formatConfirmedMoney(0, 'empty', null)).toBe('0,00');
    expect(formatConfirmedMoney(80)).toBe('—');
    expect(formatConfirmedMoney(80, 'single', null)).toBe('—');
    expect(formatConfirmedMoney(Number.NaN, 'single', 'RUB')).toBe('—');
    expect(isCashBookCurrencyConfirmed('single', 'USD')).toBe(true);
    expect(isCashBookCurrencyConfirmed('empty', null)).toBe(true);
    expect(isCashBookCurrencyConfirmed('single', null)).toBe(false);
    expect(isCashBookCurrencyConfirmed()).toBe(false);
  });
  it('shows each currency explicitly and keeps unknown currency unknown', () => {
    expect(moneyBalanceCurrencyLabel('RUB')).toBe('₽');
    expect(moneyBalanceCurrencyLabel(' usd ')).toBe('USD');
    expect(moneyBalanceCurrencyLabel(null)).toBe('валюта не указана');
    expect(moneyBalanceCurrencyLabel(undefined)).toBe('валюта не указана');
  });
  it('keeps accounts and currencies with identical display names separate', () => {
    const keys = ['companyA:RUB', 'companyB:RUB', 'companyA:USD'].map(balanceKey =>
      moneyBalanceComparisonKey({ ...row, balanceKey }));
    expect(new Set(keys).size).toBe(3);
  });

  it('preserves a known account identity when it is renamed', () => {
    expect(moneyBalanceComparisonKey({ ...row, balanceKey: 'accountA' }))
      .toBe(moneyBalanceComparisonKey({ ...row, accountName: 'Новое имя', balanceKey: 'accountA' }));
  });

  it('does not associate an unattributed saved result by display name', () => {
    expect(moneyBalanceComparisonKey({ ...row, balanceKey: null })).toBeNull();
    expect(moneyBalanceComparisonKey({ ...row, balanceKey: '' })).toBeNull();
  });

  it('keeps calendar dates without a timezone shift', () => {
    expect(moneyBalanceComparisonKey({ ...row, balanceKey: 'accountA' }))
      .toBe(moneyBalanceComparisonKey({ ...row, date: '2026-10-16T00:00:00+07:00', balanceKey: 'accountA' }));
  });

  it('separates days for the same account', () => {
    expect(moneyBalanceComparisonKey({ ...row, balanceKey: 'accountA' }))
      .not.toBe(moneyBalanceComparisonKey({ ...row, date: '2026-10-17', balanceKey: 'accountA' }));
  });

  it('supports responses from the previous API without matching them to new identities', () => {
    expect(moneyBalanceComparisonKey(row)).toBe(moneyBalanceComparisonKey({ ...row }));
    expect(moneyBalanceComparisonKey(row)).not.toBe(moneyBalanceComparisonKey({ ...row, balanceKey: 'accountA' }));
    expect(moneyBalanceComparisonKey(row)).not.toBe(moneyBalanceComparisonKey({ ...row, source: 'Cash' }));
  });
});
