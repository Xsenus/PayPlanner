import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useCurrencyReport } from './useCurrencyReport';
import type { CurrencyReport } from '../types';

const getCurrencyReportMock = vi.hoisted(() => vi.fn());
vi.mock('../services/api', () => ({ apiService: { getCurrencyReport: getCurrencyReportMock } }));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, incomeTotal: number): CurrencyReport {
  return {
    from,
    to: '2026-07-31',
    source: 'Bank',
    incomeTotal,
    expenseTotal: 0,
    netTotal: incomeTotal,
    currencies: [],
  };
}

describe('useCurrencyReport', () => {
  beforeEach(() => {
    getCurrencyReportMock.mockReset();
  });

  it('aborts an obsolete currency report and keeps the latest response', async () => {
    const oldRequest = deferred<CurrencyReport>();
    const newRequest = deferred<CurrencyReport>();
    getCurrencyReportMock
      .mockReturnValueOnce(oldRequest.promise)
      .mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useCurrencyReport({ from }), {
      initialProps: { from: '2026-05-01' },
    });

    await waitFor(() => expect(getCurrencyReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrencyReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getCurrencyReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);

    await act(async () => newRequest.resolve(report('2026-07-01', 9_000)));
    await waitFor(() => expect(result.current.report?.incomeTotal).toBe(9_000));
    await act(async () => oldRequest.resolve(report('2026-05-01', 8_000)));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active report before reload', async () => {
    getCurrencyReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCurrencyReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCurrencyReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrencyReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => {
      void result.current.reload();
    });
    await waitFor(() => expect(getCurrencyReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the report when disabled', async () => {
    getCurrencyReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useCurrencyReport({ from: '2026-07-01' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCurrencyReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrencyReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active report on unmount', async () => {
    getCurrencyReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCurrencyReport({ from: '2026-07-01' }));

    await waitFor(() => expect(getCurrencyReportMock).toHaveBeenCalledTimes(1));
    const signal = getCurrencyReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
