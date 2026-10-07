import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { LegalWorkReport } from '../types';
import { useLegalWorkReport } from './useLegalWorkReport';

const { getLegalWorkReportMock } = vi.hoisted(() => ({
  getLegalWorkReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getLegalWorkReport: getLegalWorkReportMock,
  },
}));

const emptyReport: LegalWorkReport = {
  hasCompletePaymentVisibility: true,
  from: '2026-07-01T00:00:00',
  to: '2026-07-31T00:00:00',
  statusFilter: '',
  responsibleUserIdFilter: null,
  caseTypeFilter: '',
  totalCases: 0,
  activeCases: 0,
  closedCases: 0,
  lostCases: 0,
  onHoldCases: 0,
  overdueDeadlineCount: 0,
  upcomingDeadlineCount: 0,
  openTaskCount: 0,
  overdueTaskCount: 0,
  onApprovalTaskCount: 0,
  feeTotal: 0,
  paidTotal: 0,
  outstandingTotal: 0,
  casesByStatus: [],
  casesByType: [],
  responsibles: [],
  financeByStatus: [],
  deadlinesByType: [],
  cases: [],
  overdueDeadlines: [],
  upcomingDeadlines: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('useLegalWorkReport', () => {
  beforeEach(() => {
    getLegalWorkReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useLegalWorkReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getLegalWorkReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getLegalWorkReportMock.mockResolvedValue(emptyReport);
    const filters = { from: '2026-07-01', to: '2026-07-31', status: 'Open' };
    const { result } = renderHook(() => useLegalWorkReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getLegalWorkReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<LegalWorkReport>();
    const newRequest = deferred<LegalWorkReport>();
    getLegalWorkReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ from }) => useLegalWorkReport({ from }),
      { initialProps: { from: '2026-06-01' } },
    );

    await waitFor(() => expect(getLegalWorkReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getLegalWorkReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getLegalWorkReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, from: '2026-07-01T00:00:00', totalCases: 9 });
    });
    await waitFor(() => expect(result.current.report?.totalCases).toBe(9));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, from: '2026-06-01T00:00:00', totalCases: 8 });
    });
    expect(result.current.report?.from).toBe('2026-07-01T00:00:00');
  });

  it('aborts the active request before reload', async () => {
    getLegalWorkReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useLegalWorkReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getLegalWorkReportMock).toHaveBeenCalledTimes(1));
    const signal = getLegalWorkReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getLegalWorkReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getLegalWorkReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useLegalWorkReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getLegalWorkReportMock).toHaveBeenCalledTimes(1));
    const signal = getLegalWorkReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getLegalWorkReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useLegalWorkReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getLegalWorkReportMock).toHaveBeenCalledTimes(1));
    const signal = getLegalWorkReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
