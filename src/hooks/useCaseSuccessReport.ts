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

export interface CaseSuccessReportOptions {
  enabled?: boolean;
}

export function useCaseSuccessReport(filters: CaseSuccessReportFilters, options: CaseSuccessReportOptions = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<CaseSuccessReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);
  const abortRef = useRef<AbortController | null>(null);

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
      const data = await apiService.getCaseSuccessReport(stableFilters, { signal: controller.signal });
      if (controller.signal.aborted || requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (controller.signal.aborted || requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить статистику успешности дел.');
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
