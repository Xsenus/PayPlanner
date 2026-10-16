import type { MoneyAccount } from '../types';

export function moneyAccountLabel(account: Pick<MoneyAccount, 'name' | 'companyName'>): string {
  return account.companyName?.trim() ? `${account.name} · ${account.companyName.trim()}` : account.name;
}

export function canUseMoneyAccountForNewOperation(account: MoneyAccount): boolean {
  // Older API versions omit the computed flag. An explicit server refusal must
  // never fall back to the old local active-only choice.
  return account.isActive && (account.companyId ?? 0) > 0 && account.canUseForNewOperations !== false;
}

export function canEditMoneyAccountHistory(account: MoneyAccount): boolean {
  return account.isActive && (account.companyId ?? 0) > 0 && account.canEditExistingOperations !== false;
}

export function initialMoneyAccount(accounts: readonly MoneyAccount[], preferredId?: number | null): MoneyAccount | undefined {
  const eligible = accounts.filter(canUseMoneyAccountForNewOperation);
  if (preferredId != null) return eligible.find(account => account.id === preferredId);
  const company = eligible[0]?.companyId;
  if (eligible.some(account => account.companyId !== company)) return undefined;
  return eligible.find(account => account.isDefault) ?? eligible[0];
}
