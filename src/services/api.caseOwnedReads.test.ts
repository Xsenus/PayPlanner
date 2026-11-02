import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });
const readers = [
  { name: 'client finances', path: '/clients/13/case-finance-summaries', read: (signal?: AbortSignal) => apiService.getClientCaseFinanceSummaries(13, undefined, signal ? { signal } : undefined) },
  { name: 'organization finances', path: '/legal-entities/17/case-finance-summaries', read: (signal?: AbortSignal) => apiService.getLegalEntityCaseFinanceSummaries(17, signal ? { signal } : undefined) },
  { name: 'related parties', path: '/v2/cases/19/related-parties?includeArchived=true', read: (signal?: AbortSignal) => apiService.getCaseRelatedParties(19, true, signal ? { signal } : undefined) },
  { name: 'document templates', path: '/v2/cases/document-templates', read: (signal?: AbortSignal) => apiService.getCaseDocumentTemplates(signal ? { signal } : undefined) },
];
describe('owned case read HTTP options', () => {
  it.each(readers)('retains legacy $name route and forwards cancellation without cached data', async ({ path, read }) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); await read(); await read(controller.signal);
    expect(fetch).toHaveBeenCalledTimes(2); expect(fetch.mock.calls.every(([url]) => String(url).endsWith(path))).toBe(true);
    expect(fetch.mock.calls[1][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
    expect(fetch.mock.calls.every(([, options]) => !options?.method || options.method === 'GET')).toBe(true);
  });
  it.each(readers)('does not dispatch an already cancelled $name read', async ({ read }) => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); controller.abort();
    await expect(read(controller.signal)).rejects.toMatchObject({ name: 'AbortError' }); expect(fetch).not.toHaveBeenCalled();
  });
  it.each(readers.flatMap(row => [403, 503].map(status => ({ ...row, status }))))('preserves HTTP $status failure for $name', async ({ read, status }) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ message: 'No case access' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(read(new AbortController().signal)).rejects.toMatchObject({ status }); expect(fetch).toHaveBeenCalledOnce();
  });
});
