import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { usePaidInvoiceReport } from './usePaidInvoiceReport';
import type { PaidInvoiceReport } from '../types';

const getPaidInvoiceReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getPaidInvoiceReport: getPaidInvoiceReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, paidAmountTotal: number): PaidInvoiceReport {
  return {
    from,
    to: '2026-07-31',
    source: 'Bank',
    incomeTotal: paidAmountTotal,
    expenseTotal: 0,
    netTotal: paidAmountTotal,
    paidAmountTotal,
    invoices: [],
  };
}

describe('usePaidInvoiceReport', () => {
  beforeEach(() => {
    getPaidInvoiceReportMock.mockReset();
  });

  it('aborts an obsolete paid-invoice report and keeps the latest response', async () => {
    const oldRequest = deferred<PaidInvoiceReport>();
    const newRequest = deferred<PaidInvoiceReport>();
    getPaidInvoiceReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => usePaidInvoiceReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getPaidInvoiceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('2026-07-01', 9_000));
    });
    await waitFor(() => expect(result.current.report?.paidAmountTotal).toBe(9_000));

    await act(async () => {
      oldRequest.resolve(report('2026-05-01', 8_000));
    });
    expect(result.current.report?.from).toBe('2026-07-01');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active paid-invoice report before a manual reload', async () => {
    getPaidInvoiceReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => usePaidInvoiceReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getPaidInvoiceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getPaidInvoiceReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active paid-invoice report when disabled', async () => {
    getPaidInvoiceReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => usePaidInvoiceReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(1));
    const signal = getPaidInvoiceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active paid-invoice report on unmount', async () => {
    getPaidInvoiceReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => usePaidInvoiceReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getPaidInvoiceReportMock).toHaveBeenCalledTimes(1));
    const signal = getPaidInvoiceReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
