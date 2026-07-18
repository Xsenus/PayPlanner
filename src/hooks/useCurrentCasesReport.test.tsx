import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CurrentCasesReport } from '../types';
import { useCurrentCasesReport } from './useCurrentCasesReport';

const { getCurrentCasesReportMock } = vi.hoisted(() => ({
  getCurrentCasesReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCurrentCasesReport: getCurrentCasesReportMock,
  },
}));

const emptyReport: CurrentCasesReport = {
  generatedAt: '2026-07-18T10:00:00Z',
  asOf: '2026-07-18T00:00:00',
  statusFilter: '',
  responsibleUserIdFilter: null,
  caseTypeFilter: '',
  limit: 50,
  totalCurrentCases: 0,
  openCases: 0,
  onHoldCases: 0,
  overdueCases: 0,
  withoutResponsibleCount: 0,
  withOpenTasksCount: 0,
  withOverdueTasksCount: 0,
  withOverdueDeadlinesCount: 0,
  byStatus: [],
  byResponsible: [],
  items: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('useCurrentCasesReport', () => {
  beforeEach(() => {
    getCurrentCasesReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useCurrentCasesReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getCurrentCasesReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getCurrentCasesReportMock.mockResolvedValue(emptyReport);
    const filters = { asOf: '2026-07-18', limit: 50 };
    const { result } = renderHook(() => useCurrentCasesReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getCurrentCasesReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<CurrentCasesReport>();
    const newRequest = deferred<CurrentCasesReport>();
    getCurrentCasesReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ asOf }) => useCurrentCasesReport({ asOf }),
      { initialProps: { asOf: '2026-07-18' } },
    );

    await waitFor(() => expect(getCurrentCasesReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getCurrentCasesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ asOf: '2026-07-19' });
    await waitFor(() => expect(getCurrentCasesReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, asOf: '2026-07-19T00:00:00', totalCurrentCases: 9 });
    });
    await waitFor(() => expect(result.current.report?.totalCurrentCases).toBe(9));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, totalCurrentCases: 8 });
    });
    expect(result.current.report?.asOf).toBe('2026-07-19T00:00:00');
  });

  it('aborts the active request before reload', async () => {
    getCurrentCasesReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCurrentCasesReport({ asOf: '2026-07-18' }));

    await waitFor(() => expect(getCurrentCasesReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrentCasesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCurrentCasesReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getCurrentCasesReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCurrentCasesReport({ asOf: '2026-07-18' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCurrentCasesReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrentCasesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getCurrentCasesReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCurrentCasesReport({ asOf: '2026-07-18' }));

    await waitFor(() => expect(getCurrentCasesReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrentCasesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
