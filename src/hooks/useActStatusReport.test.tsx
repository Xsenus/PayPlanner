import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useActStatusReport } from './useActStatusReport';
import type { ActStatusReport } from '../types';

const getActStatusReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getActStatusReport: getActStatusReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, totalAmount: number): ActStatusReport {
  return {
    from,
    to: '2026-07-31',
    actCount: 1,
    createdCount: 1,
    transferredCount: 0,
    signedCount: 0,
    terminatedCount: 0,
    totalAmount,
    signedAmount: 0,
    pendingAmount: totalAmount,
    statuses: [],
  };
}

describe('useActStatusReport', () => {
  beforeEach(() => {
    getActStatusReportMock.mockReset();
  });

  it('aborts an obsolete act-status report and keeps the latest response', async () => {
    const oldRequest = deferred<ActStatusReport>();
    const newRequest = deferred<ActStatusReport>();
    getActStatusReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => useActStatusReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getActStatusReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getActStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getActStatusReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('2026-07-01', 9_000));
    });
    await waitFor(() => expect(result.current.report?.totalAmount).toBe(9_000));

    await act(async () => {
      oldRequest.resolve(report('2026-05-01', 8_000));
    });
    expect(result.current.report?.from).toBe('2026-07-01');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active act-status report before a manual reload', async () => {
    getActStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useActStatusReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getActStatusReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getActStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getActStatusReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getActStatusReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active act-status report when disabled', async () => {
    getActStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useActStatusReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getActStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getActStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getActStatusReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active act-status report on unmount', async () => {
    getActStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useActStatusReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getActStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getActStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
