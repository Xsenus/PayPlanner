import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { CurrentCasesReport } from '../types';

export interface CurrentCasesReportFilters {
  asOf?: string;
  status?: string;
  responsibleUserId?: number;
  caseType?: string;
  limit?: number;
}

export interface CurrentCasesReportOptions {
  enabled?: boolean;
}

export function useCurrentCasesReport(filters: CurrentCasesReportFilters, options: CurrentCasesReportOptions = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<CurrentCasesReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      asOf: filters.asOf,
      status: filters.status,
      responsibleUserId: filters.responsibleUserId,
      caseType: filters.caseType,
      limit: filters.limit,
    }),
    [filters.asOf, filters.status, filters.responsibleUserId, filters.caseType, filters.limit],
  );

  const load = useCallback(async () => {
    if (!enabled) {
      requestIdRef.current += 1;
      setReport(null);
      setLoading(false);
      setError(null);
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getCurrentCasesReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по текущим делам.');
    } finally {
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [enabled, stableFilters]);

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
