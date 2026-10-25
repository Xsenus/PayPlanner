// @vitest-environment node

import { describe, expect, it } from 'vitest';
import type { MoneyAccount } from '../types';
import { canEditMoneyAccountHistory, initialMoneyAccount } from './moneyAccountSelection';

const account = (id: number, companyId: number | null, isDefault = false): MoneyAccount => ({
  id, companyId, name: 'Основной счёт', source: 'Bank', currency: 'RUB', isDefault, isActive: true,
  createdAt: '2026-10-01T00:00:00Z',
});

describe('initial money account selection', () => {
  it('keeps an explicit journal selection across company defaults', () => {
    const accounts = [account(1, 11, true), account(2, 22, true)];
    expect(initialMoneyAccount(accounts, 2)?.id).toBe(2);
  });

  it('requires a company choice even if only one of two companies has a default', () => {
    expect(initialMoneyAccount([account(1, 11, true), account(2, 22)])).toBeUndefined();
  });

  it('does not guess a company when the preferred account disappeared', () => {
    expect(initialMoneyAccount([account(1, 11, true)], 99)).toBeUndefined();
  });

  it('uses the default inside a single company', () => {
    expect(initialMoneyAccount([account(1, 11), account(2, 11, true)])?.id).toBe(2);
  });

  it('uses the first eligible account when one company has no default', () => {
    expect(initialMoneyAccount([account(1, 11), account(2, 11)])?.id).toBe(1);
  });

  it('never automatically assigns an unknown historical company', () => {
    expect(initialMoneyAccount([account(1, null, true)], 1)).toBeUndefined();
  });

  it('ignores inactive accounts including an explicitly selected inactive default', () => {
    expect(initialMoneyAccount([{ ...account(1, 22, true), isActive: false }, account(2, 11)], 1)).toBeUndefined();
  });

  it('does not replace a selected historical account with a known company default', () => {
    expect(initialMoneyAccount([account(1, null, true), account(2, 11, true)], 1)).toBeUndefined();
  });

  it('does not select a server-blocked default even when its local active flag is true', () => {
    const blocked = Object.assign(account(1, 11, true), { canUseForNewOperations: false });
    expect(initialMoneyAccount([blocked, account(2, 11)])?.id).toBe(2);
  });

  it('does not replace an explicitly selected server-blocked account with another company account', () => {
    const blocked = Object.assign(account(1, 11, true), { canUseForNewOperations: false });
    expect(initialMoneyAccount([blocked, account(2, 22, true)], 1)).toBeUndefined();
  });

  it('never treats a server availability flag as an owner for unknown historical data', () => {
    const unknown = Object.assign(account(1, null, true), { canUseForNewOperations: true });
    expect(initialMoneyAccount([unknown], 1)).toBeUndefined();
  });

  it('can keep descriptions editable when the bank is closed only for new operations', () => {
    const historical = Object.assign(account(1, 11), {
      canUseForNewOperations: false, canEditExistingOperations: true,
    });
    expect(canEditMoneyAccountHistory(historical)).toBe(true);
  });

  it('respects the server refusal to edit an otherwise active historical account', () => {
    expect(canEditMoneyAccountHistory(Object.assign(account(1, 11), { canEditExistingOperations: false }))).toBe(false);
  });

  it('never enables historical editing for an inactive or unattributed account', () => {
    expect(canEditMoneyAccountHistory(Object.assign(account(1, 11), { isActive: false, canEditExistingOperations: true }))).toBe(false);
    expect(canEditMoneyAccountHistory(Object.assign(account(1, null), { canEditExistingOperations: true }))).toBe(false);
  });
});
