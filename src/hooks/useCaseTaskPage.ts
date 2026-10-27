import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { TaskJournalItem } from '../utils/taskJournalStore';

export type CaseTaskFilter = 'active' | 'all' | 'overdue' | 'done';
export type CaseTaskCounts = Record<CaseTaskFilter, number>;
export const CASE_TASK_PAGE_SIZES = [10, 25, 50, 100];

type Params = { caseId: number; enabled: boolean; filter: CaseTaskFilter; revision: number; accessKey?: string };
type PageState = { key: string; items: TaskJournalItem[]; total: number; loading: boolean; error: boolean };
type SummaryState = { key: string; counts: CaseTaskCounts | null; loading: boolean; error: boolean };

/** Separate table reads from document-source tasks; late responses cannot replace a newer query. */
export function useCaseTaskPage({ caseId, enabled, filter, revision, accessKey = 'default' }: Params) {
  const scope = JSON.stringify([caseId, filter, accessKey]);
  const [paging, setPaging] = useState({ scope, page: 1, pageSize: 50 });
  const [retry, setRetry] = useState(0);
  const page = paging.scope === scope ? paging.page : 1;
  const pageSize = paging.pageSize;
  const summaryKey = JSON.stringify([caseId, revision, retry, accessKey]);
  const queryKey = `${summaryKey}:${filter}:${page}:${pageSize}`;
  const [pageState, setPageState] = useState<PageState | null>(null);
  const [summaryState, setSummaryState] = useState<SummaryState | null>(null);

  useEffect(() => {
    setPaging((current) => current.scope === scope ? current : { ...current, scope, page: 1 });
  }, [scope]);

  const changePage = useCallback((next: number) => {
    if (!Number.isInteger(next) || next < 1) return;
    setPaging((current) => ({ ...current, scope, page: next }));
  }, [scope]);
  const changePageSize = useCallback((next: number) => {
    if (!CASE_TASK_PAGE_SIZES.includes(next)) return;
    setPaging({ scope, page: 1, pageSize: next });
  }, [scope]);
  const refresh = useCallback(() => setRetry((current) => current + 1), []);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    setPageState((current) => ({
      key: queryKey,
      items: current?.key === queryKey ? current.items : [],
      total: current?.key === queryKey ? current.total : 0,
      loading: true,
      error: false,
    }));
    const status = filter === 'done' ? 'completed' : filter;
    apiService.getTaskJournalItems({ clientCaseId: caseId, status, page, pageSize, sortBy: 'dueAt', sortDir: 'asc' }, { signal: controller.signal })
      .then((result) => {
        if (controller.signal.aborted) return;
        if (!Number.isSafeInteger(result.total) || result.total < 0) throw new Error('Invalid task total');
        const lastPage = Math.max(1, Math.ceil(result.total / pageSize));
        if (page > lastPage) {
          changePage(lastPage);
          return;
        }
        setPageState({ key: queryKey, items: result.items ?? [], total: result.total, loading: false, error: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) setPageState({ key: queryKey, items: [], total: 0, loading: false, error: true });
      });
    return () => controller.abort();
  }, [caseId, changePage, enabled, filter, page, pageSize, queryKey]);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    setSummaryState({ key: summaryKey, counts: null, loading: true, error: false });
    apiService.getTaskJournalSummary({ clientCaseId: caseId }, { signal: controller.signal })
      .then((result) => {
        if (controller.signal.aborted) return;
        const { total, completed, active, overdue } = result;
        if (typeof total !== 'number' || typeof completed !== 'number'
          || ![total, completed, active, overdue].every((value) => Number.isSafeInteger(value) && value >= 0)
          || completed > total || active > total || overdue > active) throw new Error('Invalid task summary');
        setSummaryState({ key: summaryKey, counts: { all: total, done: completed, active, overdue }, loading: false, error: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) setSummaryState({ key: summaryKey, counts: null, loading: false, error: true });
      });
    return () => controller.abort();
  }, [caseId, enabled, summaryKey]);

  const currentPage = enabled && pageState?.key === queryKey ? pageState : null;
  const currentSummary = enabled && summaryState?.key === summaryKey ? summaryState : null;
  return {
    items: currentPage?.items ?? [],
    total: currentPage?.total ?? 0,
    counts: currentSummary?.counts ?? null,
    loading: enabled && (!currentPage || currentPage.loading),
    summaryLoading: enabled && (!currentSummary || currentSummary.loading),
    error: currentPage?.error ?? false,
    summaryError: currentSummary?.error ?? false,
    page,
    pageSize,
    totalPages: Math.max(1, Math.ceil((currentPage?.total ?? 0) / pageSize)),
    setPage: changePage,
    setPageSize: changePageSize,
    refresh,
  };
}
