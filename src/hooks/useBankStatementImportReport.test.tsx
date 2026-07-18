import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useBankStatementImportReport } from './useBankStatementImportReport';
import type { BankStatementImportReport } from '../types';

const getBankStatementImportReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({
  apiService: { getBankStatementImportReport: getBankStatementImportReportMock },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, batchCount: number): BankStatementImportReport {
  return {
    from,
    to: '2026-07-31',
    batchCount,
    rowsReadTotal: batchCount,
    createdRowsTotal: batchCount,
    skippedRowsTotal: 0,
    errorRowsTotal: 0,
    rollbackRowsTotal: 0,
    appliedCount: batchCount,
    appliedWithErrorsCount: 0,
    rolledBackCount: 0,
    rollbackBlockedCount: 0,
    statuses: [],
  };
}

describe('useBankStatementImportReport', () => {
  beforeEach(() => {
    getBankStatementImportReportMock.mockReset();
  });

  it('aborts an obsolete import report and keeps the latest response', async () => {
    const oldRequest = deferred<BankStatementImportReport>();
    const newRequest = deferred<BankStatementImportReport>();
    getBankStatementImportReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useBankStatementImportReport({ from }), {
      initialProps: { from: '2026-05-01' },
    });

    await waitFor(() => expect(getBankStatementImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getBankStatementImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getBankStatementImportReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);

    await act(async () => newRequest.resolve(report('2026-07-01', 9)));
    await waitFor(() => expect(result.current.report?.batchCount).toBe(9));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before reload', async () => {
    getBankStatementImportReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useBankStatementImportReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getBankStatementImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getBankStatementImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getBankStatementImportReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getBankStatementImportReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useBankStatementImportReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getBankStatementImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getBankStatementImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getBankStatementImportReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useBankStatementImportReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getBankStatementImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getBankStatementImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
