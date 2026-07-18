import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CaseSuccessReport } from '../types';
import { useCaseSuccessReport } from './useCaseSuccessReport';

const { getCaseSuccessReportMock } = vi.hoisted(() => ({
  getCaseSuccessReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCaseSuccessReport: getCaseSuccessReportMock,
  },
}));

const emptyReport: CaseSuccessReport = {
  generatedAt: '2026-07-18T10:00:00Z',
  from: '2026-07-01T00:00:00',
  to: '2026-07-31T00:00:00',
  statusFilter: '',
  responsibleUserIdFilter: null,
  caseTypeFilter: '',
  limit: 100,
  totalCases: 0,
  completedCases: 0,
  successfulCases: 0,
  lostCases: 0,
  inProgressCases: 0,
  onHoldCases: 0,
  successRate: 0,
  lossRate: 0,
  completionRate: 0,
  formulaNote: 'Closed = успешное завершение, Lost = отрицательный исход',
  byStatus: [],
  byResponsible: [],
  byType: [],
  items: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('useCaseSuccessReport', () => {
  beforeEach(() => {
    getCaseSuccessReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useCaseSuccessReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getCaseSuccessReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getCaseSuccessReportMock.mockResolvedValue(emptyReport);
    const filters = { from: '2026-07-01', to: '2026-07-31', status: 'Closed', limit: 50 };
    const { result } = renderHook(() => useCaseSuccessReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getCaseSuccessReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<CaseSuccessReport>();
    const newRequest = deferred<CaseSuccessReport>();
    getCaseSuccessReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ from }) => useCaseSuccessReport({ from }),
      { initialProps: { from: '2026-06-01' } },
    );

    await waitFor(() => expect(getCaseSuccessReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getCaseSuccessReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getCaseSuccessReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, from: '2026-07-01T00:00:00', successfulCases: 9 });
    });
    await waitFor(() => expect(result.current.report?.successfulCases).toBe(9));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, from: '2026-06-01T00:00:00', successfulCases: 8 });
    });
    expect(result.current.report?.from).toBe('2026-07-01T00:00:00');
  });

  it('aborts the active request before reload', async () => {
    getCaseSuccessReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCaseSuccessReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCaseSuccessReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseSuccessReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCaseSuccessReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getCaseSuccessReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCaseSuccessReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCaseSuccessReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseSuccessReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getCaseSuccessReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCaseSuccessReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCaseSuccessReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseSuccessReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
