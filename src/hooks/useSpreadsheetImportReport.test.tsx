import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useSpreadsheetImportReport } from './useSpreadsheetImportReport';
import type { SpreadsheetImportReport } from '../types';

const getSpreadsheetImportReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({
  apiService: { getSpreadsheetImportReport: getSpreadsheetImportReportMock },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, batchCount: number): SpreadsheetImportReport {
  return {
    from,
    to: '2026-07-31',
    batchCount,
    fileCount: batchCount,
    totalRows: batchCount,
    createdRows: batchCount,
    updatedRows: 0,
    skippedRows: 0,
    errorRows: 0,
    rollbackRows: 0,
    appliedCount: batchCount,
    appliedWithErrorsCount: 0,
    rolledBackCount: 0,
    rolledBackWithConflictsCount: 0,
    statuses: [],
  };
}

describe('useSpreadsheetImportReport', () => {
  beforeEach(() => {
    getSpreadsheetImportReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    const { result } = renderHook(() => useSpreadsheetImportReport({}, { enabled: false }));

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(getSpreadsheetImportReportMock).not.toHaveBeenCalled();
  });

  it('loads the report with a cancellation signal', async () => {
    getSpreadsheetImportReportMock.mockResolvedValue(report('2026-05-01', 2));
    const { result } = renderHook(() => useSpreadsheetImportReport({
      from: '2026-05-01',
      to: '2026-05-31',
    }));

    await waitFor(() => expect(result.current.report?.batchCount).toBe(2));
    expect(getSpreadsheetImportReportMock).toHaveBeenCalledWith(
      { from: '2026-05-01', to: '2026-05-31' },
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete report and keeps the latest response', async () => {
    const oldRequest = deferred<SpreadsheetImportReport>();
    const newRequest = deferred<SpreadsheetImportReport>();
    getSpreadsheetImportReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useSpreadsheetImportReport({ from }), {
      initialProps: { from: '2026-05-01' },
    });

    await waitFor(() => expect(getSpreadsheetImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getSpreadsheetImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getSpreadsheetImportReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);

    await act(async () => newRequest.resolve(report('2026-07-01', 9)));
    await waitFor(() => expect(result.current.report?.batchCount).toBe(9));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before reload', async () => {
    getSpreadsheetImportReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useSpreadsheetImportReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getSpreadsheetImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getSpreadsheetImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getSpreadsheetImportReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getSpreadsheetImportReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useSpreadsheetImportReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getSpreadsheetImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getSpreadsheetImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getSpreadsheetImportReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useSpreadsheetImportReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getSpreadsheetImportReportMock).toHaveBeenCalledTimes(1));
    const signal = getSpreadsheetImportReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
