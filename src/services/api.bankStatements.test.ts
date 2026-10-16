import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('bank statement request identity', () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it.each([
    ['connection', '/bank-connections/12/sync-statements'],
    ['account', '/bank-connections/accounts/12/sync-statement'],
  ])('preserves caller identity and persisted batch for %s', async (scope, suffix) => {
    const period = { from: '2026-10-01', to: '2026-10-09', requestId: '568a4000-c8eb-4b6c-bc76-11b89c440f77' };
    const response = { status: 'Success', succeeded: 1, total: 1, accounts: [{ accountId: 12, inserted: 1, updated: 0, batchId: 91 }] };
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json(response));
    vi.stubGlobal('fetch', request);
    const result = scope === 'connection'
      ? await apiService.syncBankConnectionStatements(12, period)
      : await apiService.syncBankAccountStatement(12, period);
    expect(result.accounts[0].batchId).toBe(91);
    expect(request).toHaveBeenCalledTimes(1);
    const [url, options] = request.mock.calls[0];
    expect(String(url).endsWith(suffix)).toBe(true);
    expect(options?.method).toBe('POST');
    expect(JSON.parse(String(options?.body))).toEqual(period);
  });
});
