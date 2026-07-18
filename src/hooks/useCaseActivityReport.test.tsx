import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CaseActivityReport } from '../types';
import { useCaseActivityReport } from './useCaseActivityReport';

const { getCaseActivityReportMock } = vi.hoisted(() => ({
  getCaseActivityReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCaseActivityReport: getCaseActivityReportMock,
  },
}));

const emptyReport: CaseActivityReport = {
  generatedAt: '2026-07-18T10:00:00Z',
  inactivityDays: 14,
  escalationDays: 30,
  activity: 'all',
  totalVisibleCases: 0,
  activeCaseCount: 0,
  finishedCaseCount: 0,
  inactiveCaseCount: 0,
  escalatedCaseCount: 0,
  disabledCaseCount: 0,
  withActiveTasksCount: 0,
  withoutResponsibleCount: 0,
  items: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('useCaseActivityReport', () => {
  beforeEach(() => {
    getCaseActivityReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useCaseActivityReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getCaseActivityReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getCaseActivityReportMock.mockResolvedValue(emptyReport);
    const filters = { inactiveDays: 14, escalationDays: 30, activity: 'all' as const, limit: 50 };
    const { result } = renderHook(() => useCaseActivityReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getCaseActivityReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<CaseActivityReport>();
    const newRequest = deferred<CaseActivityReport>();
    getCaseActivityReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ activity }: { activity: 'inactive' | 'escalated' }) => useCaseActivityReport({ activity }),
      { initialProps: { activity: 'inactive' as 'inactive' | 'escalated' } },
    );

    await waitFor(() => expect(getCaseActivityReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getCaseActivityReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ activity: 'escalated' });
    await waitFor(() => expect(getCaseActivityReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, activity: 'escalated', escalatedCaseCount: 9 });
    });
    await waitFor(() => expect(result.current.report?.escalatedCaseCount).toBe(9));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, activity: 'inactive', inactiveCaseCount: 8 });
    });
    expect(result.current.report?.activity).toBe('escalated');
  });

  it('aborts the active request before reload', async () => {
    getCaseActivityReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCaseActivityReport({ inactiveDays: 14 }));

    await waitFor(() => expect(getCaseActivityReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseActivityReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCaseActivityReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getCaseActivityReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCaseActivityReport({ inactiveDays: 14 }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCaseActivityReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseActivityReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getCaseActivityReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCaseActivityReport({ inactiveDays: 14 }));

    await waitFor(() => expect(getCaseActivityReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseActivityReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
