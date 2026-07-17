import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useCashReport } from './useCashReport';
import type { CashBookSummary } from '../types';

const getCashBookMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getCashBook: getCashBookMock,
  },
}));

const report = (accountName: string, closingBalance: number): CashBookSummary => ({
  from: '2026-07-01',
  to: '2026-07-31',
  accountName,
  openingBalance: 0,
  income: closingBalance,
  expense: 0,
  closingBalance,
  incomingCount: 1,
  outgoingCount: 0,
  days: [],
});

describe('useCashReport', () => {
  beforeEach(() => {
    getCashBookMock.mockReset();
  });

  it('aborts an obsolete report and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: CashBookSummary) => void> = [];
    getCashBookMock.mockImplementation((_filters, options) => {
      signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });
    const { rerender, result } = renderHook(
      ({ accountName }) => useCashReport({ accountName }),
      { initialProps: { accountName: 'Старая касса' } },
    );

    await waitFor(() => expect(getCashBookMock).toHaveBeenCalledTimes(1));
    rerender({ accountName: 'Новая касса' });

    await waitFor(() => expect(getCashBookMock).toHaveBeenCalledTimes(2));
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);

    await act(async () => {
      resolvers[1](report('Новая касса', 2000));
    });
    await waitFor(() => expect(result.current.report?.closingBalance).toBe(2000));

    await act(async () => {
      resolvers[0](report('Старая касса', 1000));
    });
    expect(result.current.report?.accountName).toBe('Новая касса');
    expect(result.current.error).toBeNull();
  });

  it('aborts the active request before a manual reload', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: CashBookSummary) => void> = [];
    getCashBookMock.mockImplementation((_filters, options) => {
      signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });
    const { result } = renderHook(() => useCashReport({ accountName: 'Основная касса' }));

    await waitFor(() => expect(getCashBookMock).toHaveBeenCalledTimes(1));
    act(() => {
      void result.current.reload();
    });

    await waitFor(() => expect(getCashBookMock).toHaveBeenCalledTimes(2));
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);

    await act(async () => {
      resolvers[1](report('Основная касса', 3000));
    });
    await waitFor(() => expect(result.current.loading).toBe(false));
  });

  it('aborts the active request and clears state when the report is disabled', async () => {
    let signal: AbortSignal | undefined;
    getCashBookMock.mockImplementation((_filters, options) => {
      signal = options.signal;
      return new Promise(() => undefined);
    });
    const { rerender, result } = renderHook(
      ({ enabled }) => useCashReport({ accountName: 'Основная касса' }, { enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(getCashBookMock).toHaveBeenCalledTimes(1));
    rerender({ enabled: false });

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(signal?.aborted).toBe(true);
    expect(result.current.report).toBeNull();
    expect(result.current.error).toBeNull();
    expect(getCashBookMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the active request on unmount', async () => {
    let signal: AbortSignal | undefined;
    getCashBookMock.mockImplementation((_filters, options) => {
      signal = options.signal;
      return new Promise(() => undefined);
    });
    const { unmount } = renderHook(() => useCashReport({ accountName: 'Основная касса' }));

    await waitFor(() => expect(getCashBookMock).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });
});
