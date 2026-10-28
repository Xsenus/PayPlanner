import { toDateInputValue } from './dateUtils';

type BalanceIdentity = {
  date: string;
  source: string;
  accountName: string;
  balanceKey?: string | null;
};

const confirmedAmountFormatter = new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export function moneyBalanceComparisonKey(row: BalanceIdentity): string | null {
  const date = toDateInputValue(row.date);
  // Explicit null marks historical ownership that has not been established.
  // Only responses from the older API, without this field, use the old key.
  if ('balanceKey' in row) return row.balanceKey ? JSON.stringify(['scope', row.balanceKey, date]) : null;
  return JSON.stringify(['legacy', row.source, row.accountName, date]);
}

export function moneyBalanceCurrencyLabel(currency?: string | null): string {
  const normalized = currency?.trim().toUpperCase();
  return normalized === 'RUB' ? '₽' : normalized || 'валюта не указана';
}

export function isCashBookCurrencyConfirmed(status?: string, currency?: string | null): boolean {
  return status === 'empty' || status === 'single' && Boolean(currency?.trim());
}

export function formatConfirmedMoney(value: number, status?: string, currency?: string | null): string {
  if (!Number.isFinite(value)) return '—';
  const amount = confirmedAmountFormatter.format(value);
  if (status === 'empty' && value === 0) return amount;
  if (status !== 'single' || !currency?.trim()) return '—';
  return `${amount} ${moneyBalanceCurrencyLabel(currency)}`;
}
