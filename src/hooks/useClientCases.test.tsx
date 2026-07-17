import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useClientCases } from './useClientCases';
import type { ClientCase } from '../types';

const getCasesV1Mock = vi.hoisted(() => vi.fn());

vi.mock('../services/api', () => ({
  apiService: {
    getCasesV1: getCasesV1Mock,
  },
}));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function createCase(id: number, title: string): ClientCase {
  return {
    id,
    title,
    status: 'Active',
    createdAt: '2026-07-17T00:00:00Z',
  };
}

describe('useClientCases', () => {
  beforeEach(() => {
    getCasesV1Mock.mockReset();
  });

  it('aborts the previous client request and keeps the latest cases', async () => {
    const oldRequest = deferred<ClientCase[]>();
    const newRequest = deferred<ClientCase[]>();
    getCasesV1Mock.mockReturnValueOnce(oldRequest.promise).mockReturnValueOnce(newRequest.promise);

    const { rerender, result } = renderHook(
      ({ clientId }) => useClientCases(clientId),
      { initialProps: { clientId: 1 } },
    );

    await waitFor(() => expect(getCasesV1Mock).toHaveBeenCalledTimes(1));
    const oldSignal = getCasesV1Mock.mock.calls[0][1]?.signal as AbortSignal;
    expect(oldSignal.aborted).toBe(false);

    rerender({ clientId: 2 });

    await waitFor(() => expect(getCasesV1Mock).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);

    await act(async () => {
      newRequest.resolve([createCase(2, 'Новое дело')]);
    });
    await waitFor(() => expect(result.current.cases[0]?.title).toBe('Новое дело'));

    await act(async () => {
      oldRequest.resolve([createCase(1, 'Старое дело')]);
    });
    expect(result.current.cases[0]?.title).toBe('Новое дело');
    expect(result.current.error).toBeNull();
  });

  it('aborts the active request when the hook is disabled', async () => {
    getCasesV1Mock.mockReturnValue(new Promise(() => undefined));

    const { rerender, result } = renderHook(
      ({ enabled }) => useClientCases(1, { enabled }),
      { initialProps: { enabled: true } },
    );
    await waitFor(() => expect(getCasesV1Mock).toHaveBeenCalledTimes(1));
    const signal = getCasesV1Mock.mock.calls[0][1]?.signal as AbortSignal;

    rerender({ enabled: false });

    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.cases).toEqual([]);
    expect(result.current.error).toBeNull();
  });

  it('aborts the active request when the hook unmounts', async () => {
    getCasesV1Mock.mockReturnValue(new Promise(() => undefined));

    const { unmount } = renderHook(() => useClientCases(undefined, { legalEntityId: 7 }));
    await waitFor(() => expect(getCasesV1Mock).toHaveBeenCalledTimes(1));
    const signal = getCasesV1Mock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(signal.aborted).toBe(true);
  });
});
