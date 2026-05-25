import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { LawyerWorkloadReport } from '../types';

export interface LawyerWorkloadReportFilters {
  asOf?: string;
  horizonDays?: number;
  lawyerUserId?: number;
  limit?: number;
}

export function useLawyerWorkloadReport(filters: LawyerWorkloadReportFilters) {
  const [report, setReport] = useState<LawyerWorkloadReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      asOf: filters.asOf,
      horizonDays: filters.horizonDays,
      lawyerUserId: filters.lawyerUserId,
      limit: filters.limit,
    }),
    [filters.asOf, filters.horizonDays, filters.lawyerUserId, filters.limit],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getLawyerWorkloadReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по загрузке юристов.');
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
