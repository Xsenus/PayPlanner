import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { usePeriodStats } from './usePeriodStats';
import type { SummaryBucket, SummaryStats } from '../types';

const getSummaryStatsMock = vi.hoisted(() => vi.fn());
type YearMonth = `${number}-${string}`;

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

function summary(from: string, to: string, amount: number): SummaryStats {
  return {
    from,
    to,
    income: bucket(amount),
    expense: bucket(0),
    netCompleted: amount,
    netCollected: amount,
    netTotal: amount,
    netRemaining: 0,
  };
}

describe('usePeriodStats', () => {
  beforeEach(() => {
    getSummaryStatsMock.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('aborts the previous period and keeps the latest aggregation', async () => {
    const oldRequest = deferred<SummaryStats>();
    const newRequest = deferred<SummaryStats>();
    getSummaryStatsMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from, to }) => usePeriodStats(from, to),
      { initialProps: { from: '2026-05', to: '2026-05' } as { from: YearMonth; to: YearMonth } },
    );

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(1));
    const oldSignal = getSummaryStatsMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-06', to: '2026-06' });

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);
    expect(getSummaryStatsMock.mock.calls[1][0]).toEqual({ from: '2026-06-01', to: '2026-06-30' });

    await act(async () => {
      newRequest.resolve(summary('2026-06-01', '2026-06-30', 200));
    });
    await waitFor(() => expect(result.current.stats?.profit).toBe(200));

    await act(async () => {
      oldRequest.resolve(summary('2026-05-01', '2026-05-31', 100));
    });
    expect(result.current.stats?.profit).toBe(200);
    expect(result.current.loading).toBe(false);
  });

  it('does not overlap polling requests and aborts the active one on unmount', () => {
    vi.useFakeTimers();
    getSummaryStatsMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => usePeriodStats(
      '2026-07',
      '2026-07',
      { pollInterval: 1_000 },
    ));

    expect(getSummaryStatsMock).toHaveBeenCalledTimes(1);
    const signal = getSummaryStatsMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      vi.advanceTimersByTime(3_000);
    });

    expect(getSummaryStatsMock).toHaveBeenCalledTimes(1);
    expect(signal.aborted).toBe(false);

    unmount();

    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active aggregation when disabled', async () => {
    getSummaryStatsMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => usePeriodStats('2026-07', '2026-07', { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getSummaryStatsMock).toHaveBeenCalledTimes(1));
    const signal = getSummaryStatsMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.stats).toBeNull();
    expect(result.current.types).toBeNull();
    expect(result.current.error).toBeNull();
  });
});
