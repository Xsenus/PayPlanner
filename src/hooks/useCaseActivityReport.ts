import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { CaseActivityReport } from '../types';

export interface CaseActivityReportFilters {
  inactiveDays?: number;
  escalationDays?: number;
  activity?: 'all' | 'inactive' | 'active' | 'escalated' | 'disabled';
  limit?: number;
}

export interface CaseActivityReportOptions {
  enabled?: boolean;
}

export function useCaseActivityReport(filters: CaseActivityReportFilters, options: CaseActivityReportOptions = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<CaseActivityReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);
  const abortRef = useRef<AbortController | null>(null);

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
    abortRef.current?.abort();
    abortRef.current = null;
    if (!enabled) {
      requestIdRef.current += 1;
      setReport(null);
      setLoading(false);
      setError(null);
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    const controller = new AbortController();
    abortRef.current = controller;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getCaseActivityReport(stableFilters, { signal: controller.signal });
      if (controller.signal.aborted || requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (controller.signal.aborted || requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по активности дел.');
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
      if (!controller.signal.aborted && requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [enabled, stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => () => {
    abortRef.current?.abort();
    abortRef.current = null;
    requestIdRef.current += 1;
  }, []);

  return {
    report,
    loading,
    error,
    reload: load,
  };
}
