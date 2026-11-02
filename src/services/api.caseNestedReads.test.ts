import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });
const readers = [{ name: 'preparation', path: '/v2/cases/42/hearing-preparation', read: (signal?: AbortSignal) => apiService.getCaseHearingPreparation(42, signal ? { signal } : undefined) }, { name: 'templates', path: '/v2/cases/position-templates', read: (signal?: AbortSignal) => apiService.getCasePositionTemplates(signal ? { signal } : undefined) }];
describe('nested case readonly transport', () => {
  it.each(readers)('$name preserves legacy route and forwards signal without cache', async ({ path, read }) => { const fetch=vi.fn<typeof globalThis.fetch>().mockImplementation(async()=>Response.json({}));vi.stubGlobal('fetch',fetch);await read();const controller=new AbortController();await read(controller.signal);expect(fetch).toHaveBeenCalledTimes(2);expect(fetch.mock.calls.every(([url])=>String(url).endsWith(path))).toBe(true);expect(fetch.mock.calls[1][1]).toMatchObject({signal:controller.signal,cache:'no-store'}); });
  it.each(readers)('$name does not dispatch after prior cancellation',async({read})=>{const fetch=vi.fn<typeof globalThis.fetch>();vi.stubGlobal('fetch',fetch);const controller=new AbortController();controller.abort();await expect(read(controller.signal)).rejects.toMatchObject({name:'AbortError'});expect(fetch).not.toHaveBeenCalled();});
  it.each(readers.flatMap(row=>[403,503].map(status=>({...row,status}))))('$name preserves HTTP$status refusal',async({read,status})=>{const fetch=vi.fn<typeof globalThis.fetch>().mockImplementation(async()=>Response.json({message:'Nested data unavailable'},{status}));vi.stubGlobal('fetch',fetch);await expect(read(new AbortController().signal)).rejects.toMatchObject({status});expect(fetch).toHaveBeenCalledOnce();});
});
