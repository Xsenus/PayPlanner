import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useContracts } from './useContracts';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { documentCopyTestSession } from '../test/documentCopySession';
import { apiService } from '../services/api';
import { StaleUiOperationError } from '../utils/StaleUiOperationError';
import type { Contract, ContractInput } from '../types';

vi.mock('../services/api', () => ({ apiService: { getContracts: vi.fn(), createContract: vi.fn(), updateContract: vi.fn(), deleteContract: vi.fn() } }));
const payload: ContractInput = { number: 'ДОГ-1', date: '2026-11-02', billingMode: 'Fixed', isOpenEnded: false, amount: 1000, clients: [{ clientId: 1 }], actIds: [31], invoiceIds: [101], paymentIds: [501] };
const row = (id = 91): Contract => ({ ...payload, clients: [{ id: 1, name: 'Клиент договора' }], id, createdAt: '2026-11-02T00:00:00Z', invoiceAmountTotal: 0, actAmountTotal: 0, paymentAmountTotal: 0 } as Contract);
const page = (id = 91) => ({ items: [row(id)], total: 1, page: 1, pageSize: 20 });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type Props = { context: string; clientId: number; enabled: boolean };
const defaults: Props = { context: 'client-1-role-4', clientId: 1, enabled: true };
const setup = () => renderHook((props: Props) => { const owner = useDocumentDeliveryOwner(props.context); return useContracts({ clientId: props.clientId, enabled: props.enabled, page: 1, pageSize: 20 }, owner); }, { initialProps: defaults });
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getContracts).mockResolvedValue(page());
  vi.mocked(apiService.createContract).mockResolvedValue(row(92)); vi.mocked(apiService.updateContract).mockResolvedValue(row()); vi.mocked(apiService.deleteContract).mockResolvedValue(undefined); });
afterEach(() => session.restore());

