import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useContractStatusReport } from './useContractStatusReport';
import type { ContractStatusReport } from '../types';

const getContractStatusReportMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getContractStatusReport: getContractStatusReportMock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function report(from: string, totalAmount: number): ContractStatusReport {
  return {
    from,
    to: '2026-07-31',
    contractCount: 1,
    draftCount: 1,
    notSignedCount: 0,
    onSigningCount: 0,
    concludedCount: 0,
    terminatedCount: 0,
    cancelledCount: 0,
    openEndedCount: 0,
    totalAmount,
    activeAmount: totalAmount,
    closedAmount: 0,
    statuses: [],
  };
}

describe('useContractStatusReport', () => {
  beforeEach(() => {
    getContractStatusReportMock.mockReset();
  });

  it('aborts an obsolete contract-status report and keeps the latest response', async () => {
    const oldRequest = deferred<ContractStatusReport>();
    const newRequest = deferred<ContractStatusReport>();
    getContractStatusReportMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ from }) => useContractStatusReport({ from, to: '2026-07-31', source: 'Bank' }),
      { initialProps: { from: '2026-05-01' } },
    );

    await waitFor(() => expect(getContractStatusReportMock).toHaveBeenCalledTimes(1));
    const oldSignal = getContractStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ from: '2026-07-01' });

    await waitFor(() => expect(getContractStatusReportMock).toHaveBeenCalledTimes(2));
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

  it('aborts the active contract-status report before a manual reload', async () => {
    getContractStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useContractStatusReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getContractStatusReportMock).toHaveBeenCalledTimes(1));
    const firstSignal = getContractStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getContractStatusReportMock).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    expect((getContractStatusReportMock.mock.calls[1][1]?.signal as AbortSignal).aborted).toBe(false);
  });

  it('aborts and clears the active contract-status report when disabled', async () => {
    getContractStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { rerender, result } = renderHook(
      ({ enabled }) => useContractStatusReport({ from: '2026-07-01', to: '2026-07-31' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getContractStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getContractStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getContractStatusReportMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active contract-status report on unmount', async () => {
    getContractStatusReportMock.mockReturnValue(new Promise(() => undefined));
    const { unmount } = renderHook(() => useContractStatusReport({ from: '2026-07-01', to: '2026-07-31' }));

    await waitFor(() => expect(getContractStatusReportMock).toHaveBeenCalledTimes(1));
    const signal = getContractStatusReportMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
