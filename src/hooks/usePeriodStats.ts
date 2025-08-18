import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Payment, PaymentStatus, MonthlyStats } from '../types';

type YM = `${number}-${string}`; // 'YYYY-MM'
type StatusKey = Extract<PaymentStatus, 'Completed' | 'Pending' | 'Overdue'>;

export type TypeBucket = {
  type: 'Income' | 'Expense';
  totalAmount: number;
  totalCount: number;
  byStatus: Array<{
    key: StatusKey;
    count: number;
    amount: number;
    percent: number; // 0..100
  }>;
};

export interface PeriodResult {
  stats: MonthlyStats | null; // агрегат по периоду в твоём формате
  types: { income: TypeBucket; expense: TypeBucket } | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

/** UTC-гранцы для периода по YM включая оба месяца */
function monthRangeToUtc(fromYM: YM, toYM: YM) {
  const [fy, fm] = fromYM.split('-').map(Number);
  const [ty, tm] = toYM.split('-').map(Number);
  const from = new Date(Date.UTC(fy, (fm ?? 1) - 1, 1, 0, 0, 0, 0));
  // конец месяца: день 0 следующего месяца = последний день текущего
  const to = new Date(Date.UTC(ty, tm ?? 1, 0, 23, 59, 59, 999));
  return { fromISO: from.toISOString(), toISO: to.toISOString() };
}

export function usePeriodStats(fromYM: YM, toYM: YM, pollInterval?: number): PeriodResult {
  const [stats, setStats] = useState<MonthlyStats | null>(null);
  const [types, setTypes] = useState<{ income: TypeBucket; expense: TypeBucket } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const timerRef = useRef<number | null>(null);

  const computeTypes = (payments: Payment[]): { income: TypeBucket; expense: TypeBucket } => {
    const STATUSES: readonly StatusKey[] = ['Completed', 'Pending', 'Overdue'] as const;

    const build = (type: 'Income' | 'Expense'): TypeBucket => {
      const items = payments.filter((p) => p.type === type);
      const totalCount = items.length;
      const totalAmount = items.reduce((s, p) => s + (p.amount ?? 0), 0);

      const byStatus = STATUSES.map((st) => {
        const list = items.filter((p) => p.status === st);
        const count = list.length;
        const amount = list.reduce((s, p) => s + (p.amount ?? 0), 0);
        const percent = totalCount ? Math.round((count / totalCount) * 100) : 0;
        return { key: st, count, amount, percent };
      });

      return { type, totalAmount, totalCount, byStatus };
    };

    return { income: build('Income'), expense: build('Expense') };
  };

  const fetchAll = useCallback(
    async (silent = false) => {
      try {
        if (!silent) setLoading(true);
        setError(null);

        const { fromISO, toISO } = monthRangeToUtc(fromYM, toYM);
        const payments: Payment[] = await apiService.getPayments(fromISO, toISO);

        const income = payments
          .filter((p) => p.type === 'Income' && p.status === 'Completed')
          .reduce((s, p) => s + (p.amount ?? 0), 0);

        const expense = payments
          .filter((p) => p.type === 'Expense' && p.status === 'Completed')
          .reduce((s, p) => s + (p.amount ?? 0), 0);

        const profit = income - expense;

        const completed = payments.filter((p) => p.status === 'Completed').length;
        const pending = payments.filter((p) => p.status === 'Pending').length;
        const overdue = payments.filter((p) => p.status === 'Overdue').length;
        const total = payments.length;
        const completionRate = total ? Math.round((completed / total) * 100) : 0;

        const aggregated: MonthlyStats = {
          income,
          expense,
          profit,
          completionRate,
          counts: { completed, pending, overdue, total },
        };

        setStats(aggregated);
        setTypes(computeTypes(payments));
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to fetch period stats');
        setStats(null);
        setTypes(null);
      } finally {
        if (!silent) setLoading(false);
      }
    },
    [fromYM, toYM],
  );

  useEffect(() => {
    fetchAll(false);
  }, [fetchAll]);

  useEffect(() => {
    if (!pollInterval) return;
    if (timerRef.current) window.clearInterval(timerRef.current);
    timerRef.current = window.setInterval(() => {
      void fetchAll(true);
    }, pollInterval);
    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
      timerRef.current = null;
    };
  }, [fetchAll, pollInterval]);

  return { stats, types, loading, error, refresh: () => fetchAll(false) };
}
