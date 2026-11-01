import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('contact action read cancellation', () => {
  afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });

  it('keeps the merge preview payload and forwards a cancellable uncached read', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json({ canMerge: false })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); const payload = { primarySourceType: 'external' as const, primarySourceId: 1, duplicateSourceType: 'external' as const, duplicateSourceId: 2 };
    await apiService.previewContactMerge(payload, { signal: controller.signal });
    const [url, options] = fetch.mock.calls[0]; expect(String(url)).toMatch(/\/contacts\/merge\/preview$/);
    expect(options.method).toBe('POST'); expect(JSON.parse(options.body)).toEqual(payload); expect(options.signal).toBe(controller.signal); expect(options.cache).toBe('no-store');
    await apiService.previewContactMerge(payload); expect(fetch.mock.calls[1][1].signal).toBeUndefined();
  });
  it('downloads the current XML with cancellation, unchanged filename and blob contents', async () => {
    const fetch = vi.fn().mockImplementation(async () => new Response('<safe/>', { headers: { 'content-disposition': 'attachment; filename="safe.xml"' } })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); const value = await apiService.downloadYealinkContactsXml({ signal: controller.signal });
    expect(value.fileName).toBe('safe.xml'); expect(await value.blob.text()).toBe('<safe/>');
    expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store', method: 'GET' });
    await apiService.downloadYealinkContactsXml(); expect(fetch.mock.calls[1][1].signal).toBeUndefined();
  });
  it('retains export errors instead of treating a denied response as a downloadable file', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Доступ закрыт', { status: 403, statusText: 'Forbidden' })));
    await expect(apiService.downloadYealinkContactsXml()).rejects.toThrow('HTTP 403 Forbidden');
  });
  it('cancels only the import dry run and preserves the explicitly confirmed write payload', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json({ dryRun: true })); vi.stubGlobal('fetch', fetch);
    const file = new File(['<safe/>'], 'safe.xml'); const controller = new AbortController();
    await apiService.importYealinkContactsXml(file, { dryRun: true, signal: controller.signal });
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/contacts\/imports\/yealink\?dryRun=true$/); expect(fetch.mock.calls[0][1].signal).toBe(controller.signal);
    const decisions = [{ rowId: 1, action: 'skip' as const }];
    await apiService.importYealinkContactsXml(file, { decisions, signal: controller.signal });
    const [url, options] = fetch.mock.calls[1]; expect(String(url)).toMatch(/\/contacts\/imports\/yealink$/); expect(options.signal).toBeUndefined();
    expect(JSON.parse(options.body.get('decisionsJson'))).toEqual(decisions); expect(options.body.get('file').name).toBe('safe.xml');
    await apiService.importYealinkContactsXml(file, true); expect(String(fetch.mock.calls[2][0])).toMatch(/\?dryRun=true$/);
  });
});
