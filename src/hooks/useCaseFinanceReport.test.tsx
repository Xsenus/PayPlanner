import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CaseFinanceReport } from '../types';
import { useCaseFinanceReport } from './useCaseFinanceReport';

const { getCaseFinanceReportMock } = vi.hoisted(() => ({
  getCaseFinanceReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCaseFinanceReport: getCaseFinanceReportMock,
  },
}));

const emptyReport: CaseFinanceReport = {
  generatedAt: '2026-07-18T10:00:00Z',
  from: '2026-07-01T00:00:00',
  to: '2026-07-31T00:00:00',
  statusFilter: '',
  responsibleUserIdFilter: null,
  caseTypeFilter: '',
  limit: 100,
  totalCases: 0,
  activeCases: 0,
  closedCases: 0,
  feeTotal: 0,
  paidTotal: 0,
  expenseTotal: 0,
  outstandingTotal: 0,
  marginTotal: 0,
  collectionRate: 0,
  marginRate: 0,
  fullyPaidCaseCount: 0,
  partiallyPaidCaseCount: 0,
  unpaidCaseCount: 0,
  overpaidCaseCount: 0,
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

describe('useCaseFinanceReport', () => {
  beforeEach(() => {
    getCaseFinanceReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useCaseFinanceReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getCaseFinanceReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getCaseFinanceReportMock.mockResolvedValue(emptyReport);
    const filters = { from: '2026-07-01', to: '2026-07-31', status: 'Open', limit: 50 };
    const { result } = renderHook(() => useCaseFinanceReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getCaseFinanceReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<CaseFinanceReport>();
    const newRequest = deferred<CaseFinanceReport>();
    getCaseFinanceReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ from }) => useCaseFinanceReport({ from }),
      { initialProps: { from: '2026-06-01' } },
    );

    await waitFor(() => expect(getCaseFinanceReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getCaseFinanceReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getCaseFinanceReportMock).toHaveBeenCalledTimes(2));
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
    getCaseFinanceReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCaseFinanceReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCaseFinanceReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseFinanceReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCaseFinanceReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getCaseFinanceReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCaseFinanceReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCaseFinanceReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseFinanceReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getCaseFinanceReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCaseFinanceReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCaseFinanceReportMock).toHaveBeenCalledTimes(1));
    const signal = getCaseFinanceReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
