import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useProjectReport } from './useProjectReport';
import type { ProjectReport } from '../types';

const getProjectReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({
  apiService: { getProjectReport: getProjectReportMock },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, incomeTotal: number): ProjectReport {
  return {
    from,
    to: '2026-07-31',
    incomeTotal,
    expenseTotal: 0,
    netTotal: incomeTotal,
    projects: [],
  };
}

describe('useProjectReport', () => {
  beforeEach(() => {
    getProjectReportMock.mockReset();
  });

  it('aborts an obsolete project report and keeps the latest response', async () => {
    const oldRequest = deferred<ProjectReport>();
    const newRequest = deferred<ProjectReport>();
    getProjectReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useProjectReport({ from }), {
      initialProps: { from: '2026-05-01' },
    });

    await waitFor(() => expect(getProjectReportMock).toHaveBeenCalledTimes(1));
    const signal = getProjectReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getProjectReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);

    await act(async () => newRequest.resolve(report('2026-07-01', 9_000)));
    await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8_000)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before reload', async () => {
    getProjectReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useProjectReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getProjectReportMock).toHaveBeenCalledTimes(1));
    const signal = getProjectReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getProjectReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getProjectReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useProjectReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getProjectReportMock).toHaveBeenCalledTimes(1));
    const signal = getProjectReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getProjectReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useProjectReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getProjectReportMock).toHaveBeenCalledTimes(1));
    const signal = getProjectReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
