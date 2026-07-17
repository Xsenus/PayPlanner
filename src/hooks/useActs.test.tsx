import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useActs } from './useActs';
import type { Act, ActsSummary } from '../types';

const { getActsMock, getActsSummaryMock } = vi.hoisted(() => ({
  getActsMock: vi.fn(),
  getActsSummaryMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getActs: getActsMock,
    getActsSummary: getActsSummaryMock,
  },
}));

type ActsResponse = {
  items: Act[];
  total: number;
  page: number;
  pageSize: number;
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function createAct(id: number, number: string): Act {
  return {
    id,
    number,
    date: '2026-07-17',
    amount: 100,
    status: 'Created',
    createdAt: '2026-07-17T00:00:00Z',
  };
}

function createSummary(totalAmount: number): ActsSummary {
  const emptyBucket = { amount: 0, count: 0 };
  return {
    created: emptyBucket,
    transferred: emptyBucket,
    signed: emptyBucket,
    terminated: emptyBucket,
    totalAmount,
    totalCount: 1,
  };
}

describe('useActs', () => {
  beforeEach(() => {
    getActsMock.mockReset();
    getActsSummaryMock.mockReset();
  });

  it('aborts obsolete list and summary requests and keeps the latest responses', async () => {
    const oldList = deferred<ActsResponse>();
    const newList = deferred<ActsResponse>();
    const oldSummary = deferred<ActsSummary>();
    const newSummary = deferred<ActsSummary>();
    getActsMock.mockReturnValueOnce(oldList.promise).mockReturnValueOnce(newList.promise);
    getActsSummaryMock.mockReturnValueOnce(oldSummary.promise).mockReturnValueOnce(newSummary.promise);

    const { rerender, result } = renderHook(
      ({ search }) => useActs({ search, page: 1, pageSize: 20 }),
      { initialProps: { search: 'старый' } },
    );

    await waitFor(() => expect(getActsMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getActsSummaryMock).toHaveBeenCalledTimes(1));
    const oldListSignal = getActsMock.mock.calls[0][1]?.signal as AbortSignal;
    const oldSummarySignal = getActsSummaryMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ search: 'новый' });

    await waitFor(() => expect(getActsMock).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(getActsSummaryMock).toHaveBeenCalledTimes(2));
    expect(oldListSignal.aborted).toBe(true);
    expect(oldSummarySignal.aborted).toBe(true);

    await act(async () => {
      newList.resolve({ items: [createAct(2, 'NEW')], total: 2, page: 1, pageSize: 20 });
      newSummary.resolve(createSummary(200));
    });
    await waitFor(() => expect(result.current.acts[0]?.number).toBe('NEW'));
    expect(result.current.pagination.total).toBe(2);
    expect(result.current.summary?.totalAmount).toBe(200);

    await act(async () => {
      oldList.resolve({ items: [createAct(1, 'OLD')], total: 1, page: 1, pageSize: 20 });
      oldSummary.resolve(createSummary(100));
    });
    expect(result.current.acts[0]?.number).toBe('NEW');
    expect(result.current.summary?.totalAmount).toBe(200);
  });

  it('aborts both active requests when the hook unmounts', async () => {
    getActsMock.mockReturnValue(new Promise(() => undefined));
    getActsSummaryMock.mockReturnValue(new Promise(() => undefined));

    const { unmount } = renderHook(() => useActs({ page: 1, pageSize: 20 }));
    await waitFor(() => expect(getActsMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getActsSummaryMock).toHaveBeenCalledTimes(1));
    const listSignal = getActsMock.mock.calls[0][1]?.signal as AbortSignal;
    const summarySignal = getActsSummaryMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(listSignal.aborted).toBe(true);
    expect(summarySignal.aborted).toBe(true);
  });

  it('clears initial indicators when a manual refresh replaces the first requests', async () => {
    const firstList = deferred<ActsResponse>();
    const refreshedList = deferred<ActsResponse>();
    const firstSummary = deferred<ActsSummary>();
    const refreshedSummary = deferred<ActsSummary>();
    getActsMock.mockReturnValueOnce(firstList.promise).mockReturnValueOnce(refreshedList.promise);
    getActsSummaryMock.mockReturnValueOnce(firstSummary.promise).mockReturnValueOnce(refreshedSummary.promise);

    const { result } = renderHook(() => useActs({ page: 1, pageSize: 20 }));
    await waitFor(() => expect(getActsMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getActsSummaryMock).toHaveBeenCalledTimes(1));

    let refreshPromise!: Promise<void>;
    act(() => {
      refreshPromise = result.current.refresh();
    });
    await waitFor(() => expect(getActsMock).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(getActsSummaryMock).toHaveBeenCalledTimes(2));

    await act(async () => {
      refreshedList.resolve({ items: [createAct(3, 'REFRESH')], total: 1, page: 1, pageSize: 20 });
      refreshedSummary.resolve(createSummary(300));
      await refreshPromise;
    });

    expect(result.current.loading).toBe(false);
    expect(result.current.refreshing).toBe(false);
    expect(result.current.summaryLoading).toBe(false);
    expect(result.current.acts[0]?.number).toBe('REFRESH');
  });
});
