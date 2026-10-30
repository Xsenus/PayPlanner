import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

describe('company branch read transport', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { session = documentCopyTestSession(); });
  afterEach(() => { session.restore(); vi.unstubAllGlobals(); });

  it.each([false, true])('forwards cancellation and prevents cached branch data with archived=%s', async archived => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response('[]', { headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request); const controller = new AbortController();
    await expect(apiService.getCompanyBranches(5, archived, controller.signal)).resolves.toEqual([]);
    expect(request).toHaveBeenCalledOnce();
    expect(String(request.mock.calls[0][0])).toMatch(new RegExp(`/legal-entities/5/branches${archived ? '\\?includeArchived=true' : ''}$`));
    expect(request.mock.calls[0][1]).toEqual(expect.objectContaining({ signal: controller.signal, cache: 'no-store' }));
  });

  it('preserves an explicit read failure without retrying', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ message: 'Нет доступа к подразделениям.' }), { status: 403, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.getCompanyBranches(5)).rejects.toThrow('Нет доступа к подразделениям.');
    expect(request).toHaveBeenCalledOnce();
  });
});
