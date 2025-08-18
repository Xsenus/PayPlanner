import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Payment, PaymentStatus } from '../types';

// только те статусы, что нужны для панели
type StatusKey = Extract<PaymentStatus, 'Completed' | 'Pending' | 'Overdue'>;

export type TypeStats = {
  type: 'Income' | 'Expense';
  totalAmount: number;
  totalCount: number;
  byStatus: Array<{
    key: StatusKey;
    count: number;
    amount: number;
    percent: number;
  }>;
};

interface Options {
  pollInterval?: number;
}

export function useMonthlyTypeStats(year: number, month: number, opts?: Options) {
  const [data, setData] = useState<{ income: TypeStats; expense: TypeStats } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const timerRef = useRef<number | null>(null);

  const fetchStats = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const from = new Date(Date.UTC(year, month - 1, 1)).toISOString();
      const to = new Date(Date.UTC(year, month, 0, 23, 59, 59, 999)).toISOString();

      const payments: Payment[] = await apiService.getPayments(from, to);

      const STATUSES: readonly StatusKey[] = ['Completed', 'Pending', 'Overdue'] as const;

      const compute = (type: 'Income' | 'Expense'): TypeStats => {
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

      setData({ income: compute('Income'), expense: compute('Expense') });
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to fetch payments');
    } finally {
      setLoading(false);
    }
  }, [year, month]);

  useEffect(() => {
    fetchStats();
  }, [fetchStats]);

  useEffect(() => {
    if (!opts?.pollInterval) return;
    if (timerRef.current) window.clearInterval(timerRef.current);
    timerRef.current = window.setInterval(fetchStats, opts.pollInterval);
    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
      timerRef.current = null;
    };
  }, [fetchStats, opts?.pollInterval]);

  return { data, loading, error, refresh: fetchStats };
}
