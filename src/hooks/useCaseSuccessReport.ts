import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { CaseSuccessReport } from '../types';

export interface CaseSuccessReportFilters {
  from?: string;
  to?: string;
  status?: string;
  responsibleUserId?: number;
  caseType?: string;
  limit?: number;
}

export function useCaseSuccessReport(filters: CaseSuccessReportFilters) {
  const [report, setReport] = useState<CaseSuccessReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
      status: filters.status,
      responsibleUserId: filters.responsibleUserId,
      caseType: filters.caseType,
      limit: filters.limit,
    }),
    [filters.from, filters.to, filters.status, filters.responsibleUserId, filters.caseType, filters.limit],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getCaseSuccessReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить статистику успешности дел.');
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
