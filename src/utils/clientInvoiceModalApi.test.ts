// @vitest-environment node

import { describe, expect, it, vi } from 'vitest';

import { bindClientInvoiceModalApi } from './clientInvoiceModalApi';
import type { Invoice, Payment } from '../types';

function createInvoice(): Invoice {
  return {
    id: 528,
    invoiceEntityId: 24,
    number: '24',
    date: '2026-04-02',
    dueDate: '2026-04-03',
    amount: 10_000,
    type: 'Income',
    status: 'Completed',
    isPaid: true,
    createdAt: '2026-04-02T10:00:00Z',
  } as Invoice;
}

function createPayment(): Payment {
  return {
    id: 901,
    invoiceId: 24,
    date: '2026-04-02',
    plannedDate: '2026-04-02',
    paidDate: '2026-04-03',
    amount: 10_000,
    type: 'Income',
    status: 'Completed',
    isPaid: true,
    description: 'Оплата по счету №24',
    notes: '',
    systemNotes: '',
    rescheduleCount: 0,
    paidAmount: 10_000,
    outstandingAmount: 0,
    hasPartialPayment: false,
    createdAt: '2026-04-02T10:05:00Z',
  } as Payment;
}

describe('bindClientInvoiceModalApi', () => {
  it('preserves api object context for invoice and payment loading', async () => {
    const invoice = createInvoice();
    const payment = createPayment();

    const service = {
      getInvoice: vi.fn(function (this: unknown, invoiceId: number) {
        if (this !== service) {
          throw new Error('lost invoice context');
        }

        return Promise.resolve({ ...invoice, id: invoiceId });
      }),
      getPayments: vi.fn(function (
        this: unknown,
        params: { from: string; to: string; clientId: number; caseId?: number },
      ) {
        if (this !== service) {
          throw new Error('lost payments context');
        }

        return Promise.resolve([{ ...payment, clientId: params.clientId }]);
      }),
    };

    const { fetchInvoice, fetchPayments } = bindClientInvoiceModalApi(service);

    await expect(fetchInvoice(528)).resolves.toMatchObject({ id: 528, number: '24' });
    await expect(
      fetchPayments({
        from: '1900-01-01',
        to: '2100-12-31',
        clientId: 55,
        caseId: 701,
      }),
    ).resolves.toMatchObject([{ id: 901, clientId: 55 }]);

    expect(service.getInvoice).toHaveBeenCalledWith(528);
    expect(service.getPayments).toHaveBeenCalledWith({
      from: '1900-01-01',
      to: '2100-12-31',
      clientId: 55,
      caseId: 701,
    });
  });

  it('shows why raw detached api methods are unsafe for this flow', async () => {
    const service = {
      getInvoice: vi.fn(function (this: unknown, id: number) {
        void id;
        if (this !== service) {
          throw new Error('lost invoice context');
        }

        return Promise.resolve(createInvoice());
      }),
      getPayments: vi.fn(function (this: unknown, params: { from: string; to: string; clientId: number }) {
        void params;
        if (this !== service) {
          throw new Error('lost payments context');
        }

        return Promise.resolve([createPayment()]);
      }),
    };

    const detachedGetInvoice = service.getInvoice;
    const detachedGetPayments = service.getPayments;

    expect(() => detachedGetInvoice(528)).toThrow('lost invoice context');
    expect(() =>
      detachedGetPayments({
        from: '1900-01-01',
        to: '2100-12-31',
        clientId: 55,
      }),
    ).toThrow('lost payments context');
  });

  it('uses the paged payments api for direct invoice lookups', async () => {
    const payment = createPayment();

    const service = {
      getInvoice: vi.fn(() => Promise.resolve(createInvoice())),
      getPayments: vi.fn(() => Promise.resolve([])),
      getPaymentsV2: vi.fn(function (this: unknown, params: { invoiceId?: number }) {
        if (this !== service) {
          throw new Error('lost payments v2 context');
        }

        return Promise.resolve({ items: [{ ...payment, invoiceId: params.invoiceId }], total: 1 });
      }),
    };

    const { fetchPayments } = bindClientInvoiceModalApi(service);

    await expect(fetchPayments({ invoiceId: 528 })).resolves.toMatchObject([
      { id: 901, invoiceId: 528 },
    ]);

    expect(service.getPayments).not.toHaveBeenCalled();
    expect(service.getPaymentsV2).toHaveBeenCalledWith({ invoiceId: 528 });
  });
  it('owned adapter: loads every current invoice page with one signal and no cached data', async () => {
    const controller = new AbortController(); const getPaymentsV2 = vi.fn().mockResolvedValueOnce({ items: [createPayment()], total: 2, page: 1 }).mockResolvedValueOnce({ items: [{ ...createPayment(), id: 902 }], total: 2, page: 2 });
    const service = { getInvoice: vi.fn(), getPayments: vi.fn(), getPaymentsV2 }; const bound = bindClientInvoiceModalApi(service);
    expect((await bound.fetchPayments({ invoiceId: 528, page: 1, pageSize: 1 }, { signal: controller.signal })).map(row => row.id)).toEqual([901, 902]);
    expect(getPaymentsV2).toHaveBeenNthCalledWith(2, { invoiceId: 528, page: 2, pageSize: 1 }, { signal: controller.signal, cache: 'no-store' });
    expect(service.getPayments).not.toHaveBeenCalled();
  });
  it.each(['abort', 'owner', 'permission'])('owned adapter: %s prevents another page', async change => {
    const controller = new AbortController(); let current = true; let allowed = true;
    const service = { getInvoice: vi.fn(), getPayments: vi.fn(), getPaymentsV2: vi.fn().mockImplementation(async () => {
      if (change === 'abort') controller.abort(); if (change === 'owner') current = false; if (change === 'permission') allowed = false;
      return { items: [createPayment()], total: 2, page: 1 };
    }) };
    await expect(bindClientInvoiceModalApi(service).fetchPayments({ invoiceId: 528, page: 1, pageSize: 1 }, { signal: controller.signal, isCurrent: () => current, canReadPayments: () => allowed })).rejects.toBeInstanceOf(Error);
    expect(service.getPaymentsV2).toHaveBeenCalledOnce(); expect(service.getPayments).not.toHaveBeenCalled();
  });
  it.each([
    { items: [], total: 2, page: 1 }, { items: [createPayment()], total: -1, page: 1 },
    { items: [createPayment()], total: NaN, page: 1 }, { items: [createPayment()], total: Infinity, page: 1 },
    { items: [createPayment()], total: 0.5, page: 1 }, { items: [createPayment()], total: 1, page: 2 },
    { items: null, total: 1, page: 1 }, { items: [createPayment()], page: 1 },
  ])('owned adapter: malformed page %# cannot look complete', async response => {
    const service = { getInvoice: vi.fn(), getPayments: vi.fn(), getPaymentsV2: vi.fn().mockResolvedValue(response) };
    await expect(bindClientInvoiceModalApi(service).fetchPayments({ invoiceId: 528, page: 1, pageSize: 1 })).rejects.toThrow('Не удалось подтвердить');
    expect(service.getPaymentsV2).toHaveBeenCalledOnce();
  });
  it.each(['duplicate', 'changedTotal'])('owned adapter: %s across pages stops partial acceptance', async change => {
    const service = { getInvoice: vi.fn(), getPayments: vi.fn(), getPaymentsV2: vi.fn().mockResolvedValueOnce({ items: [createPayment()], total: 2, page: 1 })
      .mockResolvedValueOnce({ items: [{ ...createPayment(), id: change === 'duplicate' ? 901 : 902 }], total: change === 'changedTotal' ? 3 : 2, page: 2 }) };
    await expect(bindClientInvoiceModalApi(service).fetchPayments({ invoiceId: 528, page: 1, pageSize: 1 })).rejects.toThrow('Не удалось подтвердить');
    expect(service.getPaymentsV2).toHaveBeenCalledTimes(2);
  });
  it('owned adapter: unavailable direct API cannot become an unscoped legacy query', async () => {
    const service = { getInvoice: vi.fn(), getPayments: vi.fn() };
    await expect(bindClientInvoiceModalApi(service).fetchPayments({ invoiceId: 528 })).rejects.toThrow('HTTP 501'); expect(service.getPayments).not.toHaveBeenCalled();
  });
  it.each([0, -1, NaN, Infinity, 1.5])('owned adapter: invalid client ID %s cannot load the whole catalogue', async clientId => {
    const service = { getInvoice: vi.fn(), getPayments: vi.fn() };
    await expect(bindClientInvoiceModalApi(service).fetchPayments({ clientId })).rejects.toThrow('Не удалось подтвердить'); expect(service.getPayments).not.toHaveBeenCalled();
  });

});
