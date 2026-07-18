import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CashFlowForecastReport } from '../types';
import { useCashFlowForecastReport } from './useCashFlowForecastReport';

const { getCashFlowForecastReportMock } = vi.hoisted(() => ({
  getCashFlowForecastReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCashFlowForecastReport: getCashFlowForecastReportMock,
  },
}));

const emptyReport: CashFlowForecastReport = {
  from: '2026-07-01',
  to: '2026-07-31',
  accountName: null,
  openingBalance: 0,
  expectedIncome: 0,
  expectedExpense: 0,
  netProjectedFlow: 0,
  projectedClosingBalance: 0,
  incomeCount: 0,
  expenseCount: 0,
  days: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('useCashFlowForecastReport', () => {
  beforeEach(() => {
    getCashFlowForecastReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useCashFlowForecastReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getCashFlowForecastReportMock).not.toHaveBeenCalled();
  });

  it('passes an abort signal when loading the report', async () => {
    getCashFlowForecastReportMock.mockResolvedValue(emptyReport);
    const filters = { from: '2026-07-01', to: '2026-07-31' };
    const { result } = renderHook(() => useCashFlowForecastReport(filters));

    await waitFor(() => expect(result.current.report).toEqual(emptyReport));
    expect(getCashFlowForecastReportMock).toHaveBeenCalledWith(
      filters,
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<CashFlowForecastReport>();
    const newRequest = deferred<CashFlowForecastReport>();
    getCashFlowForecastReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(
      ({ from }) => useCashFlowForecastReport({ from }),
      { initialProps: { from: '2026-07-01' } },
    );

    await waitFor(() => expect(getCashFlowForecastReportMock).toHaveBeenCalledTimes(1));
    const obsoleteSignal = getCashFlowForecastReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-08-01' });
    await waitFor(() => expect(getCashFlowForecastReportMock).toHaveBeenCalledTimes(2));
    expect(obsoleteSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ ...emptyReport, from: '2026-08-01', expectedIncome: 9_000 });
    });
    await waitFor(() => expect(result.current.report?.expectedIncome).toBe(9_000));
    await act(async () => {
      oldRequest.resolve({ ...emptyReport, expectedIncome: 8_000 });
    });
    expect(result.current.report?.from).toBe('2026-08-01');
  });

  it('aborts the active request before reload', async () => {
    getCashFlowForecastReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCashFlowForecastReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCashFlowForecastReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowForecastReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCashFlowForecastReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getCashFlowForecastReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCashFlowForecastReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCashFlowForecastReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowForecastReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getCashFlowForecastReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCashFlowForecastReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCashFlowForecastReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowForecastReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
