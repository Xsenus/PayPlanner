import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { session.restore(); vi.unstubAllGlobals(); });

type ReadOptions = { signal?: AbortSignal };
const reads: { name: string; path: string; call: (options?: ReadOptions) => Promise<unknown> }[] = [
  { name: 'case', path: '/v2/cases/15', call: options => apiService.getCaseV2(15, options) },
  { name: 'features', path: '/features', call: options => apiService.getFeatureFlags(options) },
  { name: 'timeline', path: '/v2/cases/15/timeline', call: options => apiService.getCaseTimeline(15, options) },
  { name: 'conversation', path: '/v2/cases/15/conversation', call: options => apiService.getCaseConversation(15, options) },
  { name: 'finance', path: '/v2/cases/15/finance', call: options => apiService.getCaseFinance(15, options) },
  { name: 'sync logs', path: '/v2/cases/15/court-sync/logs', call: options => apiService.getCaseCourtSyncLogs(15, options) },
  { name: 'instances', path: '/v2/cases/15/court-instances', call: options => apiService.getCaseCourtInstances(15, options) },
  { name: 'events', path: '/v2/cases/15/court-events', call: options => apiService.getCaseCourtEvents(15, options) },
  { name: 'deadlines', path: '/v2/cases/15/procedural-deadlines', call: options => apiService.getCaseProceduralDeadlines(15, options) },
  { name: 'document types', path: '/v2/cases/document-types', call: options => apiService.getCaseDocumentTypes(options) },
  { name: 'external approval', path: '/v2/cases/15/work-order/external-approval', call: options => apiService.getExternalWorkOrderApproval(15, options) },
  { name: 'court directory', path: '/v2/cases/court-directory', call: options => apiService.getCaseCourtDirectory(false, options) },
  { name: 'court parties', path: '/v2/cases/15/court-parties', call: options => apiService.getCaseCourtParties(15, false, options) },
];

describe('case detail read transport', () => {
  it.each(reads)('$name preserves legacy URL and JSON while forwarding a current signal without caching', async ({ path, call }) => {
    const body = { source: 'synthetic response' };
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json(body));
    vi.stubGlobal('fetch', fetch);
    expect(await call()).toEqual(body);
    const controller = new AbortController();
    expect(await call({ signal: controller.signal })).toEqual(body);
    expect(fetch).toHaveBeenCalledTimes(2);
    for (const [url, init] of fetch.mock.calls) {
      expect(String(url)).toMatch(new RegExp(`${path}$`));
      expect(init?.body).toBeUndefined();
      expect(init?.method ?? 'GET').toBe('GET');
    }
    expect(fetch.mock.calls[1][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  });

  it.each(reads)('$name does not dispatch an already cancelled read', async ({ call }) => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); controller.abort();
    await expect(call({ signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([401, 403, 404, 409, 503])('retains HTTP%s and never repeats the case read', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ message: 'Дело недоступно' }, { status }));
    vi.stubGlobal('fetch', fetch);
    await expect(apiService.getCaseV2(15)).rejects.toMatchObject({ status });
    expect(fetch.mock.calls.filter(([url]) => String(url).endsWith('/v2/cases/15'))).toHaveLength(1);
  });

  it('preserves explicit archived and inactive filters', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([]));
    vi.stubGlobal('fetch', fetch);
    await apiService.getCaseCourtDirectory(true);
    await apiService.getCaseCourtParties(15, true);
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/v2\/cases\/court-directory\?includeInactive=true$/);
    expect(String(fetch.mock.calls[1][0])).toMatch(/\/v2\/cases\/15\/court-parties\?includeArchived=true$/);
  });
});
