import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useMoneyCategoryReport } from './useMoneyCategoryReport';
import type { MoneyCategoryReport } from '../types';

const getMoneyCategoryReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({
  apiService: { getMoneyCategoryReport: getMoneyCategoryReportMock },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, incomeTotal: number): MoneyCategoryReport {
  return {
    from,
    to: '2026-07-31',
    incomeTotal,
    expenseTotal: 0,
    netTotal: incomeTotal,
    income: [],
    expense: [],
  };
}

describe('useMoneyCategoryReport', () => {
  beforeEach(() => {
    getMoneyCategoryReportMock.mockReset();
  });

  it('aborts an obsolete category report and keeps the latest response', async () => {
    const oldRequest = deferred<MoneyCategoryReport>();
    const newRequest = deferred<MoneyCategoryReport>();
    getMoneyCategoryReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useMoneyCategoryReport({ from }), {
      initialProps: { from: '2026-05-01' },
    });

    await waitFor(() => expect(getMoneyCategoryReportMock).toHaveBeenCalledTimes(1));
    const signal = getMoneyCategoryReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getMoneyCategoryReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);

    await act(async () => newRequest.resolve(report('2026-07-01', 9_000)));
    await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8_000)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before reload', async () => {
    getMoneyCategoryReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useMoneyCategoryReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getMoneyCategoryReportMock).toHaveBeenCalledTimes(1));
    const signal = getMoneyCategoryReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getMoneyCategoryReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getMoneyCategoryReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useMoneyCategoryReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getMoneyCategoryReportMock).toHaveBeenCalledTimes(1));
    const signal = getMoneyCategoryReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getMoneyCategoryReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useMoneyCategoryReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getMoneyCategoryReportMock).toHaveBeenCalledTimes(1));
    const signal = getMoneyCategoryReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
