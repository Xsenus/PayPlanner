import { describe, expect, it } from 'vitest';
import type { LegalEntityBankAccount } from '../../types';
import {
  isActivePrimaryBankAccount,
  setBankAccountArchived,
  syncBankAccountPrimary,
  upsertBankAccount,
} from './legalEntityBankAccountUtils';

const account = (patch: Partial<LegalEntityBankAccount> = {}): LegalEntityBankAccount => ({
  bank: 'Банк',
  name: 'Счёт',
  bik: '044525225',
  currency: 'RUB',
  accountNumber: '40702810200000000001',
  accountType: 'Дополнительный',
  ...patch,
});

describe('legalEntityBankAccountUtils', () => {
  it('keeps one primary account and demotes the previous one when a new primary is added', () => {
    const list = [account({ accountNumber: '1', accountType: 'Основной', isPrimary: true })];

    const next = upsertBankAccount(list, null, account({ accountNumber: '2', accountType: 'Основной' }));

    expect(next.map((item) => item.isPrimary)).toEqual([false, true]);
    expect(next[0].accountType).toBe('Дополнительный');
    expect(next[1].accountType).toBe('Основной');
  });

  it('preserves id and archive flag when an account is edited', () => {
    const list = [account({ id: 'abc', accountNumber: '1' })];

    const next = upsertBankAccount(list, 0, account({ accountNumber: '9' }));

    expect(next[0]).toMatchObject({ id: 'abc', accountNumber: '9', isArchived: false });
  });

  it('archiving the primary account removes the primary mark', () => {
    const list = [account({ id: 'a', accountType: 'Основной', isPrimary: true })];

    const next = setBankAccountArchived(list, 0, true);

    expect(next[0]).toMatchObject({ isArchived: true, isPrimary: false, accountType: 'Дополнительный' });
    expect(isActivePrimaryBankAccount(next[0])).toBe(false);
  });

  it('restores from archive without silently taking the primary mark away from the other account', () => {
    const list = [
      account({ id: 'a', isArchived: true }),
      account({ id: 'b', accountType: 'Основной', isPrimary: true }),
    ];

    const next = setBankAccountArchived(list, 0, false);

    expect(next.map((item) => item.isPrimary)).toEqual([false, true]);
  });

  it('treats legacy accounts typed as primary as primary', () => {
    const next = syncBankAccountPrimary([account({ accountType: 'Основной' }), account()]);

    expect(next.map((item) => item.isPrimary)).toEqual([true, false]);
  });
});
