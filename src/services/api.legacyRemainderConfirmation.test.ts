import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('legacy remainder confirmation transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('sends the confirmed total and opaque observed snapshot unchanged', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ createdRemainder: true }));
    vi.stubGlobal('fetch', request);
    const confirmation = { originalAmount: 100, expectedAmount: 40, expectedPaidAmount: 40, confirmationToken: 'remainder-v1:observed' };
    await apiService.transferExistingPaymentRemainder(91, '2026-11-15', confirmation);
    expect(request).toHaveBeenCalledTimes(1);
    expect(String(request.mock.calls[0][0]).endsWith('/payments/91/partial-remainder/from-existing')).toBe(true);
    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual({ remainderDate: '2026-11-15', ...confirmation });
  });

  it('keeps the date-only form for a debt already recorded in the payment', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ createdRemainder: true })); vi.stubGlobal('fetch', request);
    await apiService.transferExistingPaymentRemainder(91, '2026-11-15');
    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual({ remainderDate: '2026-11-15' });
  });

  it('reports a stale confirmation without retrying the write', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ message: 'Исходный платёж изменился.' }, { status: 409 })); vi.stubGlobal('fetch', request);
    await expect(apiService.transferExistingPaymentRemainder(91, '2026-11-15', {
      originalAmount: 100, expectedAmount: 40, expectedPaidAmount: 40, confirmationToken: 'old',
    })).rejects.toThrow('Исходный платёж изменился.');
    expect(request).toHaveBeenCalledTimes(1);
  });
});
