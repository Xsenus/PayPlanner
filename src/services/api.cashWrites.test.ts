import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import type { CashOrder } from '../types';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); session.restore(); });

const draft: CashOrder = { id: 0, number: '', type: 'Incoming', status: 'Draft', date: '2026-10-30', amount: 99.95,
  currency: 'RUB', selectedMoneyAccountId: 103, clientId: null, paymentId: null, moneyOperationId: null, invoiceId: null,
  payerName: 'Синтетический плательщик', createdAt: '2026-10-30T01:00:00Z' };

describe('cash order creation', () => {
  it('sends ordinary fields and account ID and accepts the created order without a payment key', async () => {
    const saved = { ...draft, id: 77, number: 'synthetic-order' };
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(saved, { status: 201 })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.createCashOrder(draft)).resolves.toMatchObject(saved); expect(fetch).toHaveBeenCalledOnce();
    const [url, options] = fetch.mock.calls[0]; expect(String(url).endsWith('/cash-orders')).toBe(true); expect(options?.method).toBe('POST');
    const body = JSON.parse(String(options?.body)); expect(body).toEqual(draft);
    for (const field of ['client', 'payment', 'moneyOperation', 'invoice']) expect(body).not.toHaveProperty(field);
    expect(new Headers(options?.headers).has('Idempotency-Key')).toBe(false);
  });

  it('keeps a readable related-record refusal and does not repeat the POST', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json('Связанные записи создавайте отдельно.', { status: 400 })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.createCashOrder(draft)).rejects.toMatchObject({ status: 400, message: expect.stringContaining('отдельно') }); expect(fetch).toHaveBeenCalledOnce();
  });

  it.each([403, 409, 503])('HTTP %s cannot fall back to another creation route', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ message: 'Сохранение не подтверждено.' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.createCashOrder(draft)).rejects.toMatchObject({ status }); expect(fetch).toHaveBeenCalledOnce();
  });

  it('does not create another order after a lost response', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new TypeError('Failed to fetch')); vi.stubGlobal('fetch', fetch);
    await expect(apiService.createCashOrder(draft)).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
});
