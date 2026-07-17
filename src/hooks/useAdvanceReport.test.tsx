import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useAdvanceReport } from './useAdvanceReport';
import type { AdvanceReport } from '../types';

const getAdvanceReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getAdvanceReport: getAdvanceReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, incomeTotal: number): AdvanceReport {
  return {
    from,
    to: '2026-07-31',
    source: 'Bank',
    incomeTotal,
    expenseTotal: 0,
    recognizedTotal: incomeTotal,
    unrecognizedTotal: 0,
    unallocatedBalanceTotal: 0,
    recognizedCount: 1,
    unrecognizedCount: 0,
    unallocatedBalanceCount: 0,
    counterparties: [],
  };
}

describe('useAdvanceReport', () => {
  beforeEach(() => {
    getAdvanceReportMock.mockReset();
  });

  it('aborts an obsolete advance report and keeps the latest response', async () => {
    const oldRequest = deferred<AdvanceReport>();
    const newRequest = deferred<AdvanceReport>();
    getAdvanceReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => useAdvanceReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getAdvanceReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getAdvanceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getAdvanceReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('2026-07-01', 9_000));
    });
    await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));

    await act(async () => {
      oldRequest.resolve(report('2026-05-01', 8_000));
    });
    expect(result.current.report?.from).toBe('2026-07-01');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active advance report before a manual reload', async () => {
    getAdvanceReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useAdvanceReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getAdvanceReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getAdvanceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getAdvanceReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getAdvanceReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active advance report when disabled', async () => {
    getAdvanceReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useAdvanceReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getAdvanceReportMock).toHaveBeenCalledTimes(1));
    const signal = getAdvanceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getAdvanceReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active advance report on unmount', async () => {
    getAdvanceReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useAdvanceReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getAdvanceReportMock).toHaveBeenCalledTimes(1));
    const signal = getAdvanceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
