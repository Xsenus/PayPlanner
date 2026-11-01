import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, StrictMode, type PropsWithChildren } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useClients, useClientStats, type ClientsListMode } from './useClients';
import { apiService, type PagedResult } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';
import type { Client, ClientInput, ClientStats } from '../types';

vi.mock('../services/api', () => ({ apiService: { getClients: vi.fn(), getClientsV2: vi.fn(), createClient: vi.fn(), updateClient: vi.fn(), deleteClient: vi.fn(), getClientStats: vi.fn() } }));
const client = (id = 1, name = `Клиент ${id}`): Client => ({ id, name, phone: '', email: '', company: '', address: '', notes: '', createdAt: '2026-11-01T00:00:00Z', isActive: true, cases: [], contracts: [] });
const payload: ClientInput = { name: 'Изменённый клиент', phone: '', email: '', company: '', address: '', notes: '', isActive: true };
const page = (items: Client[]): PagedResult<Client> => ({ items, total: items.length, page: 1, pageSize: 25 });
const statistics = (clientId: number): ClientStats => ({ clientId, clientName: `Клиент ${clientId}`, totalIncome: 100, totalExpenses: 20, netAmount: 80, totalPayments: 2, paidPayments: 1, pendingPayments: 1, overduePayments: 0, outstandingIncome: 5, outstandingExpenses: 0, outstandingTotal: 5, recentPayments: [] });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }

