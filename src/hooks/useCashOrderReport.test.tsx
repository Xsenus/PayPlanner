import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useCashOrderReport } from './useCashOrderReport';
import type { CashOrderReport } from '../types';

const getCashOrderReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({ apiService: { getCashOrderReport: getCashOrderReportMock } }));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, postedIncomingTotal: number): CashOrderReport {
  return {
    from,
    to: '2026-07-31',
    postedIncomingTotal,
    postedOutgoingTotal: 0,
    postedNetTotal: postedIncomingTotal,
    orderCount: 1,
    draftCount: 0,
    postedCount: 1,
    cancelledCount: 0,
    statuses: [],
  };
}

describe('useCashOrderReport', () => {
  beforeEach(() => {
    getCashOrderReportMock.mockReset();
  });

  it('aborts an obsolete cash order report and keeps the latest response', async () => {
    const oldRequest = deferred<CashOrderReport>();
    const newRequest = deferred<CashOrderReport>();
    getCashOrderReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useCashOrderReport({ from }), {
      initialProps: { from: '2026-05-01' },
    });

    await waitFor(() => expect(getCashOrderReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashOrderReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getCashOrderReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);

    await act(async () => newRequest.resolve(report('2026-07-01', 9_000)));
    await waitFor(() => expect(result.current.report?.postedIncomingTotal).toBe(9_000));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8_000)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before reload', async () => {
    getCashOrderReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCashOrderReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCashOrderReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashOrderReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCashOrderReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getCashOrderReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCashOrderReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCashOrderReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashOrderReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getCashOrderReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCashOrderReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCashOrderReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashOrderReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
