import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });
describe('appeal decision readonly HTTP contract', () => {
  it('preserves the legacy URL and normalizes an empty decision', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(null, { status: 204 })); vi.stubGlobal('fetch', fetch);
    expect(await apiService.getCurrentCaseAppealDecision(42)).toBeNull(); expect(String(fetch.mock.calls[0][0])).toMatch(/\/v2\/cases\/42\/appeal-decision$/); expect(fetch.mock.calls[0][1]).toMatchObject({ cache: 'no-store' });
  });
  it('binds both visible dates and forwards cancellation without cache', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(null)); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getCurrentCaseAppealDecision(42, { signal: controller.signal, expectedCourtDecisionAt: '2026-09-20T00:00:00', expectedAppealDeadlineAt: '2026-10-20T00:00:00' });
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://localhost'); expect(url.pathname).toMatch(/\/v2\/cases\/42\/appeal-decision$/); expect([...url.searchParams]).toEqual([['expectedCourtDecisionAt', '2026-09-20T00:00:00'], ['expectedAppealDeadlineAt', '2026-10-20T00:00:00']]); expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  });
  it('does not send an already cancelled request', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); controller.abort(); await expect(apiService.getCurrentCaseAppealDecision(42, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' }); expect(fetch).not.toHaveBeenCalled();
  });
  it.each([401, 403, 409, 503])('retains HTTP%s errors without retrying or creating a task', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ message: 'Даты изменились' }, { status })); vi.stubGlobal('fetch', fetch); await expect(apiService.getCurrentCaseAppealDecision(42)).rejects.toMatchObject({ status }); const reads = fetch.mock.calls.filter(([url]) => String(url).includes('/appeal-decision')); expect(reads).toHaveLength(1); expect(reads[0][1]?.method ?? 'GET').toBe('GET'); expect(fetch.mock.calls.filter(([url]) => !String(url).includes('/appeal-decision')).map(([url]) => String(url))).toEqual(status === 401 ? [expect.stringContaining('/auth/refresh')] : []);
  });
});