describe('owned client directory', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getClients).mockResolvedValue([client()]); vi.mocked(apiService.getClientsV2).mockResolvedValue(page([client()])); vi.mocked(apiService.getClientStats).mockImplementation(async id => statistics(id)); });
  afterEach(() => session.restore());
  it.each<ClientsListMode>(['all', 'paged'])('client baseline: %s clears cached clients on a new login before any reply', async mode => {
    const view = renderHook(() => useClients({ mode })); await waitFor(() => expect(view.result.current.clients).toHaveLength(1));
    vi.mocked(apiService.getClients).mockReturnValue(new Promise(() => {})); vi.mocked(apiService.getClientsV2).mockReturnValue(new Promise(() => {}));
    session.replace(); view.rerender(); expect(view.result.current.clients).toEqual([]); expect(view.result.current.total).toBe(0);
  });
  it.each<ClientsListMode>(['all', 'paged'])('client baseline: %s discards nested detail after the caller scope changes', async mode => {
    const secret = { ...client(), cases: [{ id: 90, title: 'Закрытое дело' }] as Client['cases'], contracts: [{ id: 91, number: 'Закрытый договор' }] as Client['contracts'] };
    vi.mocked(apiService.getClients).mockResolvedValueOnce([secret]); vi.mocked(apiService.getClientsV2).mockResolvedValueOnce(page([secret]));
    const view = renderHook(({ ownerScope }) => useClients({ mode, ownerScope }), { initialProps: { ownerScope: 'all' } });
    await waitFor(() => expect(view.result.current.clients[0]?.cases).toHaveLength(1)); view.rerender({ ownerScope: 'own' });
    await waitFor(() => expect(view.result.current.clients[0]?.cases).toHaveLength(0)); expect(view.result.current.clients[0]?.contracts).toEqual([]);
  });
  it('client baseline: cached setClients cannot repaint a disabled directory', async () => {
    const view = renderHook(({ enabled }) => useClients({ enabled }), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const set = view.result.current.setClients; view.rerender({ enabled: false }); act(() => set([client(2, 'Прежний доступ')])); expect(view.result.current.clients).toEqual([]);
  });
  it('client baseline: an old update receipt is rejected after an authority change', async () => {
    const pending = deferred<Client>(); vi.mocked(apiService.updateClient).mockReturnValue(pending.promise);
    const view = renderHook(({ ownerScope }) => useClients({ ownerScope }), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    let observed!: Promise<unknown>; act(() => { observed = view.result.current.updateClient(1, payload).then(() => null, error => error); });
    view.rerender({ ownerScope: 'own' }); await act(async () => pending.resolve(client(1, 'Старое сохранение')));
    expect(await observed).toBeInstanceOf(Error); expect(view.result.current.clients[0]?.name).not.toBe('Старое сохранение');
  });
  it('client baseline: old statistics cannot replace another client', async () => {
    const old = deferred<ClientStats>(); vi.mocked(apiService.getClientStats).mockReturnValueOnce(old.promise);
    const view = renderHook(({ id }) => useClientStats(id), { initialProps: { id: 1 } }); view.rerender({ id: 2 }); await waitFor(() => expect(view.result.current.stats?.clientId).toBe(2));
    await act(async () => old.resolve(statistics(1))); expect(view.result.current.stats?.clientId).toBe(2);
  });
  it('client baseline: an absent statistics client clears data and loading', async () => {
    const view = renderHook(({ id }) => useClientStats(id), { initialProps: { id: 1 } }); await waitFor(() => expect(view.result.current.stats?.clientId).toBe(1));
    view.rerender({ id: 0 }); expect(view.result.current.stats).toBeNull(); expect(view.result.current.loading).toBe(false);
  });
  it('client regression: a paged summary keeps expanded details only within the current owner', async () => {
    const view = renderHook(() => useClients({ mode: 'paged' })); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const detail = { ...client(), cases: [{ id: 90, title: 'Разрешённое дело' }] as Client['cases'], contracts: [{ id: 91, number: 'Договор' }] as Client['contracts'] };
    act(() => view.result.current.setClients([detail])); await act(async () => { await view.result.current.refresh(); });
    expect(view.result.current.clients[0].cases).toEqual(detail.cases); expect(view.result.current.clients[0].contracts).toEqual(detail.contracts);
  });
  it.each<ClientsListMode>(['all', 'paged'])('client regression: current %s HTTP 403 clears cached rows and total', async mode => {
    const view = renderHook(() => useClients({ mode })); await waitFor(() => expect(view.result.current.loading).toBe(false));
    vi.mocked(apiService.getClients).mockRejectedValue(new Error('HTTP 403 Forbidden')); vi.mocked(apiService.getClientsV2).mockRejectedValue(new Error('HTTP 403 Forbidden'));
    await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.clients).toEqual([]); expect(view.result.current.total).toBe(0); expect(view.result.current.error).toContain('403');
  });
  it('client regression: an old denied read cannot clear a newer permitted owner', async () => {
    const old = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValueOnce(old.promise).mockResolvedValueOnce([client(2)]);
    const view = renderHook(({ ownerScope }) => useClients({ ownerScope }), { initialProps: { ownerScope: 'all' } }); view.rerender({ ownerScope: 'own' });
    await waitFor(() => expect(view.result.current.clients[0]?.id).toBe(2)); await act(async () => old.reject(new Error('HTTP 403 Old owner')));
    expect(view.result.current.clients[0]?.id).toBe(2); expect(view.result.current.error).toBeNull(); expect(view.result.current.loading).toBe(false);
  });
  it('client regression: disabling aborts and ignores a late read, including its loading flag', async () => {
    const old = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValue(old.promise);
    const view = renderHook(({ enabled }) => useClients({ enabled }), { initialProps: { enabled: true } });
    const signal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal; view.rerender({ enabled: false }); expect(signal?.aborted).toBe(true);
    await act(async () => old.resolve([client()])); expect(view.result.current.clients).toEqual([]); expect(view.result.current.loading).toBe(false); expect(view.result.current.refreshing).toBe(false);
  });
  it('client regression: cached refresh and functional setter cannot act on another search', async () => {
    const view = renderHook(({ search }) => useClients({ mode: 'paged', search }), { initialProps: { search: 'old' } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const old = view.result.current; view.rerender({ search: 'new' }); await waitFor(() => expect(view.result.current.refreshing).toBe(false));
    const callback = vi.fn(() => [client(9)]); await act(async () => { old.setClients(callback); await old.refresh(); });
    expect(callback).not.toHaveBeenCalled(); expect(apiService.getClientsV2).toHaveBeenCalledTimes(2); expect(view.result.current.clients[0]?.id).toBe(1);
  });
  it('client regression: a superseded refresh cannot finish the active refresh', async () => {
    const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const first = deferred<Client[]>(); const second = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    act(() => { void view.result.current.refresh(); void view.result.current.refresh(); }); await act(async () => first.resolve([client(3)]));
    expect(view.result.current.refreshing).toBe(true); expect(view.result.current.clients[0]?.id).toBe(1);
    await act(async () => second.resolve([client(4)])); expect(view.result.current.refreshing).toBe(false); expect(view.result.current.clients[0]?.id).toBe(4);
  });
  it('client regression: duplicate identities do not replace a valid catalogue', async () => {
    const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false)); vi.mocked(apiService.getClients).mockResolvedValue([client(2), client(2)]);
    await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.clients[0]?.id).toBe(1); expect(view.result.current.error).toContain('подтвердить');
  });
  it('client regression: invalid paged totals do not enter the list', async () => {
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ ...page([client()]), total: -1 }); const view = renderHook(() => useClients({ mode: 'paged' }));
    await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.clients).toEqual([]); expect(view.result.current.total).toBe(0); expect(view.result.current.error).toContain('подтвердить');
  });
  it('client regression: a normal all-mode create updates rows and total without another GET', async () => {
    vi.mocked(apiService.createClient).mockResolvedValue(client(2)); const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    await act(async () => { expect(await view.result.current.createClient(payload)).toBeUndefined(); }); expect(view.result.current.clients.map(row => row.id)).toEqual([2, 1]); expect(view.result.current.total).toBe(2); expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });
  it('client regression: create cancels a pre-acknowledgement read and reloads without losing the new row', async () => {
    const old = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValueOnce(old.promise).mockResolvedValueOnce([client(1), client(2)]); vi.mocked(apiService.createClient).mockResolvedValue(client(2));
    const view = renderHook(() => useClients()); const signal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    await act(async () => { await view.result.current.createClient(payload); }); expect(signal?.aborted).toBe(true);
    await act(async () => old.resolve([client()])); expect(view.result.current.clients.map(row => row.id)).toEqual([1, 2]); expect(view.result.current.loading).toBe(false);
  });
  it('client regression: an acknowledged create refreshes the newest filtered page instead of replaying its old query', async () => {
    const pending = deferred<Client>(); vi.mocked(apiService.createClient).mockReturnValue(pending.promise);
    const view = renderHook(({ search, page: pageNumber }) => useClients({ mode: 'paged', search, page: pageNumber, pageSize: 20, filters: { note: 'a=b&c' } }), { initialProps: { search: 'old', page: 1 } });
    await waitFor(() => expect(view.result.current.loading).toBe(false)); let saving!: Promise<void>; act(() => { saving = view.result.current.createClient(payload); });
    vi.mocked(apiService.getClientsV2).mockResolvedValue(page([client(3)])); view.rerender({ search: 'new', page: 2 }); await waitFor(() => expect(view.result.current.clients[0]?.id).toBe(3));
    await act(async () => { pending.resolve(client(2)); await saving; }); expect(view.result.current.clients.map(row => row.id)).toEqual([3]);
    expect(vi.mocked(apiService.getClientsV2).mock.lastCall?.[0]).toMatchObject({ search: 'new', page: 2, pageSize: 20, note: 'a=b&c' }); expect(apiService.createClient).toHaveBeenCalledTimes(1);
  });
  it.each(['create', 'update', 'delete'] as const)('client regression: a cached %s cannot start after disable', async kind => {
    const view = renderHook(({ enabled }) => useClients({ enabled }), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.loading).toBe(false)); const old = view.result.current;
    view.rerender({ enabled: false }); const operation = kind === 'create' ? old.createClient(payload) : kind === 'update' ? old.updateClient(1, payload) : old.deleteClient(1);
    await expect(operation).rejects.toMatchObject({ name: 'StaleUiOperationError' }); expect(apiService.createClient).not.toHaveBeenCalled(); expect(apiService.updateClient).not.toHaveBeenCalled(); expect(apiService.deleteClient).not.toHaveBeenCalled();
  });
  it('client regression: the latest update receipt wins and its background reload does not block saving', async () => {
    const first = deferred<Client>(); const second = deferred<Client>(); const reload = deferred<Client[]>(); vi.mocked(apiService.updateClient).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false)); vi.mocked(apiService.getClients).mockReturnValue(reload.promise);
    let old!: Promise<unknown>; let latest!: Promise<void>; act(() => { old = view.result.current.updateClient(1, payload).catch(error => error); latest = view.result.current.updateClient(1, payload); });
    await act(async () => { second.resolve(client(1, 'Последний ответ')); await latest; }); expect(view.result.current.clients[0]?.name).toBe('Последний ответ'); expect(view.result.current.refreshing).toBe(true);
    await act(async () => first.reject(new Error('HTTP 403 Old write'))); expect(await old).toMatchObject({ name: 'StaleUiOperationError' }); expect(view.result.current.error).toBeNull();
    await act(async () => reload.resolve([client(1, 'После загрузки')])); expect(view.result.current.clients[0]?.name).toBe('После загрузки'); expect(apiService.getClients).toHaveBeenCalledTimes(2);
  });
  it('client regression: a wrong update identity is rejected without changing any row', async () => {
    const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false)); vi.mocked(apiService.updateClient).mockResolvedValue(client(2));
    await act(async () => { await expect(view.result.current.updateClient(1, payload)).rejects.toThrow('подтвердить'); }); expect(view.result.current.clients[0]?.id).toBe(1); expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });
  it('client regression: failed deletion retains the row without rollback or redundant GET', async () => {
    const pending = deferred<void>(); vi.mocked(apiService.deleteClient).mockReturnValue(pending.promise); const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    let observed!: Promise<unknown>; act(() => { observed = view.result.current.deleteClient(1).catch(error => error); }); expect(view.result.current.clients).toHaveLength(1);
    await act(async () => pending.reject(new Error('Connection unavailable'))); expect(await observed).toBeInstanceOf(Error); expect(view.result.current.clients).toHaveLength(1); expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });
  it('client regression: successful deletion updates current rows and total once', async () => {
    vi.mocked(apiService.deleteClient).mockResolvedValue(undefined); const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    await act(async () => { await view.result.current.deleteClient(1); }); expect(view.result.current.clients).toEqual([]); expect(view.result.current.total).toBe(0); expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });
  it('client regression: old create failure cannot write an error or refresh the next authority', async () => {
    const pending = deferred<Client>(); vi.mocked(apiService.createClient).mockReturnValue(pending.promise); const view = renderHook(({ ownerScope }) => useClients({ ownerScope }), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    let observed!: Promise<unknown>; act(() => { observed = view.result.current.createClient(payload).catch(error => error); }); view.rerender({ ownerScope: 'own' }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    await act(async () => pending.reject(new Error('HTTP 403 Previous login'))); expect(await observed).toMatchObject({ name: 'StaleUiOperationError' }); expect(view.result.current.error).toBeNull(); expect(apiService.getClients).toHaveBeenCalledTimes(2);
  });
  it('client regression: current mutation HTTP 401 clears retained protected rows', async () => {
    vi.mocked(apiService.updateClient).mockRejectedValue(new Error('HTTP 401 Unauthorized')); const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    await act(async () => { await expect(view.result.current.updateClient(1, payload)).rejects.toThrow('401'); }); expect(view.result.current.clients).toEqual([]); expect(view.result.current.total).toBe(0);
  });
  it('client regression: ordinary token refresh does not reload clients or statistics', async () => {
    const view = renderHook(() => ({ clients: useClients(), stats: useClientStats(1) })); await waitFor(() => expect(view.result.current.clients.loading || view.result.current.stats.loading).toBe(false));
    session.refresh(); view.rerender(); await act(async () => {}); expect(apiService.getClients).toHaveBeenCalledTimes(1); expect(apiService.getClientStats).toHaveBeenCalledTimes(1);
  });
  it('client regression: StrictMode replay leaves an active current directory and stats', async () => {
    const view = renderHook(() => ({ clients: useClients(), stats: useClientStats(1) }), { wrapper: ({ children }: PropsWithChildren) => createElement(StrictMode, null, children) });
    await waitFor(() => expect(view.result.current.clients.clients[0]?.id).toBe(1)); await waitFor(() => expect(view.result.current.stats.stats?.clientId).toBe(1)); expect(view.result.current.clients.loading || view.result.current.stats.loading).toBe(false);
  });
  it('client regression: statistics case and role changes abort old reads and discard stale errors', async () => {
    const old = deferred<ClientStats>(); vi.mocked(apiService.getClientStats).mockReturnValueOnce(old.promise); const view = renderHook(({ caseId, role }: { caseId: number; role: 'counterparty' | 'expenseClient' }) => useClientStats(1, caseId, role), { initialProps: { caseId: 2, role: 'counterparty' } });
    const signal = vi.mocked(apiService.getClientStats).mock.calls[0][2]?.signal; view.rerender({ caseId: 3, role: 'expenseClient' }); expect(signal?.aborted).toBe(true);
    await waitFor(() => expect(view.result.current.loading).toBe(false)); await act(async () => old.reject(new Error('HTTP 403 Old case'))); expect(view.result.current.stats?.clientId).toBe(1); expect(view.result.current.error).toBeNull();
    expect(vi.mocked(apiService.getClientStats).mock.lastCall?.[1]).toEqual({ caseId: 3, clientRole: 'expenseClient' });
  });
  it('client regression: statistics rejects another client identity', async () => {
    vi.mocked(apiService.getClientStats).mockResolvedValue(statistics(8)); const view = renderHook(() => useClientStats(1)); await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.stats).toBeNull(); expect(view.result.current.error).toContain('подтвердить');
  });
  it('client regression: current statistics denial clears cache, a network failure preserves it', async () => {
    const view = renderHook(() => useClientStats(1)); await waitFor(() => expect(view.result.current.loading).toBe(false)); vi.mocked(apiService.getClientStats).mockRejectedValueOnce(new Error('Temporary network'));
    await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.stats?.clientId).toBe(1); expect(view.result.current.error).toContain('network');
    vi.mocked(apiService.getClientStats).mockRejectedValueOnce(new Error('HTTP 403 Forbidden')); await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.stats).toBeNull(); expect(view.result.current.error).toContain('403');
  });
  it('client regression: disabled statistics and its cached refresh cannot load data', async () => {
    const view = renderHook(({ enabled }) => useClientStats(1, undefined, undefined, { enabled }), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.loading).toBe(false)); const refresh = view.result.current.refresh;
    view.rerender({ enabled: false }); await act(async () => { await refresh(); }); expect(view.result.current.stats).toBeNull(); expect(view.result.current.loading).toBe(false); expect(apiService.getClientStats).toHaveBeenCalledTimes(1);
  });
  it('client regression: a changed statistics scope or login immediately hides cached values', async () => {
    const view = renderHook(({ ownerScope }) => useClientStats(1, undefined, undefined, { ownerScope }), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    vi.mocked(apiService.getClientStats).mockReturnValue(new Promise(() => {})); view.rerender({ ownerScope: 'own' }); expect(view.result.current.stats).toBeNull();
    session.replace(); view.rerender({ ownerScope: 'own' }); expect(view.result.current.stats).toBeNull(); expect(apiService.getClientStats).toHaveBeenCalledTimes(3);
  });
  it('client regression: the last statistics refresh owns data and loading, unmount aborts', async () => {
    const first = deferred<ClientStats>(); const last = deferred<ClientStats>(); const view = renderHook(() => useClientStats(1)); await waitFor(() => expect(view.result.current.loading).toBe(false));
    vi.mocked(apiService.getClientStats).mockReturnValueOnce(first.promise).mockReturnValueOnce(last.promise); act(() => { void view.result.current.refresh(); void view.result.current.refresh(); });
    await act(async () => first.resolve({ ...statistics(1), totalIncome: 500 })); expect(view.result.current.loading).toBe(true); expect(view.result.current.stats?.totalIncome).toBe(100);
    await act(async () => last.resolve({ ...statistics(1), totalIncome: 200 })); expect(view.result.current.stats?.totalIncome).toBe(200); expect(view.result.current.loading).toBe(false);
    vi.mocked(apiService.getClientStats).mockReturnValue(new Promise(() => {})); act(() => { void view.result.current.refresh(); });
    const signal = vi.mocked(apiService.getClientStats).mock.lastCall?.[2]?.signal; view.unmount(); expect(signal?.aborted).toBe(true);
  });
  it('client boundary: a mutation denial aborts an older read which cannot restore protected rows', async () => {
    const old = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValue(old.promise); vi.mocked(apiService.updateClient).mockRejectedValue(new Error('HTTP 403 Forbidden'));
    const view = renderHook(() => useClients()); const signal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    await act(async () => { await expect(view.result.current.updateClient(1, payload)).rejects.toThrow('403'); }); expect(signal?.aborted).toBe(true);
    await act(async () => old.resolve([client()])); expect(view.result.current.clients).toEqual([]); expect(view.result.current.error).toContain('403'); expect(view.result.current.loading).toBe(false);
  });
  it('client boundary: client callbacks verify a new login even before a React render', async () => {
    const view = renderHook(() => useClients()); await waitFor(() => expect(view.result.current.loading).toBe(false)); session.replace();
    await expect(view.result.current.createClient(payload)).rejects.toMatchObject({ name: 'StaleUiOperationError' }); expect(apiService.createClient).not.toHaveBeenCalled();
    const change = vi.fn(() => [client(4)]); act(() => view.result.current.setClients(change)); expect(change).not.toHaveBeenCalled();
  });
  it('client boundary: a summary without optional nested arrays remains valid on a paged refresh', async () => {
    const view = renderHook(() => useClients({ mode: 'paged' })); await waitFor(() => expect(view.result.current.loading).toBe(false));
    act(() => view.result.current.setClients([{ ...client(), cases: undefined, contracts: undefined }]));
    await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.error).toBeNull(); expect(view.result.current.clients[0]?.id).toBe(1);
  });
  it('client boundary: a disabled empty catalogue keeps its array identity across parent renders', () => {
    const view = renderHook(() => useClients({ enabled: false })); const first = view.result.current.clients; view.rerender(); expect(view.result.current.clients).toBe(first); expect(apiService.getClients).not.toHaveBeenCalled();
  });
  it('client reopen: statistics cannot revive the old client after an absent id lifetime', async () => {
    const view = renderHook(({ id }) => useClientStats(id), { initialProps: { id: 1 } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    view.rerender({ id: 0 }); vi.mocked(apiService.getClientStats).mockReturnValue(new Promise(() => {})); view.rerender({ id: 1 });
    expect(view.result.current.stats).toBeNull(); expect(view.result.current.loading).toBe(true);
  });
});
