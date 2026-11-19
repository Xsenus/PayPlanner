import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });
const response = (payload: unknown) => new Response(JSON.stringify(payload), { headers: { 'content-type': 'application/json' } });

describe('contract expiration recipient API', () => {
  it('reads uncached recipient settings from the notifications endpoint', async () => {
    const body = { departmentManagersEnabled: false, roleIds: [], userIds: [], departmentManagers: [], selectedRoles: [], selectedUsers: [], selectedDepartments: [] };
    const fetch = vi.fn().mockResolvedValue(response(body));
    vi.stubGlobal('fetch', fetch);
    expect(await apiService.getContractExpirationRecipientSettings()).toEqual(body);
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/notifications\/contract-expiration$/);
    expect(fetch.mock.calls[0][1].cache).toBe('no-store');
  });

  it('writes the complete combination without selection label metadata', async () => {
    const body = { departmentManagersEnabled: true, roleIds: [2], userIds: [7], departmentManagers: [{ departmentId: 3, managerUserId: 7 }] };
    const fetch = vi.fn().mockResolvedValue(response(body));
    vi.stubGlobal('fetch', fetch);
    await apiService.updateContractExpirationRecipientSettings(body);
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/notifications\/contract-expiration$/);
    expect(fetch.mock.calls[0][1].method).toBe('PUT');
    expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual(body);
  });

  it('encodes literal directory search with bounded paging and cancellation', async () => {
    const fetch = vi.fn().mockResolvedValue(response({ items: [], page: 2, pageSize: 100, totalCount: 0 }));
    vi.stubGlobal('fetch', fetch);
    const abort = new AbortController();
    await apiService.getContractExpirationRecipientOptions({ kind: 'users', search: '  Ёжик &%_?  ', page: 2, pageSize: 1000 }, { signal: abort.signal });
    const url = new URL(String(fetch.mock.calls[0][0]), 'https://fixture.local');
    expect(url.pathname).toMatch(/\/notifications\/contract-expiration\/options$/);
    expect([...url.searchParams]).toEqual([['kind', 'users'], ['search', 'Ёжик &%_?'], ['page', '2'], ['pageSize', '100']]);
    expect(fetch.mock.calls[0][1].signal).toBe(abort.signal);
    expect(fetch.mock.calls[0][1].cache).toBe('no-store');
  });

  it.each(['roles', 'departments'] as const)('uses a bounded first page for %s without an empty search parameter', async (kind) => {
    const fetch = vi.fn().mockResolvedValue(response({ items: [], page: 1, pageSize: 50, totalCount: 0 }));
    vi.stubGlobal('fetch', fetch);
    await apiService.getContractExpirationRecipientOptions({ kind });
    const url = new URL(String(fetch.mock.calls[0][0]), 'https://fixture.local');
    expect([...url.searchParams]).toEqual([['kind', kind], ['page', '1'], ['pageSize', '50']]);
  });

  it('preserves a server validation message and status', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ message: 'Выберите действующего сотрудника.' }), { status: 400, headers: { 'content-type': 'application/json' } })));
    await expect(apiService.updateContractExpirationRecipientSettings({ departmentManagersEnabled: false, roleIds: [], userIds: [99], departmentManagers: [] }))
      .rejects.toMatchObject({ status: 400, message: expect.stringContaining('Выберите действующего сотрудника.') });
  });
});
