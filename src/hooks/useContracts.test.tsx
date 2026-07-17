import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useContracts } from './useContracts';
import type { Contract } from '../types';

const getContractsMock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getContracts: getContractsMock,
  },
}));

type ContractsResponse = {
  items: Contract[];
  total: number;
  page: number;
  pageSize: number;
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function createContract(id: number, number: string): Contract {
  return {
    id,
    number,
    date: '2026-07-17',
    billingMode: 'Fixed',
    isOpenEnded: false,
    createdAt: '2026-07-17T00:00:00Z',
    invoiceAmountTotal: 0,
    actAmountTotal: 0,
    paymentAmountTotal: 0,
    clients: [],
  };
}

describe('useContracts', () => {
  beforeEach(() => {
    getContractsMock.mockReset();
  });

  it('aborts the obsolete request and keeps the latest response', async () => {
    const oldRequest = deferred<ContractsResponse>();
    const newRequest = deferred<ContractsResponse>();
    getContractsMock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ search }) => useContracts({ search, page: 1, pageSize: 20 }),
      { initialProps: { search: 'старый' } },
    );

    await waitFor(() => expect(getContractsMock).toHaveBeenCalledTimes(1));
    const oldSignal = getContractsMock.mock.calls[0][1]?.signal as AbortSignal;
    expect(oldSignal.aborted).toBe(false);

    rerender({ search: 'новый' });

    await waitFor(() => expect(getContractsMock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve({ items: [createContract(2, 'NEW')], total: 2, page: 1, pageSize: 20 });
    });
    await waitFor(() => expect(result.current.contracts[0]?.number).toBe('NEW'));
    expect(result.current.pagination.total).toBe(2);

    await act(async () => {
      oldRequest.resolve({ items: [createContract(1, 'OLD')], total: 1, page: 1, pageSize: 20 });
    });
    expect(result.current.contracts[0]?.number).toBe('NEW');
    expect(result.current.pagination.total).toBe(2);
  });

  it('aborts the active request when the hook unmounts', async () => {
    getContractsMock.mockReturnValue(new Promise(() => undefined));

    const { unmount } = renderHook(() => useContracts({ page: 1, pageSize: 20 }));
    await waitFor(() => expect(getContractsMock).toHaveBeenCalledTimes(1));
    const signal = getContractsMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });

  it('clears initial indicators when a manual refresh replaces the first request', async () => {
    const firstRequest = deferred<ContractsResponse>();
    const refreshedRequest = deferred<ContractsResponse>();
    getContractsMock.mockReturnValueOnce(firstRequest.promise).mockReturnValueOnce(refreshedRequest.promise);

    const { result } = renderHook(() => useContracts({ page: 1, pageSize: 20 }));
    await waitFor(() => expect(getContractsMock).toHaveBeenCalledTimes(1));

    let refreshPromise!: Promise<void>;
    act(() => {
      refreshPromise = result.current.refresh();
    });
    await waitFor(() => expect(getContractsMock).toHaveBeenCalledTimes(2));

    await act(async () => {
      refreshedRequest.resolve({ items: [createContract(3, 'REFRESH')], total: 1, page: 1, pageSize: 20 });
      await refreshPromise;
    });

    expect(result.current.loading).toBe(false);
    expect(result.current.refreshing).toBe(false);
    expect(result.current.contracts[0]?.number).toBe('REFRESH');
  });
});
