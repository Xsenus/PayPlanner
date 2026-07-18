import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useJournalStatusReport } from './useJournalStatusReport';
import type { JournalStatusReport } from '../types';

const getJournalStatusReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({ apiService: { getJournalStatusReport: getJournalStatusReportMock } }));

function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((res) => { resolve = res; }); return { promise, resolve }; }
function report(from: string, incomeTotal: number): JournalStatusReport { return { from, to: '2026-07-31', source: 'Bank', incomeTotal, expenseTotal: 0, netTotal: incomeTotal, operationCount: 1, postedCount: 1, processingCount: 0, errorCount: 0, statuses: [] }; }

describe('useJournalStatusReport', () => {
  beforeEach(() => { getJournalStatusReportMock.mockReset(); });

  it('aborts an obsolete journal-status report and keeps the latest response', async () => {
    const oldRequest = deferred<JournalStatusReport>(); const newRequest = deferred<JournalStatusReport>();
    getJournalStatusReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useJournalStatusReport({ from, to: '2026-07-31' }), { initialProps: { from: '2026-05-01' } });
    await waitFor(() => expect(getJournalStatusReportMock).toHaveBeenCalledTimes(1)); const signal = getJournalStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' }); await waitFor(() => expect(getJournalStatusReportMock).toHaveBeenCalledTimes(2)); expect(signal.aborted).toBe(true);
    await act(async () => newRequest.resolve(report('2026-07-01', 9_000))); await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8_000))); expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before a manual reload', async () => {
    getJournalStatusReportMock.mockReturnValue(new Promise(() => undefined)); const { result } = renderHook(() => useJournalStatusReport({ from: '2026-07-01' }));
    await waitFor(() => expect(getJournalStatusReportMock).toHaveBeenCalledTimes(1)); const signal = getJournalStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => { void result.current.reload(); }); await waitFor(() => expect(getJournalStatusReportMock).toHaveBeenCalledTimes(2)); expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getJournalStatusReportMock.mockReturnValue(new Promise(() => undefined)); const { rerender, result } = renderHook(({ enabled }) => useJournalStatusReport({ from: '2026-07-01' }, { enabled }), { initialProps: { enabled: true } });
    await waitFor(() => expect(getJournalStatusReportMock).toHaveBeenCalledTimes(1)); const signal = getJournalStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false }); expect(signal.aborted).toBe(true); await waitFor(() => expect(result.current.loading).toBe(false)); expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getJournalStatusReportMock.mockReturnValue(new Promise(() => undefined)); const { unmount } = renderHook(() => useJournalStatusReport({ from: '2026-07-01' }));
    await waitFor(() => expect(getJournalStatusReportMock).toHaveBeenCalledTimes(1)); const signal = getJournalStatusReportMock.mock.calls[0][1]?.signal as AbortSignal; unmount(); expect(signal.aborted).toBe(true);
  });
});
