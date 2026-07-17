import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { usePaymentPriorityReport } from './usePaymentPriorityReport';
import type { PaymentPriorityReport } from '../types';

const getPaymentPriorityReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getPaymentPriorityReport: getPaymentPriorityReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, incomeTotal: number): PaymentPriorityReport {
  return {
    from,
    to: '2026-07-31',
    source: 'Bank',
    incomeTotal,
    expenseTotal: 0,
    netTotal: incomeTotal,
    priorities: [],
  };
}

describe('usePaymentPriorityReport', () => {
  beforeEach(() => {
    getPaymentPriorityReportMock.mockReset();
  });

  it('aborts an obsolete priority report and keeps the latest response', async () => {
    const oldRequest = deferred<PaymentPriorityReport>();
    const newRequest = deferred<PaymentPriorityReport>();
    getPaymentPriorityReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => usePaymentPriorityReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getPaymentPriorityReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('2026-07-01', 9_000));
    });
    await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));

    await act(async () => {
      oldRequest.resolve(report('2026-05-01', 8_000));
    });
    expect(result.current.report?.from).toBe('2026-07-01');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active priority report before a manual reload', async () => {
    getPaymentPriorityReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => usePaymentPriorityReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getPaymentPriorityReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getPaymentPriorityReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active priority report when disabled', async () => {
    getPaymentPriorityReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => usePaymentPriorityReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(1));
    const signal = getPaymentPriorityReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active priority report on unmount', async () => {
    getPaymentPriorityReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => usePaymentPriorityReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getPaymentPriorityReportMock).toHaveBeenCalledTimes(1));
    const signal = getPaymentPriorityReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
