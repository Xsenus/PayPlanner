import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { OverdueProceduralDeadlinesReport } from '../types';
import { useOverdueProceduralDeadlinesReport } from './useOverdueProceduralDeadlinesReport';

const { getOverdueProceduralDeadlinesReportMock } = vi.hoisted(() => ({
  getOverdueProceduralDeadlinesReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getOverdueProceduralDeadlinesReport: getOverdueProceduralDeadlinesReportMock,
  },
}));

const emptyReport: OverdueProceduralDeadlinesReport = {
  generatedAt: '2026-07-18T10:00:00Z',
  asOf: '2026-07-18T00:00:00',
  deadlineTypeFilter: '',
  responsibleUserIdFilter: null,
  limit: 50,
  totalOverdueCount: 0,
  criticalOverdueCount: 0,
  withoutResponsibleCount: 0,
  byType: [],
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

describe('useOverdueProceduralDeadlinesReport', () => {
  beforeEach(() => {
    getOverdueProceduralDeadlinesReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useOverdueProceduralDeadlinesReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getOverdueProceduralDeadlinesReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getOverdueProceduralDeadlinesReportMock.mockResolvedValue(emptyReport);
    const filters = { asOf: '2026-07-18', limit: 50 };
    const { result } = renderHook(() => useOverdueProceduralDeadlinesReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<OverdueProceduralDeadlinesReport>();
    const newRequest = deferred<OverdueProceduralDeadlinesReport>();
    getOverdueProceduralDeadlinesReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ deadlineType }) => useOverdueProceduralDeadlinesReport({ deadlineType }),
      { initialProps: { deadlineType: 'appeal' } },
    );

    await waitFor(() => expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getOverdueProceduralDeadlinesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ deadlineType: 'motion' });
    await waitFor(() => expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, deadlineTypeFilter: 'motion', totalOverdueCount: 9 });
    });
    await waitFor(() => expect(result.current.report?.totalOverdueCount).toBe(9));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, deadlineTypeFilter: 'appeal', totalOverdueCount: 8 });
    });
    expect(result.current.report?.deadlineTypeFilter).toBe('motion');
  });

  it('aborts the active request before reload', async () => {
    getOverdueProceduralDeadlinesReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useOverdueProceduralDeadlinesReport({ asOf: '2026-07-18' }));

    await waitFor(() => expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledTimes(1));
    const signal = getOverdueProceduralDeadlinesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getOverdueProceduralDeadlinesReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useOverdueProceduralDeadlinesReport({ asOf: '2026-07-18' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledTimes(1));
    const signal = getOverdueProceduralDeadlinesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getOverdueProceduralDeadlinesReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useOverdueProceduralDeadlinesReport({ asOf: '2026-07-18' }));

    await waitFor(() => expect(getOverdueProceduralDeadlinesReportMock).toHaveBeenCalledTimes(1));
    const signal = getOverdueProceduralDeadlinesReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
