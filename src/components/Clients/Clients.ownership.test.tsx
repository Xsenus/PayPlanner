import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Clients } from './Clients';
import { apiService } from '../../services/api';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { ClientModal } from './ClientModal';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { Client } from '../../types';

const permissions = vi.hoisted(() => ({ clients: { canView: true, canCreate: false, canEdit: false, canDelete: false, viewScope: 'all' },
  cases: { canView: true, viewScope: 'all' }, contracts: { canView: true, viewScope: 'all' }, accounts: { canView: true, viewScope: 'all' }, acts: { canView: true, viewScope: 'all' }, payments: { canView: true, viewScope: 'all' } }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 1 } } }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => permissions }));
vi.mock('../../hooks/useTranslation', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'UTC' }));
vi.mock('../../hooks/useClientStatuses', () => ({ useClientStatuses: vi.fn(() => ({ statuses: [] })) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: vi.fn(), success: vi.fn(), info: vi.fn() }) }));
vi.mock('./ClientModal', () => ({ ClientModal: vi.fn(() => null) }));
vi.mock('./ClientDetail', () => ({ ClientDetail: () => null }));
vi.mock('./CaseModal', () => ({ CaseModal: () => null }));
vi.mock('../../services/api', () => ({ apiService: { getClientsV2: vi.fn(), getUserSetting: vi.fn(), upsertUserSetting: vi.fn() } }));
const row: Client = { id: 1, name: 'Разрешённый клиент', phone: '', email: '', company: '', address: '', notes: '', isActive: true, createdAt: '2026-11-01T00:00:00Z', cases: [], contracts: [] };

describe('client list parent ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.clearAllMocks(); session = documentCopyTestSession(); permissions.clients.canView = true; permissions.clients.canCreate = false; permissions.clients.viewScope = 'all'; permissions.cases.viewScope = 'all';
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [row], total: 1, page: 1, pageSize: 25 }); vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'synthetic-client-setting', value: '' }); });
  afterEach(() => session.restore());
  it('requirements parent: client page passes current permission scope to its quick editor', async () => {
    const view = render(<Clients />); await screen.findByText('Разрешённый клиент'); const first = vi.mocked(ClientModal).mock.lastCall?.[0].ownerScope; expect(first).toBeTruthy();
    permissions.clients.viewScope = 'own'; view.rerender(<Clients />); await waitFor(() => expect(vi.mocked(ClientModal).mock.lastCall?.[0].ownerScope).not.toBe(first));
  });
  it('quick client parent: direct child requests receive the editor lifetime invalidated by close and permissions', async () => {
    permissions.clients.canCreate = true; const view = render(<Clients />); await screen.findByText('Разрешённый клиент'); fireEvent.click(screen.getByRole('button', { name: 'addClient' }));
    const first = vi.mocked(ClientModal).mock.lastCall![0]; expect(first.isOpen).toBe(true); const captured = first.editorOwner!.capture()!; expect(captured.isCurrent()).toBe(true);
    act(() => first.onClose()); expect(captured.isCurrent()).toBe(false); expect(first.editorOwner!.capture()).toBeNull(); fireEvent.click(screen.getByRole('button', { name: 'addClient' }));
    const second = vi.mocked(ClientModal).mock.lastCall![0]; expect(second.editorOwner!.scope).not.toBe(first.editorOwner!.scope); const next = second.editorOwner!.capture()!;
    permissions.clients.viewScope = 'own'; view.rerender(<Clients />); expect(next.isCurrent()).toBe(false);
  });
  it('dictionary caller: client list scopes its statuses and disables them without view access', async () => {
    const view = render(<Clients />); await screen.findByText('Разрешённый клиент'); const first = vi.mocked(useClientStatuses).mock.lastCall?.[0]; expect(first?.enabled).toBe(true);
    permissions.clients.viewScope = 'own'; view.rerender(<Clients />); expect(vi.mocked(useClientStatuses).mock.lastCall?.[0]?.ownerScope).not.toBe(first?.ownerScope);
    permissions.clients.canView = false; view.rerender(<Clients />); expect(vi.mocked(useClientStatuses).mock.lastCall?.[0]?.enabled).toBe(false);
  });
  it('client caller: list discards protected client details when nested case access changes', async () => {
    const view = render(<Clients />); await screen.findByText('Разрешённый клиент'); vi.mocked(apiService.getClientsV2).mockReturnValue(new Promise(() => {}));
    permissions.cases.viewScope = 'own'; view.rerender(<Clients />); expect(screen.queryByText('Разрешённый клиент')).toBeNull(); await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(2));
  });
  it('client caller: denied list performs no client GET and clears displayed rows on revocation', async () => {
    permissions.clients.canView = false; const view = render(<Clients />); await act(async () => {}); expect(apiService.getClientsV2).not.toHaveBeenCalled(); expect(screen.getByText('Доступ к клиентам ограничен')).toBeTruthy();
    permissions.clients.canView = true; view.rerender(<Clients />); await screen.findByText('Разрешённый клиент');
    permissions.clients.canView = false; view.rerender(<Clients />); expect(screen.queryByText('Разрешённый клиент')).toBeNull(); expect(apiService.getClientsV2).toHaveBeenCalledTimes(1);
  });
});
