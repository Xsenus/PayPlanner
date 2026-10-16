import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { usePaymentsPaged } from './usePaymentsPaged';
import type { PaymentsListSummary } from '../types';

const getPaymentsV2Mock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getPaymentsV2: getPaymentsV2Mock,
  },
}));

const summary: PaymentsListSummary = {
  income: 0,
  expense: 0,
  balance: 0,
  incomeCount: 0,
  expenseCount: 0,
  total: 0,
};

type PaymentsResponse = {
  items: [];
  total: number;
  page: number;
  pageSize: number;
  summary: PaymentsListSummary;
};

describe('usePaymentsPaged', () => {
  beforeEach(() => {
    getPaymentsV2Mock.mockReset();
  });

  it('aborts the obsolete payments request and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: PaymentsResponse) => void> = [];
    getPaymentsV2Mock.mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });
    const { rerender, result } = renderHook(
      ({ search }) => usePaymentsPaged({ search, page: 1, pageSize: 25 }),
      { initialProps: { search: 'старый' } },
    );

    await waitFor(() => expect(getPaymentsV2Mock).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    rerender({ search: 'новый' });

    await waitFor(() => expect(getPaymentsV2Mock).toHaveBeenCalledTimes(2));
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);

    await act(async () => {
      resolvers[1]({ items: [], total: 2, page: 1, pageSize: 25, summary });
    });
    await waitFor(() => expect(result.current.total).toBe(2));
    expect(result.current.loading).toBe(false);

    await act(async () => {
      resolvers[0]({ items: [], total: 1, page: 1, pageSize: 25, summary });
    });
    expect(result.current.total).toBe(2);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active payments request when the hook unmounts', async () => {
    let signal: AbortSignal | undefined;
    getPaymentsV2Mock.mockImplementation((_params, options) => {
      signal = options?.signal;
      return new Promise(() => undefined);
    });
    const { unmount } = renderHook(() => usePaymentsPaged({ page: 1, pageSize: 25 }));

    await waitFor(() => expect(getPaymentsV2Mock).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });

  it('refreshes the current filter when the caller captured refresh before the filter changed', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: PaymentsResponse) => void> = [];
    getPaymentsV2Mock.mockImplementation((_params, options) => {
      signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });
    const { rerender, result } = renderHook(({ legalEntityId }) => usePaymentsPaged({ legalEntityId, page: 1, pageSize: 25 }),
      { initialProps: { legalEntityId: 12 } });
    await waitFor(() => expect(getPaymentsV2Mock).toHaveBeenCalledTimes(1));
    const oldRefresh = result.current.refresh;
    rerender({ legalEntityId: 13 });
    await waitFor(() => expect(getPaymentsV2Mock).toHaveBeenCalledTimes(2));
    await act(async () => { void oldRefresh(); });
    expect(getPaymentsV2Mock).toHaveBeenLastCalledWith(expect.objectContaining({ legalEntityId: 13 }), expect.anything());
    expect(signals[1].aborted).toBe(true);
    await act(async () => { resolvers[2]({ items: [], total: 13, page: 1, pageSize: 25, summary }); });
    await waitFor(() => expect(result.current.total).toBe(13));
    expect(result.current.loading).toBe(false);
    expect(result.current.refreshing).toBe(false);
    await act(async () => { resolvers[1]({ items: [], total: 2, page: 1, pageSize: 25, summary }); });
    expect(result.current.total).toBe(13);
  });

  it('does not fetch from a captured refresh after unmount', async () => {
    getPaymentsV2Mock.mockResolvedValue({ items: [], total: 1, page: 1, pageSize: 25, summary });
    const { result, unmount } = renderHook(() => usePaymentsPaged({ page: 1, pageSize: 25 }));
    await waitFor(() => expect(result.current.total).toBe(1));
    const capturedRefresh = result.current.refresh;
    unmount();
    await act(async () => { await capturedRefresh(); });
    expect(getPaymentsV2Mock).toHaveBeenCalledTimes(1);
  });

  it('does not restart a disabled scope from a captured refresh', async () => {
    getPaymentsV2Mock.mockResolvedValue({ items: [], total: 1, page: 1, pageSize: 25, summary });
    const { result, rerender } = renderHook(({ enabled }) => usePaymentsPaged({ page: 1, pageSize: 25, enabled }),
      { initialProps: { enabled: true } });
    await waitFor(() => expect(result.current.total).toBe(1));
    const capturedRefresh = result.current.refresh;
    rerender({ enabled: false });
    await waitFor(() => expect(result.current.total).toBe(0));
    await act(async () => { await capturedRefresh(); });
    expect(getPaymentsV2Mock).toHaveBeenCalledTimes(1);
    expect(result.current.total).toBe(0);
  });

  it('clears refreshing after a filter request replaces a pending refresh', async () => {
    const resolvers: Array<(value: PaymentsResponse) => void> = [];
    getPaymentsV2Mock.mockImplementation(() => new Promise((resolve) => resolvers.push(resolve)));
    const { result, rerender } = renderHook(({ legalEntityId }) => usePaymentsPaged({ legalEntityId, page: 1, pageSize: 25 }),
      { initialProps: { legalEntityId: 12 } });
    await waitFor(() => expect(resolvers).toHaveLength(1));
    await act(async () => { resolvers[0]({ items: [], total: 12, page: 1, pageSize: 25, summary }); });
    await act(async () => { void result.current.refresh(); });
    expect(result.current.refreshing).toBe(true);
    rerender({ legalEntityId: 13 });
    await waitFor(() => expect(resolvers).toHaveLength(3));
    await act(async () => { resolvers[2]({ items: [], total: 13, page: 1, pageSize: 25, summary }); });
    expect(result.current.loading).toBe(false);
    expect(result.current.refreshing).toBe(false);
    await act(async () => { resolvers[1]({ items: [], total: 12, page: 1, pageSize: 25, summary }); });
    expect(result.current.total).toBe(13);
  });
});
