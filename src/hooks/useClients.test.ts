import { renderHook, waitFor } from '@testing-library/react';
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
});
