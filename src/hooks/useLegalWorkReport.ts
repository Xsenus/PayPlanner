import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { LegalWorkReport } from '../types';

export interface LegalWorkReportFilters {
  from?: string;
  to?: string;
  status?: string;
  responsibleUserId?: number;
  caseType?: string;
}

export function useLegalWorkReport(filters: LegalWorkReportFilters) {
  const [report, setReport] = useState<LegalWorkReport | null>(null);
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
    }),
    [filters.from, filters.to, filters.status, filters.responsibleUserId, filters.caseType],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getLegalWorkReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по юридической работе.');
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
