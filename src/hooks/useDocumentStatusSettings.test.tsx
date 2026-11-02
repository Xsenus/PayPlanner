import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDocumentStatusSettings } from './useDocumentStatusSettings';
import { apiService } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';
import type { DocumentStatusEntity } from '../types';
vi.mock('../services/api', () => ({ apiService: { getDocumentStatuses: vi.fn() } }));
const row = (id = 1): DocumentStatusEntity => ({ id, code: 'Draft', name: `Статус ${id}`, description: '', colorHex: '#ffffff', isActive: true,
  useForInvoices: true, useForActs: true, useForContracts: true, sortOrder: 1, createdAt: '2026-11-02' });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getDocumentStatuses).mockResolvedValue([row()]); });
afterEach(() => session.restore());
describe('document status snapshot ownership', () => {
  it('baseline: refuses the previous login response even before context rerenders', async () => {
    const pending = deferred<DocumentStatusEntity[]>(); vi.mocked(apiService.getDocumentStatuses).mockReturnValueOnce(pending.promise);
    const view = renderHook(() => useDocumentStatusSettings()); await waitFor(() => expect(apiService.getDocumentStatuses).toHaveBeenCalledOnce());
    session.replace(); await act(async () => pending.resolve([row(9)])); expect(view.result.current).toBeNull();
  });
  it.each(['document', 'role', 'disabled', 'login', 'actor', 'logout', 'unmount'].flatMap(change => ['success', 'error'].map(outcome => ({ change, outcome }))))(
    'refuses late $outcome after $change', async ({ change, outcome }) => {
      const pending = deferred<DocumentStatusEntity[]>(); vi.mocked(apiService.getDocumentStatuses).mockReturnValueOnce(pending.promise);
      const view = renderHook((p: { enabled: boolean; ownerScope: string }) => useDocumentStatusSettings(p), { initialProps: { enabled: true, ownerScope: 'document-1-role-4' } });
      await waitFor(() => expect(apiService.getDocumentStatuses).toHaveBeenCalledOnce()); const signal = vi.mocked(apiService.getDocumentStatuses).mock.calls[0][0]?.signal;
      if (change === 'login' || change === 'actor' || change === 'logout') session.replace(change);
      if (change === 'unmount') view.unmount(); else view.rerender({ enabled: change !== 'disabled', ownerScope: ['document', 'role'].includes(change) ? 'document-2-role-8' : 'document-1-role-4' });
      await act(async () => { if (outcome === 'error') pending.reject(new Error('HTTP 403 прежнего статуса')); else pending.resolve([row(999)]); });
      if (change !== 'unmount') expect(view.result.current?.some(x => x.id === 999) ?? false).toBe(false);
      expect(signal?.aborted).toBe(true);
    });
  it('keeps disabled statuses lazy, then verifies the exact signal and does not reload on token refresh', async () => {
    const view = renderHook(({ enabled }) => useDocumentStatusSettings({ enabled, ownerScope: 'role-4' }), { initialProps: { enabled: false } });
    act(() => window.dispatchEvent(new Event('dicts:changed'))); expect(apiService.getDocumentStatuses).not.toHaveBeenCalled(); expect(view.result.current).toBeNull();
    view.rerender({ enabled: true }); await waitFor(() => expect(view.result.current?.[0]?.id).toBe(1));
    expect(apiService.getDocumentStatuses).toHaveBeenCalledExactlyOnceWith({ signal: expect.any(AbortSignal) });
    session.refresh(); view.rerender({ enabled: true }); await act(async () => {}); expect(apiService.getDocumentStatuses).toHaveBeenCalledOnce();
  });
  it('accepts only the latest dictionary event and returns the existing null fallback after an error', async () => {
    const pending = deferred<DocumentStatusEntity[]>(); vi.mocked(apiService.getDocumentStatuses).mockReturnValueOnce(pending.promise).mockResolvedValue([row(2)]);
    const view = renderHook(() => useDocumentStatusSettings()); act(() => window.dispatchEvent(new Event('dicts:changed')));
    await waitFor(() => expect(view.result.current?.[0]?.id).toBe(2)); await act(async () => pending.resolve([row(999)])); expect(view.result.current?.[0]?.id).toBe(2);
    vi.mocked(apiService.getDocumentStatuses).mockRejectedValueOnce(new Error('HTTP 500'));
    act(() => window.dispatchEvent(new Event('dicts:changed'))); await waitFor(() => expect(view.result.current).toBeNull());
  });
  it.each([null, {}, [row(), row()], [{ ...row(), id: -1 }], [{ ...row(), name: {} }], [{ ...row(), code: [] }], [{ ...row(), code: ' ' }],
    [{ ...row(), useForInvoices: 'false' }], [{ ...row(), isActive: 'true' }], [{ ...row(), sortOrder: Infinity }]])('uses default labels for an unconfirmed response %j', async data => {
    vi.mocked(apiService.getDocumentStatuses).mockResolvedValueOnce(data as DocumentStatusEntity[]); const view = renderHook(() => useDocumentStatusSettings());
    await waitFor(() => expect(apiService.getDocumentStatuses).toHaveBeenCalledOnce()); await act(async () => {}); expect(view.result.current).toBeNull();
  });
  it('preserves inactive status flags and order so the existing business label rules still decide visibility', async () => {
    const rows = [{ ...row(2), isActive: false, sortOrder: -1, useForInvoices: false }, row(1)]; vi.mocked(apiService.getDocumentStatuses).mockResolvedValueOnce(rows);
    const view = renderHook(() => useDocumentStatusSettings()); await waitFor(() => expect(view.result.current).toBe(rows));
  });

  it('keeps logged out statuses lazy', async () => { session.replace('logout'); const view = renderHook(() => useDocumentStatusSettings()); await act(async () => {}); expect(view.result.current).toBeNull(); expect(apiService.getDocumentStatuses).not.toHaveBeenCalled(); });

});
