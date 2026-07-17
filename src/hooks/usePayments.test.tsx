import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { usePayments } from './usePayments';
import type { Payment } from '../types';

const getPaymentsMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getPayments: getPaymentsMock,
  },
}));

const payment = (id: number): Payment => ({
  id,
  amount: id * 100,
  paidAmount: 0,
  outstandingAmount: id * 100,
  status: 'Pending',
  type: 'Income',
  date: `2026-07-${String(id).padStart(2, '0')}`,
  plannedDate: null,
} as Payment);

describe('usePayments', () => {
  beforeEach(() => {
    getPaymentsMock.mockReset();
  });

  it('aborts the obsolete calendar request and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: Payment[]) => void> = [];
    getPaymentsMock.mockImplementation((_params, options) => {
      signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });
    const { rerender, result } = renderHook(
      ({ from }) => usePayments(from, '2026-07-31', { enabled: true }),
      { initialProps: { from: '2026-07-01' } },
    );

    await waitFor(() => expect(getPaymentsMock).toHaveBeenCalledTimes(1));
    expect(signals[0].aborted).toBe(false);

    rerender({ from: '2026-07-02' });

    await waitFor(() => expect(getPaymentsMock).toHaveBeenCalledTimes(2));
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);

    await act(async () => {
      resolvers[1]([payment(2)]);
    });
    await waitFor(() => expect(result.current.payments.map((item) => item.id)).toEqual([2]));

    await act(async () => {
      resolvers[0]([payment(1)]);
    });
    expect(result.current.payments.map((item) => item.id)).toEqual([2]);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active calendar request when the hook unmounts', async () => {
    let signal: AbortSignal | undefined;
    getPaymentsMock.mockImplementation((_params, options) => {
      signal = options.signal;
      return new Promise(() => undefined);
    });
    const { unmount } = renderHook(() => usePayments('2026-07-01', '2026-07-31'));

    await waitFor(() => expect(getPaymentsMock).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });
});
