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

        return Promise.resolve({ items: [{ ...payment, invoiceId: params.invoiceId }] });
      }),
    };

    const { fetchPayments } = bindClientInvoiceModalApi(service);

    await expect(fetchPayments({ invoiceId: 528 })).resolves.toMatchObject([
      { id: 901, invoiceId: 528 },
    ]);

    expect(service.getPayments).not.toHaveBeenCalled();
    expect(service.getPaymentsV2).toHaveBeenCalledWith({ invoiceId: 528 });
  });
});