describe('owned contract reads and mutations', () => {
  it.each(['client', 'role', 'disabled', 'login', 'actor', 'logout', 'unmount'].flatMap(change => ['success', 'error'].map(outcome => ({ change, outcome }))))(
    'refuses a late read $outcome after $change and its former refresh callback', async ({ change, outcome }) => {
      const pending = deferred<ReturnType<typeof page>>(); vi.mocked(apiService.getContracts).mockReturnValueOnce(pending.promise);
      const view = setup(); await waitFor(() => expect(apiService.getContracts).toHaveBeenCalledOnce()); const old = view.result.current;
      const signal = vi.mocked(apiService.getContracts).mock.calls[0][1]?.signal;
      if (change === 'login' || change === 'actor' || change === 'logout') session.replace(change);
      if (change === 'unmount') view.unmount(); else if (change !== 'login' && change !== 'actor' && change !== 'logout') view.rerender({ ...defaults,
        ...(change === 'client' ? { context: 'client-2', clientId: 2 } : {}), ...(change === 'role' ? { context: 'role-8' } : {}), ...(change === 'disabled' ? { enabled: false, context: 'disabled' } : {}) });
      const count = vi.mocked(apiService.getContracts).mock.calls.length;
      await act(async () => { await old.refresh(); if (outcome === 'error') pending.reject(new Error('Прежний список договоров')); else pending.resolve(page(999)); });
      expect(apiService.getContracts).toHaveBeenCalledTimes(count);
      if (!['login', 'actor', 'logout'].includes(change)) expect(signal?.aborted).toBe(true);
      if (change !== 'unmount') { expect(view.result.current.contracts.map(x => x.id)).not.toContain(999); expect(view.result.current.error).not.toBe('Прежний список договоров'); }
    });
  it('preserves a current token refresh and settles errors without an extra full-list request', async () => {
    const view = setup(); await waitFor(() => expect(view.result.current.contracts[0]?.id).toBe(91)); session.refresh(); view.rerender({ ...defaults });
    expect(apiService.getContracts).toHaveBeenCalledOnce(); vi.mocked(apiService.getContracts).mockRejectedValueOnce(new Error('Список недоступен'));
    await act(async () => view.result.current.refresh()); expect(view.result.current.error).toContain('Список недоступен'); expect(view.result.current.loading || view.result.current.refreshing).toBe(false);
  });
  it('keeps the owned list lazy without read access', async () => {
    const view = renderHook(() => { const owner = useDocumentDeliveryOwner('disabled'); return useContracts({ enabled: false }, owner); });
    await act(async () => view.result.current.refresh()); expect(apiService.getContracts).not.toHaveBeenCalled(); expect(view.result.current.contracts).toEqual([]); expect(view.result.current.loading).toBe(false);
  });
  for (const kind of ['create', 'update', 'delete'] as const) {
    const method = kind === 'create' ? 'createContract' : kind === 'update' ? 'updateContract' : 'deleteContract';
    const mutate = (value: ReturnType<typeof useContracts>, current?: () => boolean) => kind === 'create' ? value.createContract(payload, current) : kind === 'update' ? value.updateContract(91, payload, current) : value.deleteContract(91, current);
    it(`${kind} refuses dispatch when the caller was already invalidated`, async () => {
      const view = setup(); await waitFor(() => expect(view.result.current.loading).toBe(false)); const count = vi.mocked(apiService.getContracts).mock.calls.length;
      await act(async () => { await expect(mutate(view.result.current, () => false)).rejects.toBeInstanceOf(StaleUiOperationError); });
      expect(apiService[method]).not.toHaveBeenCalled(); expect(apiService.getContracts).toHaveBeenCalledTimes(count);
    });
    it.each(['success', 'error'])(`${kind} rejects stale write %s without refreshing`, async outcome => {
      const view = setup(); await waitFor(() => expect(view.result.current.loading).toBe(false)); const count = vi.mocked(apiService.getContracts).mock.calls.length;
      const pending = deferred<Contract>(); vi.mocked(apiService[method]).mockReturnValueOnce(pending.promise as never); let current = true; let result!: Promise<unknown>;
      act(() => { result = mutate(view.result.current, () => current).catch(error => error); }); current = false;
      await act(async () => { if (outcome === 'error') pending.reject(new Error('Прежняя запись')); else pending.resolve(row()); expect(await result).toBeInstanceOf(StaleUiOperationError); });
      expect(apiService[method]).toHaveBeenCalledOnce(); expect(apiService.getContracts).toHaveBeenCalledTimes(count);
    });
    it(`${kind} preserves the current payload, performs one fresh bounded read and retains the returned record`, async () => {
      const view = setup(); await waitFor(() => expect(view.result.current.loading).toBe(false)); let result: unknown;
      await act(async () => { result = await mutate(view.result.current, () => true); });
      expect(apiService[method]).toHaveBeenCalledExactlyOnceWith(...(kind === 'create' ? [payload] : kind === 'update' ? [91, payload] : [91]));
      expect(apiService.getContracts).toHaveBeenCalledTimes(2); expect(vi.mocked(apiService.getContracts).mock.lastCall?.[0]).toMatchObject({ clientId: 1, page: 1, pageSize: 20 });
      if (kind !== 'delete') expect(result).toEqual(row(kind === 'create' ? 92 : 91));
    });
    it(`${kind} does not let an old query reread after the current editor write`, async () => {
      const view = setup(); await waitFor(() => expect(view.result.current.loading).toBe(false)); const old = view.result.current;
      const pending = deferred<Contract>(); vi.mocked(apiService[method]).mockReturnValueOnce(pending.promise as never); let result!: Promise<unknown>;
      act(() => { result = mutate(old, () => true); }); view.rerender({ ...defaults, context: 'new-filter' });
      await waitFor(() => expect(apiService.getContracts).toHaveBeenCalledTimes(2)); await act(async () => { pending.resolve(row()); await result; });
      expect(apiService.getContracts).toHaveBeenCalledTimes(2);
    });
    it(`${kind} stops adopting a refresh result after its caller becomes stale`, async () => {
      const view = setup(); await waitFor(() => expect(view.result.current.loading).toBe(false)); const pending = deferred<ReturnType<typeof page>>();
      vi.mocked(apiService.getContracts).mockReturnValueOnce(pending.promise); let current = true; let result!: Promise<unknown>;
      act(() => { result = mutate(view.result.current, () => current).catch(error => error); }); await waitFor(() => expect(apiService.getContracts).toHaveBeenCalledTimes(2)); current = false;
      await act(async () => { pending.resolve(page(999)); expect(await result).toBeInstanceOf(StaleUiOperationError); });
      expect(view.result.current.contracts.map(x => x.id)).not.toContain(999); expect(view.result.current.refreshing).toBe(false);
    });
    it(`${kind} leaves existing callers without an optional guard working`, async () => {
      const view = renderHook(() => useContracts({ clientId: 1, page: 1, pageSize: 20 })); await waitFor(() => expect(view.result.current.loading).toBe(false));
      await act(async () => { await mutate(view.result.current); }); expect(apiService[method]).toHaveBeenCalledOnce(); expect(apiService.getContracts).toHaveBeenCalledTimes(2);
    });
  }
});
