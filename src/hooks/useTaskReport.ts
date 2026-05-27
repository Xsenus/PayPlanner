import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { TaskReport } from '../types';

export type TaskReportScope = 'open' | 'closed' | 'all';

export interface TaskReportFilters {
  asOf?: string;
  scope?: TaskReportScope;
  assigneeUserId?: number;
  limit?: number;
}

export function useTaskReport(filters: TaskReportFilters) {
  const [report, setReport] = useState<TaskReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      asOf: filters.asOf,
      scope: filters.scope ?? 'open',
      assigneeUserId: filters.assigneeUserId,
      limit: filters.limit,
    }),
    [filters.asOf, filters.assigneeUserId, filters.limit, filters.scope],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getTaskReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по задачам.');
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
