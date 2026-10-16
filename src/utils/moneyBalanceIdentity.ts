import { toDateInputValue } from './dateUtils';

type BalanceIdentity = {
  date: string;
  source: string;
  accountName: string;
  balanceKey?: string | null;
};

export function moneyBalanceComparisonKey(row: BalanceIdentity): string | null {
  const date = toDateInputValue(row.date);
  // Explicit null marks historical ownership that has not been established.
  // Only responses from the older API, without this field, use the old key.
  if ('balanceKey' in row) return row.balanceKey ? JSON.stringify(['scope', row.balanceKey, date]) : null;
  return JSON.stringify(['legacy', row.source, row.accountName, date]);
}

export function moneyBalanceCurrencyLabel(currency?: string | null): string {
  if (currency === undefined) return '₽'; // Previous API responses.
  const normalized = currency?.trim().toUpperCase();
  return normalized === 'RUB' ? '₽' : normalized || 'валюта не указана';
}
