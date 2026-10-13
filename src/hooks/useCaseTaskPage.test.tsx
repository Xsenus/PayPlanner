import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService, type TaskJournalListSummary } from '../services/api';
import type { TaskJournalItem } from '../utils/taskJournalStore';
import { useCaseTaskPage, type CaseTaskFilter } from './useCaseTaskPage';

vi.mock('../services/api', () => ({ apiService: { getTaskJournalItems: vi.fn(), getTaskJournalSummary: vi.fn() } }));

const task = (id: number) => ({ id, title: `Задача ${id}` }) as TaskJournalItem;
const page = (ids: number[], total = 59, pageNumber = 1) => ({ items: ids.map(task), total, page: pageNumber, pageSize: 50 });
const summary: TaskJournalListSummary = { total: 61, completed: 2, active: 59, overdue: 3, today: 0, highPriority: 0, unbilled: 0 };
const initial = { caseId: 15, enabled: true, filter: 'active' as CaseTaskFilter, revision: 0 };
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

describe('useCaseTaskPage', () => {
  beforeEach(() => {
    vi.mocked(apiService.getTaskJournalItems).mockReset().mockResolvedValue(page([1]));
    vi.mocked(apiService.getTaskJournalSummary).mockReset().mockResolvedValue(summary);
  });

  it('loads a later page with server filtering and keeps whole-case counters without rereading summary', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockImplementation(async (query) => query?.page === 2 ? page([51, 59], 59, 2) : page([1]));
    const { result } = renderHook(() => useCaseTaskPage(initial));
    await waitFor(() => expect(result.current.counts).toEqual({ all: 61, done: 2, active: 59, overdue: 3 }));
    act(() => result.current.setPage(2));
    await waitFor(() => expect(result.current.items.map((item) => item.id)).toEqual([51, 59]));
    expect(result.current.total).toBe(59);
    expect(apiService.getTaskJournalItems).toHaveBeenLastCalledWith(
      { clientCaseId: 15, status: 'active', page: 2, pageSize: 50, sortBy: 'dueAt', sortDir: 'asc' },
      { signal: expect.any(AbortSignal) },
    );
    expect(apiService.getTaskJournalSummary).toHaveBeenCalledTimes(1);
  });

  it('resets the page on each filter transition and requests done together with cancelled', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockImplementation(async (query) => query?.status === 'completed' ? page([60, 61], 2) : page([query?.page ?? 1]));
    const view = renderHook((params) => useCaseTaskPage(params), { initialProps: initial });
    await waitFor(() => expect(view.result.current.loading).toBe(false));
    act(() => view.result.current.setPage(2));
    await waitFor(() => expect(view.result.current.items[0].id).toBe(2));
    view.rerender({ ...initial, filter: 'done' });
    await waitFor(() => expect(view.result.current.items.map((item) => item.id)).toEqual([60, 61]));
    expect(view.result.current.page).toBe(1);
    view.rerender(initial);
    await waitFor(() => expect(view.result.current.items[0].id).toBe(1));
    expect(view.result.current.page).toBe(1);
    expect(apiService.getTaskJournalSummary).toHaveBeenCalledTimes(1);
  });

  it.each(['success', 'failure'] as const)('ignores old case %s and aborts its reads while the new case is pending', async (outcome) => {
    const old = deferred<Awaited<ReturnType<typeof apiService.getTaskJournalItems>>>();
    const next = deferred<Awaited<ReturnType<typeof apiService.getTaskJournalItems>>>();
    vi.mocked(apiService.getTaskJournalItems).mockImplementation((query) => query?.clientCaseId === 15 ? old.promise : next.promise);
    const view = renderHook((params) => useCaseTaskPage(params), { initialProps: initial });
    await waitFor(() => expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(apiService.getTaskJournalItems).mock.calls[0][1]!.signal!;
    view.rerender({ ...initial, caseId: 16 });
    await waitFor(() => expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
    await act(async () => { if (outcome === 'success') old.resolve(page([99])); else old.reject(new Error('Old request')); });
    expect(view.result.current.loading).toBe(true);
    expect(view.result.current.error).toBe(false);
    expect(view.result.current.items).toEqual([]);
    await act(async () => next.resolve(page([16])));
    expect(view.result.current.items[0].id).toBe(16);
  });

  it('hides data and aborts requests when viewing is disabled and makes no disabled reads', async () => {
    const view = renderHook((params) => useCaseTaskPage(params), { initialProps: { ...initial, enabled: false } });
    expect(apiService.getTaskJournalItems).not.toHaveBeenCalled();
    expect(apiService.getTaskJournalSummary).not.toHaveBeenCalled();
    view.rerender(initial);
    await waitFor(() => expect(view.result.current.items[0].id).toBe(1));
    const signal = vi.mocked(apiService.getTaskJournalItems).mock.calls[0][1]!.signal!;
    view.rerender({ ...initial, enabled: false });
    expect(signal.aborted).toBe(true);
    expect(view.result.current.items).toEqual([]);
    expect(view.result.current.counts).toBeNull();
    expect(view.result.current.loading).toBe(false);
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1);
  });

  it('corrects a vanished last page after refresh instead of displaying a false empty list', async () => {
    let total = 101;
    vi.mocked(apiService.getTaskJournalItems).mockImplementation(async (query) => page(query?.page === 3 && total < 101 ? [] : [query?.page ?? 1], total, query?.page));
    const { result } = renderHook(() => useCaseTaskPage(initial));
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.setPage(3));
    await waitFor(() => expect(result.current.items[0].id).toBe(3));
    total = 100;
    act(() => result.current.refresh());
    await waitFor(() => expect(result.current.page).toBe(2));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.items[0].id).toBe(2);
    expect(result.current.total).toBe(100);
  });

  it('reports a failed page separately from an empty result and retries page and summary', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockRejectedValueOnce(new Error('Unavailable'));
    const { result } = renderHook(() => useCaseTaskPage(initial));
    await waitFor(() => expect(result.current.error).toBe(true));
    expect(result.current.loading).toBe(false);
    act(() => result.current.refresh());
    await waitFor(() => expect(result.current.items[0].id).toBe(1));
    expect(result.current.error).toBe(false);
    expect(apiService.getTaskJournalSummary).toHaveBeenCalledTimes(2);
  });

  it('does not invent zero counters from an older or invalid summary contract', async () => {
    vi.mocked(apiService.getTaskJournalSummary).mockResolvedValue({ ...summary, total: undefined, completed: undefined });
    const { result } = renderHook(() => useCaseTaskPage(initial));
    await waitFor(() => expect(result.current.summaryError).toBe(true));
    expect(result.current.counts).toBeNull();
    expect(result.current.items[0].id).toBe(1);
  });

  it('resets page size without accepting an unbounded size', async () => {
    const { result } = renderHook(() => useCaseTaskPage(initial));
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => { result.current.setPage(2); result.current.setPageSize(25); });
    await waitFor(() => expect(apiService.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 25 }), expect.anything()));
    act(() => result.current.setPageSize(100000));
    expect(result.current.pageSize).toBe(25);
  });
});
