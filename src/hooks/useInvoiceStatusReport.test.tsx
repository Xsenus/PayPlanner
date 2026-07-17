import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useInvoiceStatusReport } from './useInvoiceStatusReport';
import type { InvoiceStatusReport } from '../types';

const getInvoiceStatusReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getInvoiceStatusReport: getInvoiceStatusReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, totalAmount: number): InvoiceStatusReport {
  return {
    from,
    to: '2026-07-31',
    invoiceCount: 1,
    incomeCount: 1,
    expenseCount: 0,
    paidCount: 0,
    overdueCount: 0,
    totalAmount,
    paidAmount: 0,
    outstandingAmount: totalAmount,
    statuses: [],
  };
}

describe('useInvoiceStatusReport', () => {
  beforeEach(() => {
    getInvoiceStatusReportMock.mockReset();
  });

  it('aborts an obsolete invoice-status report and keeps the latest response', async () => {
    const oldRequest = deferred<InvoiceStatusReport>();
    const newRequest = deferred<InvoiceStatusReport>();
    getInvoiceStatusReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => useInvoiceStatusReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getInvoiceStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve(report('2026-07-01', 9_000));
    });
    await waitFor(() => expect(result.current.report?.totalAmount).toBe(9_000));

    await act(async () => {
      oldRequest.resolve(report('2026-05-01', 8_000));
    });
    expect(result.current.report?.from).toBe('2026-07-01');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active invoice-status report before a manual reload', async () => {
    getInvoiceStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useInvoiceStatusReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getInvoiceStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getInvoiceStatusReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active invoice-status report when disabled', async () => {
    getInvoiceStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useInvoiceStatusReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getInvoiceStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active invoice-status report on unmount', async () => {
    getInvoiceStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useInvoiceStatusReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getInvoiceStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getInvoiceStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
