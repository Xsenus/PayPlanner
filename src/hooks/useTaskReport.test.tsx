import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useTaskReport } from './useTaskReport';
import type { TaskReport } from '../types';

const getTaskReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getTaskReport: getTaskReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(scope: 'open' | 'closed' | 'all', totalTaskCount: number): TaskReport {
  return {
    generatedAt: '2026-07-17T00:00:00Z',
    asOf: '2026-07-17T00:00:00Z',
    scope,
    limit: 50,
    totalTaskCount,
    openTaskCount: scope === 'closed' ? 0 : totalTaskCount,
    closedTaskCount: scope === 'closed' ? totalTaskCount : 0,
    overdueTaskCount: 0,
    dueTodayTaskCount: 0,
    withoutAssigneeCount: 0,
    byStatus: [],
    byResponsible: [],
    items: [],
  };
}

describe('useTaskReport', () => {
  beforeEach(() => {
    getTaskReportMock.mockReset();
  });

  it('aborts an obsolete task report and keeps the latest response', async () => {
    const oldRequest = deferred<TaskReport>();
    const newRequest = deferred<TaskReport>();
    getTaskReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ scope }) => useTaskReport({ scope }),
      { initialProps: { scope: 'open' as 'open' | 'closed' } },
    );

    await waitFor(() => expect(getTaskReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getTaskReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ scope: 'closed' });

    await waitFor(() => expect(getTaskReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('closed', 2));
    });
    await waitFor(() => expect(result.current.report?.scope).toBe('closed'));

    await act(async () => {
      oldRequest.resolve(report('open', 1));
    });
    expect(result.current.report?.scope).toBe('closed');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active report before a manual reload', async () => {
    getTaskReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useTaskReport({ scope: 'all' }));

    await waitFor(() => expect(getTaskReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getTaskReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getTaskReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getTaskReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active report when disabled', async () => {
    getTaskReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useTaskReport({ scope: 'open' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getTaskReportMock).toHaveBeenCalledTimes(1));
    const signal = getTaskReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getTaskReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active report on unmount', async () => {
    getTaskReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useTaskReport({ scope: 'open' }));

    await waitFor(() => expect(getTaskReportMock).toHaveBeenCalledTimes(1));
    const signal = getTaskReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
