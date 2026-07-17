import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useMatchStatusReport } from './useMatchStatusReport';
import type { MatchStatusReport } from '../types';

const getMatchStatusReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({ apiService: { getMatchStatusReport: getMatchStatusReportMock } }));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

function report(from: string, incomeTotal: number): MatchStatusReport {
  return { from, to: '2026-07-31', source: 'Bank', incomeTotal, expenseTotal: 0, netTotal: incomeTotal, operationCount: 1, matchedCount: 1, needsAttentionCount: 0, statuses: [] };
}

describe('useMatchStatusReport', () => {
  beforeEach(() => {
    getMatchStatusReportMock.mockReset();
  });

  it('aborts an obsolete match-status report and keeps the latest response', async () => {
    const oldRequest = deferred<MatchStatusReport>();
    const newRequest = deferred<MatchStatusReport>();
    getMatchStatusReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useMatchStatusReport({ from, to: '2026-07-31' }), { initialProps: { from: '2026-05-01' } });
    await waitFor(() => expect(getMatchStatusReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getMatchStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getMatchStatusReportMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);
    await act(async () => newRequest.resolve(report('2026-07-01', 9_000)));
    await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8_000)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before a manual reload', async () => {
    getMatchStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useMatchStatusReport({ from: '2026-07-01', to: '2026-07-31' }));
    await waitFor(() => expect(getMatchStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getMatchStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => { void result.current.reload(); });
    await waitFor(() => expect(getMatchStatusReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getMatchStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(({ enabled }) => useMatchStatusReport({ from: '2026-07-01' }, { enabled }), { initialProps: { enabled: true } });
    await waitFor(() => expect(getMatchStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getMatchStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getMatchStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useMatchStatusReport({ from: '2026-07-01' }));
    await waitFor(() => expect(getMatchStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getMatchStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
