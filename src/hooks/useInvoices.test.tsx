import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useInvoices } from './useInvoices';
import type { Invoice, InvoiceSummary } from '../types';

const { getInvoicesMock, getInvoicesSummaryMock } = vi.hoisted(() => ({
  getInvoicesMock: vi.fn(),
  getInvoicesSummaryMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getInvoices: getInvoicesMock,
    getInvoicesSummary: getInvoicesSummaryMock,
  },
}));

type InvoiceResponse = {
  items: Invoice[];
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

function invoice(id: number, number: string): Invoice {
  return { id, number } as Invoice;
}

function summary(amount: number): InvoiceSummary {
  const emptyBucket = { amount: 0, count: 0 };
  return {
    total: { amount, count: 1 },
    pending: emptyBucket,
    paid: emptyBucket,
    overdue: emptyBucket,
  };
}

describe('useInvoices', () => {
  beforeEach(() => {
    getInvoicesMock.mockReset();
    getInvoicesSummaryMock.mockReset();
  });

  it('aborts obsolete list and summary requests and keeps the latest responses', async () => {
    const oldList = deferred<InvoiceResponse>();
    const newList = deferred<InvoiceResponse>();
    const oldSummary = deferred<InvoiceSummary>();
    const newSummary = deferred<InvoiceSummary>();
    getInvoicesMock.mockReturnValueOnce(oldList.promise).mockReturnValueOnce(newList.promise);
    getInvoicesSummaryMock.mockReturnValueOnce(oldSummary.promise).mockReturnValueOnce(newSummary.promise);

    const { rerender, result } = renderHook(
      ({ search }) => useInvoices({ search, page: 1, pageSize: 20 }),
      { initialProps: { search: 'старый' } },
    );

    await waitFor(() => expect(getInvoicesMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getInvoicesSummaryMock).toHaveBeenCalledTimes(1));
    const oldListSignal = getInvoicesMock.mock.calls[0][1]?.signal as AbortSignal;
    const oldSummarySignal = getInvoicesSummaryMock.mock.calls[0][1]?.signal as AbortSignal;
    expect(oldListSignal.aborted).toBe(false);
    expect(oldSummarySignal.aborted).toBe(false);

    rerender({ search: 'новый' });

    await waitFor(() => expect(getInvoicesMock).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(getInvoicesSummaryMock).toHaveBeenCalledTimes(2));
    expect(oldListSignal.aborted).toBe(true);
    expect(oldSummarySignal.aborted).toBe(true);

    await act(async () => {
      newList.resolve({ items: [invoice(2, 'NEW')], total: 2, page: 1, pageSize: 20 });
      newSummary.resolve(summary(200));
    });
    await waitFor(() => expect(result.current.invoices[0]?.number).toBe('NEW'));
    expect(result.current.pagination.total).toBe(2);
    expect(result.current.summary?.total.amount).toBe(200);

    await act(async () => {
      oldList.resolve({ items: [invoice(1, 'OLD')], total: 1, page: 1, pageSize: 20 });
      oldSummary.resolve(summary(100));
    });
    expect(result.current.invoices[0]?.number).toBe('NEW');
    expect(result.current.pagination.total).toBe(2);
    expect(result.current.summary?.total.amount).toBe(200);
  });

  it('aborts both active requests when the hook unmounts', async () => {
    getInvoicesMock.mockReturnValue(new Promise(() => undefined));
    getInvoicesSummaryMock.mockReturnValue(new Promise(() => undefined));

    const { unmount } = renderHook(() => useInvoices({ page: 1, pageSize: 20 }));
    await waitFor(() => expect(getInvoicesMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getInvoicesSummaryMock).toHaveBeenCalledTimes(1));
    const listSignal = getInvoicesMock.mock.calls[0][1]?.signal as AbortSignal;
    const summarySignal = getInvoicesSummaryMock.mock.calls[0][1]?.signal as AbortSignal;

    unmount();

    expect(listSignal.aborted).toBe(true);
    expect(summarySignal.aborted).toBe(true);
  });

  it('clears initial indicators when a manual refresh replaces the first requests', async () => {
    const firstList = deferred<InvoiceResponse>();
    const refreshedList = deferred<InvoiceResponse>();
    const firstSummary = deferred<InvoiceSummary>();
    const refreshedSummary = deferred<InvoiceSummary>();
    getInvoicesMock.mockReturnValueOnce(firstList.promise).mockReturnValueOnce(refreshedList.promise);
    getInvoicesSummaryMock
      .mockReturnValueOnce(firstSummary.promise)
      .mockReturnValueOnce(refreshedSummary.promise);

    const { result } = renderHook(() => useInvoices({ page: 1, pageSize: 20 }));
    await waitFor(() => expect(getInvoicesMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getInvoicesSummaryMock).toHaveBeenCalledTimes(1));

    let refreshPromise!: Promise<void>;
    act(() => {
      refreshPromise = result.current.refresh();
    });
    await waitFor(() => expect(getInvoicesMock).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(getInvoicesSummaryMock).toHaveBeenCalledTimes(2));

    await act(async () => {
      refreshedList.resolve({ items: [invoice(3, 'REFRESH')], total: 1, page: 1, pageSize: 20 });
      refreshedSummary.resolve(summary(300));
      await refreshPromise;
    });

    expect(result.current.loading).toBe(false);
    expect(result.current.refreshing).toBe(false);
    expect(result.current.summaryLoading).toBe(false);
    expect(result.current.invoices[0]?.number).toBe('REFRESH');
  });
});
