import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { CaseActivityReport } from '../types';

export interface CaseActivityReportFilters {
  inactiveDays?: number;
  escalationDays?: number;
  activity?: 'all' | 'inactive' | 'active' | 'escalated' | 'disabled';
  limit?: number;
}

export function useCaseActivityReport(filters: CaseActivityReportFilters) {
  const [report, setReport] = useState<CaseActivityReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      inactiveDays: filters.inactiveDays,
      escalationDays: filters.escalationDays,
      activity: filters.activity,
      limit: filters.limit,
    }),
    [filters.inactiveDays, filters.escalationDays, filters.activity, filters.limit],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getCaseActivityReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по активности дел.');
    } finally {
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => () => {
    requestIdRef.current += 1;
  }, []);

  return {
    report,
    loading,
    error,
    reload: load,
  };
}
