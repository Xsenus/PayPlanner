import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useAccountsRegistry } from './useAccountsRegistry';
import type { AccountsSummary } from '../types';

const getAccountsRegistryMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getAccountsRegistry: getAccountsRegistryMock,
  },
}));

const summary: AccountsSummary = {
  total: { amount: 0, count: 0 },
  pending: { amount: 0, count: 0 },
  paid: { amount: 0, count: 0 },
  overdue: { amount: 0, count: 0 },
  cancelled: { amount: 0, count: 0 },
};

type RegistryResponse = {
  items: [];
  total: number;
  page: number;
  pageSize: number;
  summary: AccountsSummary;
};

describe('useAccountsRegistry', () => {
  beforeEach(() => {
    getAccountsRegistryMock.mockReset();
  });

  it('aborts the obsolete registry request and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: RegistryResponse) => void> = [];
    getAccountsRegistryMock.mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });
    const { rerender, result } = renderHook(
      ({ search }) => useAccountsRegistry({ search, page: 1, pageSize: 20 }),
      { initialProps: { search: 'старый' } },
    );

    await waitFor(() => expect(getAccountsRegistryMock).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    rerender({ search: 'новый' });

    await waitFor(() => expect(getAccountsRegistryMock).toHaveBeenCalledTimes(2));
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);

    await act(async () => {
      resolvers[1]({ items: [], total: 2, page: 1, pageSize: 20, summary });
    });
    await waitFor(() => expect(result.current.total).toBe(2));
    expect(result.current.loading).toBe(false);

    await act(async () => {
      resolvers[0]({ items: [], total: 1, page: 1, pageSize: 20, summary });
    });
    expect(result.current.total).toBe(2);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active registry request when the hook unmounts', async () => {
    let signal: AbortSignal | undefined;
    getAccountsRegistryMock.mockImplementation((_params, options) => {
      signal = options?.signal;
      return new Promise(() => undefined);
    });
    const { unmount } = renderHook(() => useAccountsRegistry({ page: 1, pageSize: 20 }));

    await waitFor(() => expect(getAccountsRegistryMock).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });
});
