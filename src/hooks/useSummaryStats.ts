import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { SummaryStats, PeriodKey } from '../types';

export function useSummaryStats(params: {
  clientId?: number;
  caseId?: number;
  from?: string;
  to?: string;
  period?: PeriodKey;
  type?: 'Income' | 'Expense';
}) {
  const { clientId, caseId, from, to, period, type } = params;

  const [data, setData] = useState<SummaryStats | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetch = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await apiService.getSummaryStats({
        clientId,
        caseId,
        from,
        to,
        period,
        type,
      });
      setData(res);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to fetch summary');
    } finally {
      setLoading(false);
    }
  }, [clientId, caseId, from, to, period, type]);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      setLoading(true);
      setError(null);
      try {
        const res = await apiService.getSummaryStats({
          clientId,
          caseId,
          from,
          to,
          period,
          type,
        });
        if (!cancelled) setData(res);
      } catch (e: unknown) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to fetch summary');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [clientId, caseId, from, to, period, type]);

  return { data, loading, error, refresh: fetch };
}
