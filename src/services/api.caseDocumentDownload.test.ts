import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });
describe('case file download HTTP cancellation', () => {
  it('retains legacy route, blob and file name while forwarding signal without cache', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => new Response('contents', { headers: { 'Content-Disposition': 'attachment; filename="case.pdf"' } })); vi.stubGlobal('fetch', fetch);
    const legacy = await apiService.downloadCaseDocument(19, 41); const controller = new AbortController(); const scoped = await apiService.downloadCaseDocument(19, 41, { signal: controller.signal });
    expect(await legacy.blob.text()).toBe('contents'); expect(scoped.fileName).toBe('case.pdf'); expect(fetch).toHaveBeenCalledTimes(2); expect(fetch.mock.calls.every(([url]) => String(url).endsWith('/v2/cases/19/documents/41/download'))).toBe(true); expect(fetch.mock.calls[1][1]).toMatchObject({ method: 'GET', cache: 'no-store', signal: controller.signal, headers: { Authorization: expect.any(String) } });
  });
  it('does not dispatch an already cancelled download', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); controller.abort(); await expect(apiService.downloadCaseDocument(19, 41, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' }); expect(fetch).not.toHaveBeenCalled();
  });
  it.each([403, 503])('preserves HTTP %s refusal instead of creating a downloadable blob', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ message: 'File access refused' }, { status })); vi.stubGlobal('fetch', fetch); await expect(apiService.downloadCaseDocument(19, 41, { signal: new AbortController().signal })).rejects.toThrow('File access refused'); expect(fetch).toHaveBeenCalledOnce();
  });
  it('allows cancellation of the native HTTP request after dispatch', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, init) => new Promise((_resolve, reject) => init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true }))); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); const pending = apiService.downloadCaseDocument(19, 41, { signal: controller.signal }); controller.abort(); await expect(pending).rejects.toMatchObject({ name: 'AbortError' }); expect(fetch).toHaveBeenCalledOnce();
  });
});
