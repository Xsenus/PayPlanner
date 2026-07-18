import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CashFlowReport } from '../types';
import { apiService } from '../services/api';
import { useCashFlowReport } from './useCashFlowReport';

const { getCashFlowReportMock } = vi.hoisted(() => ({
  getCashFlowReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCashFlowReport: getCashFlowReportMock,
  },
}));

const emptyReport: CashFlowReport = {
  from: '2026-05-01',
  to: '2026-05-31',
  source: null,
  accountName: null,
  openingBalance: 0,
  income: 0,
  expense: 0,
  netCashFlow: 0,
  closingBalance: 0,
  incomeCount: 0,
  expenseCount: 0,
  bySource: [],
  byAccount: [],
  days: [],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

function CashFlowReportProbe({ enabled }: { enabled: boolean }) {
  const { report, loading, error } = useCashFlowReport(
    { from: '2026-05-01', to: '2026-05-31' },
    { enabled },
  );

  return (
    <div>
      <div data-testid="loading">{loading ? 'loading' : 'ready'}</div>
      <div data-testid="error">{error ?? ''}</div>
      <div data-testid="income">{report?.income ?? 'none'}</div>
    </div>
  );
}

describe('useCashFlowReport', () => {
  beforeEach(() => {
    getCashFlowReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    render(<CashFlowReportProbe enabled={false} />);

    await waitFor(() => {
      expect(screen.getByTestId('loading').textContent).toBe('ready');
    });
    expect(screen.getByTestId('income').textContent).toBe('none');
    expect(apiService.getCashFlowReport).not.toHaveBeenCalled();
  });

  it('loads the report when enabled', async () => {
    getCashFlowReportMock.mockResolvedValue(emptyReport);

    render(<CashFlowReportProbe enabled />);

    await waitFor(() => {
      expect(screen.getByTestId('income').textContent).toBe('0');
    });
    expect(apiService.getCashFlowReport).toHaveBeenCalledWith(
      { from: '2026-05-01', to: '2026-05-31' },
      { signal: expect.any(AbortSignal) },
    );
  });

  it('aborts an obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<CashFlowReport>();
    const newRequest = deferred<CashFlowReport>();
    getCashFlowReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);
    const { rerender, result } = renderHook(({ from }) => useCashFlowReport({ from }), { initialProps: { from: '2026-05-01' } });
    await waitFor(() => expect(getCashFlowReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ from: '2026-07-01' });
    await waitFor(() => expect(getCashFlowReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
    await act(async () => newRequest.resolve({ ...emptyReport, from: '2026-07-01', income: 9_000 }));
    await waitFor(() => expect(result.current.report?.income).toBe(9_000));
    await act(async () => oldRequest.resolve({ ...emptyReport, income: 8_000 }));
    expect(result.current.report?.from).toBe('2026-07-01');
  });

  it('aborts the active request before reload', async () => {
    getCashFlowReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useCashFlowReport({ from: '2026-07-01' }));
    await waitFor(() => expect(getCashFlowReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowReportMock.mock.calls[0][1]?.signal as AbortSignal;
    act(() => { void result.current.reload(); });
    await waitFor(() => expect(getCashFlowReportMock).toHaveBeenCalledTimes(2));
    expect(signal.aborted).toBe(true);
  });

  it('aborts and clears the active request when disabled', async () => {
    getCashFlowReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(({ enabled }) => useCashFlowReport({ from: '2026-07-01' }, { enabled }), { initialProps: { enabled: true } });
    await waitFor(() => expect(getCashFlowReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowReportMock.mock.calls[0][1]?.signal as AbortSignal;
    rerender({ enabled: false });
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
  });

  it('aborts the active request on unmount', async () => {
    getCashFlowReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useCashFlowReport({ from: '2026-07-01' }));
    await waitFor(() => expect(getCashFlowReportMock).toHaveBeenCalledTimes(1));
    const signal = getCashFlowReportMock.mock.calls[0][1]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
