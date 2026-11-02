import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';
import { useUserTimeZone } from '../hooks/useUserTimeZone';
const key = 'profile.timeZoneId';
let session: ReturnType<typeof documentCopyTestSession>; let sequence = 0;
beforeEach(() => { session = documentCopyTestSession(); localStorage.setItem('pp.auth-session-epoch.v1', `setting-http-${++sequence}`); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });
describe('owned user setting transport', () => {
  it('keeps legacy calls and encoded keys, supplies an optional read signal and disables cached profile reads', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ key, value: 'Europe/Samara' })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); await apiService.getUserSetting('profile key/one'); await apiService.getUserSetting(key, { signal: controller.signal });
    expect(String(fetch.mock.calls[0][0])).toContain('profile%20key%2Fone'); expect(fetch.mock.calls[1][1]?.signal).toBe(controller.signal);
    expect(fetch.mock.calls.every(([,options]) => options?.cache === 'no-store')).toBe(true);
  });
  it.each([204, 404])('keeps an actual HTTP %s missing setting nullable', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(status === 204 ? new Response(null, { status }) : Response.json({ message: 'Missing' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.getUserSetting(key)).resolves.toBeNull(); expect(fetch).toHaveBeenCalledOnce();
  });
  it.each([403, 500])('does not mistake HTTP %s mentioning 404 for an absent setting', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ message: 'upstream HTTP 404 Not Found' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.getUserSetting(key)).rejects.toMatchObject({ status }); expect(fetch).toHaveBeenCalledOnce();
  });
  it('keeps the PUT route and JSON body with legacy calls or an optional cancellation signal', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ key, value: 'UTC' })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); await apiService.upsertUserSetting(key, 'UTC'); await apiService.upsertUserSetting(key, 'UTC', { signal: controller.signal });
    expect(fetch).toHaveBeenCalledTimes(2); expect(fetch.mock.calls[1][1]).toMatchObject({ method: 'PUT', body: JSON.stringify({ value: 'UTC' }), signal: controller.signal });
  });
  it('does not dispatch already cancelled profile reads or writes', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch); const controller = new AbortController(); controller.abort();
    await expect(apiService.getUserSetting(key, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    await expect(apiService.upsertUserSetting(key, 'UTC', { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    await expect(apiService.initializeUserSetting(key, 'UTC', { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' }); expect(fetch).not.toHaveBeenCalled();
  });
  it('forwards document status signal through the existing dictionary route without changing legacy reads', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getDocumentStatuses(); await apiService.getDocumentStatuses({ signal: controller.signal });
    expect(fetch).toHaveBeenCalledTimes(2); expect(fetch.mock.calls.every(([url]) => String(url).endsWith('/dictionaries/document-statuses'))).toBe(true);
    expect(fetch.mock.calls[1][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  });
  it('handles the real 204 hook path with exactly one own-profile initialization and unchanged JSON', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (_url, options) => options?.method === 'PUT'
      ? Response.json({ key, value: JSON.parse(String(options.body)).value }) : new Response(null, { status: 204 })); vi.stubGlobal('fetch', fetch);
    const view = renderHook(() => useUserTimeZone()); await waitFor(() => expect(fetch.mock.calls.filter(([,options]) => options?.method === 'PUT')).toHaveLength(1));
    const write = fetch.mock.calls.find(([,options]) => options?.method === 'PUT')!;
    expect(JSON.parse(String(write[1]?.body))).toEqual({ value: view.result.current });
    expect(String(write[0])).toContain('/profile.timeZoneId/initialize'); expect(write[1]?.signal).toBeInstanceOf(AbortSignal);
    view.rerender(); await act(async () => {}); expect(fetch.mock.calls.filter(([,options]) => options?.method === 'PUT')).toHaveLength(1);
  });
  it('does not fall back to an ordinary PUT when an older server lacks initialization', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (_url, options) => options?.method === 'PUT'
      ? Response.json({ message: 'Endpoint missing' }, { status: 404 }) : new Response(null, { status: 204 })); vi.stubGlobal('fetch', fetch);
    renderHook(() => useUserTimeZone()); await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2)); await act(async () => {});
    expect(fetch.mock.calls.filter(([,options]) => options?.method === 'PUT')).toHaveLength(1);
    expect(String(fetch.mock.calls.find(([,options]) => options?.method === 'PUT')![0])).toContain('/profile.timeZoneId/initialize');
  });
  it('never initializes another login from a pending old actual HTTP 404', async () => {
    let finish!: (value: Response) => void; const fetch = vi.fn<typeof globalThis.fetch>().mockReturnValueOnce(new Promise(resolve => { finish = resolve; })); vi.stubGlobal('fetch', fetch);
    const view = renderHook(() => useUserTimeZone()); await waitFor(() => expect(fetch).toHaveBeenCalledOnce()); view.unmount(); session.replace();
    await act(async () => finish(Response.json({ message: 'Missing' }, { status: 404 }))); expect(fetch).toHaveBeenCalledOnce();
  });
});
