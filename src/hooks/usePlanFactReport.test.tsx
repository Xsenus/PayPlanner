import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { usePlanFactReport } from './usePlanFactReport';
import type { PlanFactReport } from '../types';

const getPlanFactReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getPlanFactReport: getPlanFactReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, factIncome: number): PlanFactReport {
  return {
    from,
    to: '2026-07-31',
    source: 'Bank',
    planIncome: 10_000,
    factIncome,
    incomeVariance: factIncome - 10_000,
    incomeCompletionPercent: factIncome / 100,
    planExpense: 0,
    factExpense: 0,
    expenseVariance: 0,
    expenseExecutionPercent: 0,
    planNetTotal: 10_000,
    factNetTotal: factIncome,
    netVariance: factIncome - 10_000,
    days: [],
  };
}

describe('usePlanFactReport', () => {
  beforeEach(() => {
    getPlanFactReportMock.mockReset();
  });

  it('aborts an obsolete plan-fact report and keeps the latest response', async () => {
    const oldRequest = deferred<PlanFactReport>();
    const newRequest = deferred<PlanFactReport>();
    getPlanFactReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => usePlanFactReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getPlanFactReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getPlanFactReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getPlanFactReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('2026-07-01', 9_000));
    });
    await waitFor(() => expect(result.current.report?.factIncome).toBe(9_000));

    await act(async () => {
      oldRequest.resolve(report('2026-05-01', 8_000));
    });
    expect(result.current.report?.from).toBe('2026-07-01');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active plan-fact report before a manual reload', async () => {
    getPlanFactReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => usePlanFactReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getPlanFactReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getPlanFactReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getPlanFactReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getPlanFactReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active plan-fact report when disabled', async () => {
    getPlanFactReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => usePlanFactReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getPlanFactReportMock).toHaveBeenCalledTimes(1));
    const signal = getPlanFactReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getPlanFactReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active plan-fact report on unmount', async () => {
    getPlanFactReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => usePlanFactReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getPlanFactReportMock).toHaveBeenCalledTimes(1));
    const signal = getPlanFactReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
