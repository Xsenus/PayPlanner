import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { MonthlyStats, PaymentStatus, SummaryBucket, SummaryStats } from '../types';
import { toMonthlyStats } from '../utils/statsAdapters';

type YM = `${number}-${string}`;
type StatusKey = Extract<PaymentStatus, 'Completed' | 'Pending' | 'Overdue'>;

export type TypeBucket = {
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

export interface PeriodResult {
  stats: MonthlyStats | null;
  types: { income: TypeBucket; expense: TypeBucket } | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

export interface PeriodStatsOptions {
  enabled?: boolean;
  pollInterval?: number;
}

function normalizeRange(fromYM: YM, toYM: YM): { fromYM: YM; toYM: YM } {
  return fromYM <= toYM ? { fromYM, toYM } : { fromYM: toYM, toYM: fromYM };
}

function monthRangeToApiDates(fromYM: YM, toYM: YM) {
  const normalized = normalizeRange(fromYM, toYM);
  const [fromYear, fromMonth] = normalized.fromYM.split('-').map(Number);
  const [toYear, toMonth] = normalized.toYM.split('-').map(Number);
  const monthDays = new Date(toYear, toMonth, 0).getDate();

  return {
    from: `${fromYear}-${String(fromMonth).padStart(2, '0')}-01`,
    to: `${toYear}-${String(toMonth).padStart(2, '0')}-${String(monthDays).padStart(2, '0')}`,
  };
}

function buildTypeBucket(bucket: SummaryBucket, type: 'Income' | 'Expense'): TypeBucket {
  const totalCount = bucket.totalCount ?? 0;
  const toPercent = (count: number) => (totalCount > 0 ? Math.round((count / totalCount) * 100) : 0);

  return {
    type,
    totalAmount: bucket.totalAmount ?? 0,
    totalCount,
    byStatus: [
      {
        key: 'Completed',
        count: bucket.completedCount ?? 0,
        amount: bucket.completedAmount ?? 0,
        percent: toPercent(bucket.completedCount ?? 0),
      },
      {
        key: 'Pending',
        count: bucket.pendingCount ?? 0,
        amount: bucket.pendingAmount ?? 0,
        percent: toPercent(bucket.pendingCount ?? 0),
      },
      {
        key: 'Overdue',
        count: bucket.overdueCount ?? 0,
        amount: bucket.overdueAmount ?? 0,
        percent: toPercent(bucket.overdueCount ?? 0),
      },
    ],
  };
}

export function usePeriodStats(fromYM: YM, toYM: YM, options: PeriodStatsOptions = {}): PeriodResult {
  const enabled = options.enabled ?? true;
  const pollInterval = options.pollInterval;
  const [stats, setStats] = useState<MonthlyStats | null>(null);
  const [types, setTypes] = useState<{ income: TypeBucket; expense: TypeBucket } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const timerRef = useRef<number | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const fetchAll = useCallback(
    async (silent = false) => {
      if (!enabled) {
        abortRef.current?.abort();
        abortRef.current = null;
        setStats(null);
        setTypes(null);
        setError(null);
        setLoading(false);
        return;
      }

      if (silent && abortRef.current) {
        return;
      }

      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;

      try {
        if (!silent) setLoading(true);
        setError(null);

        const range = monthRangeToApiDates(fromYM, toYM);
        const summary: SummaryStats = await apiService.getSummaryStats(range, { signal: controller.signal });
        if (controller.signal.aborted) return;

        setStats(toMonthlyStats(summary));
        setTypes({
          income: buildTypeBucket(summary.income, 'Income'),
          expense: buildTypeBucket(summary.expense, 'Expense'),
        });
      } catch (e) {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : 'Failed to fetch period stats');
        setStats(null);
        setTypes(null);
      } finally {
        if (abortRef.current === controller) {
          abortRef.current = null;
        }
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    },
    [enabled, fromYM, toYM],
  );

  useEffect(() => {
    void fetchAll(false);
    return () => {
      abortRef.current?.abort();
    };
  }, [fetchAll]);

  useEffect(() => {
    if (!pollInterval) return;

    if (timerRef.current) {
      window.clearInterval(timerRef.current);
    }

    timerRef.current = window.setInterval(() => {
      void fetchAll(true);
    }, pollInterval);

    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
      timerRef.current = null;
    };
  }, [fetchAll, pollInterval]);

  return { stats, types, loading, error, refresh: () => void fetchAll(false) };
}
