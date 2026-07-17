import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useSummaryStats } from './useSummaryStats';
import type { SummaryBucket, SummaryStats } from '../types';

const getSummaryStatsMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getSummaryStats: getSummaryStatsMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function bucket(amount: number): SummaryBucket {
  return {
    totalAmount: amount,
    totalCount: 1,
    collectedAmount: amount,
    completedAmount: amount,
    completedCount: 1,
    pendingAmount: 0,
    pendingCount: 0,
    overdueAmount: 0,
    overdueCount: 0,
    remainingAmount: 0,
  };
}

function summary(clientId: number, amount: number): SummaryStats {
  return {
    from: '2026-07-01',
    to: '2026-07-31',
    clientId,
    income: bucket(amount),
    expense: bucket(0),
    netCompleted: amount,
    netCollected: amount,
    netTotal: amount,
    netRemaining: 0,
  };
}

describe('useSummaryStats', () => {
  beforeEach(() => {
    getSummaryStatsMock.mockReset();
  });

  it('aborts an obsolete aggregation and keeps the latest result', async () => {
    const oldRequest = deferred<SummaryStats>();
    const newRequest = deferred<SummaryStats>();
    getSummaryStatsMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ clientId }) => useSummaryStats({ clientId }),
      { initialProps: { clientId: 1 } },
    );

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(1));
    const oldSignal = getSummaryStatsMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ clientId: 2 });

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(summary(2, 200));
    });
    await waitFor(() => expect(result.current.data?.clientId).toBe(2));

    await act(async () => {
      oldRequest.resolve(summary(1, 100));
    });
    expect(result.current.data?.clientId).toBe(2);
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active aggregation before a manual refresh and on unmount', async () => {
    getSummaryStatsMock.mockReturnValue(new Promise(() => undefined));
    const { result, unmount } = renderHook(() => useSummaryStats({ period: 'this-month' }));

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(1));
    const firstSignal = getSummaryStatsMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.refresh();
    });

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(2));
    const secondSignal = getSummaryStatsMock.mock.calls[1][1]?.signal as AbortSignal;
    expect(firstSignal.aborted).toBe(true);
    expect(secondSignal.aborted).toBe(false);

    unmount();

    expect(secondSignal.aborted).toBe(true);
  });
});
