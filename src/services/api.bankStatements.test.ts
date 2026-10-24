import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('bank statement request identity', () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it('sends the selected company and exact money account as multipart fields with the file', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ rowsRead: 0, created: 0, skipped: 0, errors: [] }));
    vi.stubGlobal('fetch', request);
    const file = new File(['synthetic'], 'statement.csv', { type: 'text/csv' });

    await apiService.importBankStatement(file, { companyId: 22, moneyAccountId: 202 });

    expect(request).toHaveBeenCalledTimes(1);
    const [url, options] = request.mock.calls[0];
    expect(String(url).endsWith('/money-operations/bank-statement/import')).toBe(true);
    expect(options?.method).toBe('POST');
    const body = options?.body as FormData;
    expect(body).toBeInstanceOf(FormData);
    expect(Array.from(body.keys()).sort()).toEqual(['companyId', 'file', 'moneyAccountId']);
    expect(body.get('file')).toEqual(file);
    expect(body.get('companyId')).toBe('22');
    expect(body.get('moneyAccountId')).toBe('202');
    expect(new Headers(options?.headers).has('Content-Type')).toBe(false);
  });

  it('sends the same explicit company and account in the folder run query', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ filesFound: 0, created: 0, skipped: 0, errors: [] }));
    vi.stubGlobal('fetch', request);

    await apiService.runBankStatementAutoImport({ companyId: 22, moneyAccountId: 202 });

    expect(request).toHaveBeenCalledTimes(1);
    const [url, options] = request.mock.calls[0];
    const endpoint = new URL(String(url), 'http://localhost');
    expect(endpoint.pathname.endsWith('/money-operations/bank-statement/auto-import/run')).toBe(true);
    expect(Array.from(endpoint.searchParams.entries()).sort()).toEqual([['companyId', '22'], ['moneyAccountId', '202']]);
    expect(options?.method).toBe('POST');
    expect(options?.body).toBeUndefined();
  });

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
