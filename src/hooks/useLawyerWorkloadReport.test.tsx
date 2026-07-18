import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { LawyerWorkloadReport } from '../types';
import { useLawyerWorkloadReport } from './useLawyerWorkloadReport';

const { getLawyerWorkloadReportMock } = vi.hoisted(() => ({
  getLawyerWorkloadReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getLawyerWorkloadReport: getLawyerWorkloadReportMock,
  },
}));

const emptyReport: LawyerWorkloadReport = {
  generatedAt: '2026-07-18T10:00:00Z',
  asOf: '2026-07-18T00:00:00',
  horizonDays: 14,
  lawyerUserIdFilter: null,
  limit: 50,
  lawyerCount: 0,
  totalActiveCases: 0,
  totalOpenTasks: 0,
  totalOverdueTasks: 0,
  totalOverdueDeadlines: 0,
  overloadedLawyerCount: 0,
  totalPlannedHours: 0,
  items: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('useLawyerWorkloadReport', () => {
  beforeEach(() => {
    getLawyerWorkloadReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useLawyerWorkloadReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getLawyerWorkloadReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getLawyerWorkloadReportMock.mockResolvedValue(emptyReport);
    const filters = { asOf: '2026-07-18', horizonDays: 14, limit: 50 };
    const { result } = renderHook(() => useLawyerWorkloadReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getLawyerWorkloadReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<LawyerWorkloadReport>();
    const newRequest = deferred<LawyerWorkloadReport>();
    getLawyerWorkloadReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ horizonDays }) => useLawyerWorkloadReport({ horizonDays }),
      { initialProps: { horizonDays: 7 } },
    );

    await waitFor(() => expect(getLawyerWorkloadReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getLawyerWorkloadReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ horizonDays: 30 });
    await waitFor(() => expect(getLawyerWorkloadReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, horizonDays: 30, lawyerCount: 9 });
    });
    await waitFor(() => expect(result.current.report?.lawyerCount).toBe(9));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, horizonDays: 7, lawyerCount: 8 });
    });
    expect(result.current.report?.horizonDays).toBe(30);
  });

  it('aborts the active request before reload', async () => {
    getLawyerWorkloadReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useLawyerWorkloadReport({ asOf: '2026-07-18' }));

    await waitFor(() => expect(getLawyerWorkloadReportMock).toHaveBeenCalledTimes(1));
    const signal = getLawyerWorkloadReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getLawyerWorkloadReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getLawyerWorkloadReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useLawyerWorkloadReport({ asOf: '2026-07-18' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getLawyerWorkloadReportMock).toHaveBeenCalledTimes(1));
    const signal = getLawyerWorkloadReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getLawyerWorkloadReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useLawyerWorkloadReport({ asOf: '2026-07-18' }));

    await waitFor(() => expect(getLawyerWorkloadReportMock).toHaveBeenCalledTimes(1));
    const signal = getLawyerWorkloadReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
