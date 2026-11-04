import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('client company lookup read contract', () => {
  afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });
  it('retains paged search parameters and forwards cancellation without caching company rows', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json({ items: [], total: 0, page: 1, pageSize: 20 })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); await apiService.getLegalEntitiesPaged({ search: 'Ёжик & ИНН', page: 1, pageSize: 20 }, { signal: controller.signal });
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://fixture.local');
    expect(url.pathname).toMatch(/\/legal-entities\/paged$/); expect(url.searchParams.get('search')).toBe('Ёжик & ИНН'); expect(url.searchParams.get('pageSize')).toBe('20');
    expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
    await apiService.getLegalEntitiesPaged(); expect(fetch.mock.calls[1][1].signal).toBeUndefined(); expect(fetch.mock.calls[1][1].cache).toBe('no-store');
  });
  it('organization directory transport preserves bounded search, role and cancellation without rich rows', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json({ items: [{ id: 25, shortName: 'Company' }], total: 1, page: 2, pageSize: 50 }));
    vi.stubGlobal('fetch', fetch);
    const controller = new AbortController();
    const result = await apiService.getLegalEntityDirectoryPage({ search: 'Имя & телефон', page: 2, pageSize: 50, counterpartyRole: 'supplier' }, { signal: controller.signal });
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://fixture.local');
    expect(url.pathname).toMatch(/\/legal-entities\/directory$/);
    expect(Object.fromEntries(url.searchParams)).toEqual({ search: 'Имя & телефон', page: '2', pageSize: '50', counterpartyRole: 'supplier' });
    expect(result.items).toEqual([{ id: 25, shortName: 'Company' }]);
    expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
    await apiService.getLegalEntityDirectoryPage();
    const defaultUrl = new URL(String(fetch.mock.calls[1][0]), 'http://fixture.local');
    expect(defaultUrl.searchParams.get('page')).toBe('1');
    expect(defaultUrl.searchParams.get('pageSize')).toBe('50');
    expect(fetch.mock.calls[1][1].signal).toBeUndefined();
    expect(fetch.mock.calls[1][1].cache).toBe('no-store');
  });
  it('retains a single company identity and provides cancellation with the previous uncached default', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json({ id: 21, shortName: 'Контрольная компания' })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); expect(await apiService.getLegalEntity(21, { signal: controller.signal })).toMatchObject({ id: 21 });
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/legal-entities\/21$/); expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
    await apiService.getLegalEntity(21); expect(fetch.mock.calls[1][1].signal).toBeUndefined();
  });

  it('participant directory transport: contacts preserve paging search cancellation and uncached reads', async () => {
    const fetch = vi.fn().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController();
    await apiService.getContacts({ scope: 'external', search: 'Имя & телефон', page: 2, pageSize: 50 }, { signal: controller.signal });
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://fixture.local');
    expect(url.pathname).toMatch(/\/contacts$/);
    expect(url.searchParams.get('scope')).toBe('external');
    expect(url.searchParams.get('search')).toBe('Имя & телефон');
    expect(url.searchParams.get('page')).toBe('2');
    expect(url.searchParams.get('pageSize')).toBe('50');
    expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
    await apiService.getContacts();
    expect(fetch.mock.calls[1][1].signal).toBeUndefined();
    expect(fetch.mock.calls[1][1].cache).toBe('no-store');
  });

});
