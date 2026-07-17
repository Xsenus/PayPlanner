import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';

import { useClients } from './useClients';
import { apiService } from '../services/api';
import type { Client } from '../types';

vi.mock('../services/api', () => ({
  apiService: {
    getClients: vi.fn(),
    getClientsV2: vi.fn(),
  },
}));

const client: Client = {
  id: 1,
  name: 'Test client',
  phone: '',
  email: '',
  company: '',
  address: '',
  notes: '',
  createdAt: '2026-01-01T00:00:00Z',
  isActive: true,
  cases: [],
  contracts: [],
};

const secondClient: Client = {
  ...client,
  id: 2,
  name: 'Current client',
};

describe('useClients', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getClients).mockResolvedValue([client]);
    vi.mocked(apiService.getClientsV2).mockResolvedValue({
      items: [client],
      total: 1,
      page: 1,
      pageSize: 25,
    });
  });

  it('does not refetch all clients when equivalent inline options are passed again', async () => {
    const { rerender, result } = renderHook(
      ({ enabled }) => useClients({ enabled }),
      { initialProps: { enabled: true } },
    );

    await waitFor(() => expect(apiService.getClients).toHaveBeenCalledTimes(1));
    expect(result.current.loading).toBe(false);

    rerender({ enabled: true });

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });

  it('does not refetch paged clients when filters object identity changes without value changes', async () => {
    const { rerender, result } = renderHook(
      ({ search }) => useClients({
        mode: 'paged',
        search,
        page: 1,
        pageSize: 25,
        filters: { clientStatusId: '3' },
      }),
      { initialProps: { search: 'test' } },
    );

    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(1));
    expect(result.current.loading).toBe(false);

    rerender({ search: 'test' });

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(apiService.getClientsV2).toHaveBeenCalledTimes(1);
  });

  it('aborts an obsolete paged request and keeps the current response', async () => {
    const signals: AbortSignal[] = [];
    const resolvePending: Array<(response: {
      items: Client[];
      total: number;
      page: number;
      pageSize: number;
    }) => void> = [];
    vi.mocked(apiService.getClientsV2).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvePending.push(resolve));
    });
    const { rerender, result } = renderHook(
      ({ search }) => useClients({ mode: 'paged', search, page: 1, pageSize: 25 }),
      { initialProps: { search: 'old' } },
    );
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    rerender({ search: 'current' });

    await waitFor(() => expect(signals[0]?.aborted).toBe(true));
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(2));
    await act(async () => {
      resolvePending[1]({ items: [secondClient], total: 1, page: 1, pageSize: 25 });
    });
    await waitFor(() => expect(result.current.clients.map((item) => item.id)).toEqual([2]));

    await act(async () => {
      resolvePending[0]({ items: [client], total: 1, page: 1, pageSize: 25 });
    });
    expect(result.current.clients.map((item) => item.id)).toEqual([2]);
  });

  it('aborts the unpaged request when the hook unmounts', async () => {
    let signal: AbortSignal | undefined;
    vi.mocked(apiService.getClients).mockImplementation((options) => {
      signal = options?.signal;
      return new Promise(() => undefined);
    });
    const { unmount } = renderHook(() => useClients());
    await waitFor(() => expect(apiService.getClients).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });
});
