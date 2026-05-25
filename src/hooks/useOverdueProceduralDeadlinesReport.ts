import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { OverdueProceduralDeadlinesReport } from '../types';

export interface OverdueProceduralDeadlinesReportFilters {
  asOf?: string;
  deadlineType?: string;
  responsibleUserId?: number;
  limit?: number;
}

export function useOverdueProceduralDeadlinesReport(filters: OverdueProceduralDeadlinesReportFilters) {
  const [report, setReport] = useState<OverdueProceduralDeadlinesReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      asOf: filters.asOf,
      deadlineType: filters.deadlineType,
      responsibleUserId: filters.responsibleUserId,
      limit: filters.limit,
    }),
    [filters.asOf, filters.deadlineType, filters.responsibleUserId, filters.limit],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getOverdueProceduralDeadlinesReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по просроченным срокам.');
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
