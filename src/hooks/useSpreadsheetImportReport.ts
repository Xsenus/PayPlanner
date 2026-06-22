import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { SpreadsheetImportReport } from '../types';

export interface SpreadsheetImportReportFilters {
  from?: string;
  to?: string;
}

export interface SpreadsheetImportReportOptions {
  enabled?: boolean;
}

export function useSpreadsheetImportReport(filters: SpreadsheetImportReportFilters, options: SpreadsheetImportReportOptions = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<SpreadsheetImportReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
    }),
    [filters.from, filters.to],
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
      const data = await apiService.getSpreadsheetImportReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по табличным импортам.');
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
