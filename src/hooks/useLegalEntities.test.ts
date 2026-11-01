import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLegalEntities } from './useLegalEntities';
import { apiService } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';
import type { LegalEntityDetail, LegalEntityInput, LegalEntitySummary } from '../types';

vi.mock('../services/api', () => ({ apiService: { getLegalEntities: vi.fn(), getLegalEntity: vi.fn(), createLegalEntity: vi.fn(), updateLegalEntity: vi.fn(), deleteLegalEntity: vi.fn(), suggestLegalEntities: vi.fn(), findLegalEntityById: vi.fn() } }));
const entity = (id: number, shortName = `Компания ${id}`): LegalEntityDetail => ({ id, shortName, clientsCount: 0, createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T00:00:00Z', clients: [] });
const payload: LegalEntityInput = { shortName: 'Новая компания', clientIds: [] };
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
describe('owned legal entity directory', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getLegalEntities).mockResolvedValue([entity(1)]); });
  afterEach(() => session.restore());
  it('directory baseline: a disabled hook cannot adopt a late response', async () => {
    const old = deferred<LegalEntitySummary[]>(); vi.mocked(apiService.getLegalEntities).mockReturnValue(old.promise);
    const view = renderHook(({ enabled }) => useLegalEntities({ enabled }), { initialProps: { enabled: true } });
    view.rerender({ enabled: false }); await act(async () => old.resolve([entity(1, 'Чужой старый каталог')]));
    expect(view.result.current.legalEntities).toEqual([]); expect(view.result.current.loading).toBe(false);
  });
  it('directory baseline: a changed caller scope clears cached rows before its new response', async () => {
    const next = deferred<LegalEntitySummary[]>(); const view = renderHook((props: { enabled: boolean; ownerScope: string }) => useLegalEntities(props), { initialProps: { enabled: true, ownerScope: 'all' } });
    await waitFor(() => expect(view.result.current.legalEntities).toHaveLength(1)); vi.mocked(apiService.getLegalEntities).mockReturnValue(next.promise);
    view.rerender({ enabled: true, ownerScope: 'own' }); expect(view.result.current.legalEntities).toEqual([]);
  });
  it('directory baseline: the newest search owns the list and loading flag', async () => {
    const old = deferred<LegalEntitySummary[]>(); const next = deferred<LegalEntitySummary[]>();
    vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise); const view = renderHook(() => useLegalEntities());
    act(() => { void view.result.current.refresh('new'); }); await act(async () => old.resolve([entity(1)]));
    expect(view.result.current.loading).toBe(true); expect(view.result.current.legalEntities).toEqual([]);
    await act(async () => next.resolve([entity(2)])); expect(view.result.current.legalEntities.map(row => row.id)).toEqual([2]);
  });
  it('directory baseline: session replacement clears existing cached rows', async () => {
    const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.legalEntities).toHaveLength(1));
    vi.mocked(apiService.getLegalEntities).mockReturnValue(new Promise(() => {})); session.replace(); view.rerender(); expect(view.result.current.legalEntities).toEqual([]);
  });
  it('directory baseline: a cached callback cannot start a write after disable', async () => {
    vi.mocked(apiService.createLegalEntity).mockResolvedValue(entity(2)); const view = renderHook(({ enabled }) => useLegalEntities({ enabled }), { initialProps: { enabled: true } });
    const create = view.result.current.createLegalEntity; view.rerender({ enabled: false }); await expect(create(payload)).rejects.toThrow(); expect(apiService.createLegalEntity).not.toHaveBeenCalled();
  });
  it('directory baseline: an old update receipt cannot be adopted by the next scope', async () => {
    const old = deferred<LegalEntityDetail>(); vi.mocked(apiService.updateLegalEntity).mockReturnValue(old.promise);
    const view = renderHook((props: { enabled: boolean; ownerScope: string }) => useLegalEntities(props), { initialProps: { enabled: true, ownerScope: 'all' } });
    let pending!: Promise<LegalEntityDetail>; act(() => { pending = view.result.current.updateLegalEntity(1, payload); });
    const observed = pending.then(value => ({ value, error: null }), error => ({ value: null, error }));
    view.rerender({ enabled: true, ownerScope: 'own' }); await act(async () => old.resolve(entity(1, 'Старое сохранение')));
    expect((await observed).error).toBeInstanceOf(Error);
    expect(view.result.current.legalEntities.every(row => row.shortName !== 'Старое сохранение')).toBe(true);
  });
  it('directory regression: CRUD updates the current cached list without redundant reads', async () => {
    vi.mocked(apiService.createLegalEntity).mockResolvedValue(entity(2)); vi.mocked(apiService.updateLegalEntity).mockResolvedValue(entity(1, 'Изменённая')); vi.mocked(apiService.deleteLegalEntity).mockResolvedValue(undefined);
    const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    await act(async () => { await view.result.current.createLegalEntity(payload); }); expect(view.result.current.legalEntities.map(row => row.id).sort()).toEqual([1, 2]);
    await act(async () => { await view.result.current.updateLegalEntity(1, payload); }); expect(view.result.current.legalEntities.find(row => row.id === 1)?.shortName).toBe('Изменённая');
    await act(async () => { await view.result.current.deleteLegalEntity(2); }); expect(view.result.current.legalEntities.map(row => row.id)).toEqual([1]); expect(apiService.getLegalEntities).toHaveBeenCalledOnce();
  });
  it('directory regression: a pending pre-create list cannot drop the new company', async () => {
    const old = deferred<LegalEntitySummary[]>(); vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(old.promise).mockResolvedValueOnce([entity(1), entity(2)]); vi.mocked(apiService.createLegalEntity).mockResolvedValue(entity(2));
    const view = renderHook(() => useLegalEntities()); await act(async () => { await view.result.current.createLegalEntity(payload); });
    await waitFor(() => expect(view.result.current.legalEntities).toHaveLength(2)); await act(async () => old.resolve([entity(1)])); expect(view.result.current.legalEntities).toHaveLength(2); expect(apiService.getLegalEntities).toHaveBeenCalledTimes(2);
  });
  it('directory regression: write refresh preserves the current search', async () => {
    vi.mocked(apiService.createLegalEntity).mockResolvedValue(entity(2)); const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    await act(async () => view.result.current.refresh('current')); await act(async () => { await view.result.current.createLegalEntity(payload); });
    expect(apiService.getLegalEntities).toHaveBeenLastCalledWith({ search: 'current' }, expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });
  it.each(['create', 'update', 'delete', 'detail', 'suggest', 'find'] as const)('directory regression: pending %s cannot return to another login', async kind => {
    const pending = deferred<LegalEntityDetail>(); const nothing = deferred<void>(); const suggestions = deferred<[]>(); const details = deferred<{ shortName: string }>();
    vi.mocked(apiService.createLegalEntity).mockReturnValue(pending.promise); vi.mocked(apiService.updateLegalEntity).mockReturnValue(pending.promise); vi.mocked(apiService.deleteLegalEntity).mockReturnValue(nothing.promise);
    vi.mocked(apiService.getLegalEntity).mockReturnValue(pending.promise); vi.mocked(apiService.suggestLegalEntities).mockReturnValue(suggestions.promise); vi.mocked(apiService.findLegalEntityById).mockReturnValue(details.promise);
    const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const operation = kind === 'create' ? view.result.current.createLegalEntity(payload) : kind === 'update' ? view.result.current.updateLegalEntity(1, payload) : kind === 'delete' ? view.result.current.deleteLegalEntity(1) : kind === 'detail' ? view.result.current.getLegalEntity(1) : kind === 'suggest' ? view.result.current.suggestLegalEntities({ query: 'test' }) : view.result.current.findLegalEntityById({ query: 'test' });
    const observed = operation.then(() => null, error => error); session.replace();
    await act(async () => { pending.resolve(entity(1)); nothing.resolve(); suggestions.resolve([]); details.resolve({ shortName: 'Details' }); });
    expect((await observed)?.name).toBe('StaleUiOperationError');
  });
  it.each(['create', 'update', 'delete', 'detail', 'suggest', 'find'] as const)('directory regression: cached %s cannot start after a scope change', async kind => {
    const view = renderHook((props: { ownerScope: string }) => useLegalEntities(props), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const old = view.result.current; view.rerender({ ownerScope: 'own' });
    const action = () => kind === 'create' ? old.createLegalEntity(payload) : kind === 'update' ? old.updateLegalEntity(1, payload) : kind === 'delete' ? old.deleteLegalEntity(1) : kind === 'detail' ? old.getLegalEntity(1) : kind === 'suggest' ? old.suggestLegalEntities({ query: 'test' }) : old.findLegalEntityById({ query: 'test' });
    await expect(action()).rejects.toMatchObject({ name: 'StaleUiOperationError' });
    for (const method of [apiService.createLegalEntity, apiService.updateLegalEntity, apiService.deleteLegalEntity, apiService.getLegalEntity, apiService.suggestLegalEntities, apiService.findLegalEntityById]) expect(method).not.toHaveBeenCalled();
  });
  it.each(['success', 'error'] as const)('directory regression: an older update %s cannot replace a newer one', async outcome => {
    const old = deferred<LegalEntityDetail>(); vi.mocked(apiService.updateLegalEntity).mockReturnValueOnce(old.promise).mockResolvedValueOnce(entity(1, 'Последняя запись'));
    const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    const earlier = view.result.current.updateLegalEntity(1, payload).then(() => null, error => error);
    await act(async () => { await view.result.current.updateLegalEntity(1, payload); });
    await act(async () => { if (outcome === 'success') old.resolve(entity(1, 'Старая запись')); else old.reject(new Error('Old write error')); });
    expect((await earlier)?.name).toBe('StaleUiOperationError'); expect(view.result.current.legalEntities[0].shortName).toBe('Последняя запись');
  });
  it.each(['duplicate', 'wrong-id'] as const)('directory regression: malformed %s data is not adopted', async kind => {
    if (kind === 'duplicate') vi.mocked(apiService.getLegalEntities).mockResolvedValueOnce([entity(1), entity(1)]); else vi.mocked(apiService.getLegalEntity).mockResolvedValue(entity(2));
    const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    if (kind === 'duplicate') { expect(view.result.current.legalEntities).toEqual([]); expect(view.result.current.error).toContain('подтвердить'); }
    else await expect(view.result.current.getLegalEntity(1)).rejects.toThrow('подтвердить');
  });
  it('directory regression: unchanged inline options and token refresh do not refetch', async () => {
    const view = renderHook(() => useLegalEntities({ ownerScope: 'same' })); await waitFor(() => expect(view.result.current.loading).toBe(false)); session.refresh(); view.rerender();
    expect(view.result.current.legalEntities).toHaveLength(1); expect(apiService.getLegalEntities).toHaveBeenCalledOnce();
  });
  it('directory regression: a late error does not replace the next scope or its loading state', async () => {
    const old = deferred<LegalEntitySummary[]>(); const next = deferred<LegalEntitySummary[]>(); vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
    const view = renderHook((props: { ownerScope: string }) => useLegalEntities(props), { initialProps: { ownerScope: 'all' } }); view.rerender({ ownerScope: 'own' });
    await act(async () => old.reject(new Error('Old list failure'))); expect(view.result.current.error).toBeNull(); expect(view.result.current.loading).toBe(true);
    await act(async () => next.resolve([entity(2)])); expect(view.result.current.legalEntities.map(row => row.id)).toEqual([2]);
  });
  it.each([401, 403])('directory denied: HTTP %s clears the currently cached directory', async status => {
    const view = renderHook(() => useLegalEntities()); await waitFor(() => expect(view.result.current.legalEntities).toHaveLength(1));
    vi.mocked(apiService.getLegalEntities).mockRejectedValueOnce(new Error(`HTTP ${status} Denied`)); await act(async () => view.result.current.refresh());
    expect(view.result.current.legalEntities).toEqual([]); expect(view.result.current.loading).toBe(false); expect(view.result.current.error).toContain(String(status));
  });
  it('directory denied: a stale 403 cannot clear the new authorized scope', async () => {
    const old = deferred<LegalEntitySummary[]>(); vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(old.promise).mockResolvedValueOnce([entity(2)]);
    const view = renderHook((props: { ownerScope: string }) => useLegalEntities(props), { initialProps: { ownerScope: 'all' } }); view.rerender({ ownerScope: 'own' });
    await waitFor(() => expect(view.result.current.legalEntities.map(row => row.id)).toEqual([2])); await act(async () => old.reject(new Error('HTTP 403 Old denial')));
    expect(view.result.current.legalEntities.map(row => row.id)).toEqual([2]); expect(view.result.current.error).toBeNull();
  });
});
