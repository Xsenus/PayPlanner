import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { MonthlyStats } from '../types';

interface Options {
  pollInterval?: number;
}

export function useMonthlyStats(year: number, month: number, opts?: Options) {
  const [stats, setStats] = useState<MonthlyStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const timerRef = useRef<number | null>(null);

  const fetchStats = useCallback(
    async (silent = false) => {
      try {
        if (!silent) setLoading(true);
        setError(null);
        const data = await apiService.getMonthlyStats(year, month);
        setStats(data);
      } catch (err: unknown) {
        setError(err instanceof Error ? err.message : 'Failed to fetch stats');
      } finally {
        if (!silent) setLoading(false);
      }
    },
    [year, month],
  );

  useEffect(() => {
    fetchStats(false);
  }, [fetchStats]);

  useEffect(() => {
    if (!opts?.pollInterval) return;
    if (timerRef.current) window.clearInterval(timerRef.current);
    timerRef.current = window.setInterval(() => {
      void fetchStats(true);
    }, opts.pollInterval);
    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
      timerRef.current = null;
    };
  }, [fetchStats, opts?.pollInterval]);

  return { stats, loading, error, refresh: () => fetchStats(false) };
}
