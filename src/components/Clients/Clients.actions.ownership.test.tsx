import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ComponentProps } from 'react';
import { Clients } from './Clients';
import { CaseModal } from './CaseModal';
import { ClientModal } from './ClientModal';
import { ClientDetail } from './ClientDetail';
import { FormPromptModal } from '../ui/FormPromptModal';
import { TableContextMenu } from '../ui/TableContextMenu';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { Client, ClientCase, ClientInput } from '../../types';

const ports = vi.hoisted(() => ({ confirm: vi.fn(), error: vi.fn(), success: vi.fn(), caseProps: vi.fn(), clientProps: vi.fn(), detailProps: vi.fn(), menuProps: vi.fn(), promptProps: vi.fn(),
  permissions: { clients: { canView: true, canCreate: true, canEdit: true, canDelete: true, viewScope: 'all' },
    cases: { canView: true, canCreate: true, canEdit: true, canDelete: true, viewScope: 'all' }, contracts: { canView: true }, accounts: { canView: true }, acts: { canView: true }, payments: { canView: true }, tasks: { canView: true, canCreate: true } } }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 1 } } }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ports.permissions }));
vi.mock('../../hooks/useTranslation', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'UTC' }));
vi.mock('../../hooks/useClientStatuses', () => ({ useClientStatuses: () => ({ statuses: [] }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: ports.confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: ports.error, success: ports.success, info: vi.fn() }) }));
vi.mock('./CaseModal', () => ({ CaseModal: (props: ComponentProps<typeof CaseModal>) => { ports.caseProps(props); return props.isOpen ? <div data-testid="case-fixture">{props.caseData?.id}<button onClick={props.onClose}>close-case</button></div> : null; } }));
vi.mock('./ClientModal', () => ({ ClientModal: (props: ComponentProps<typeof ClientModal>) => { ports.clientProps(props); return props.isOpen ? <div data-testid="quick-fixture"><button onClick={props.onClose}>close-client</button></div> : null; } }));
vi.mock('./ClientDetail', () => ({ ClientDetail: (props: ComponentProps<typeof ClientDetail>) => { ports.detailProps(props); return <div data-testid="detail-fixture">{props.clientId}</div>; } }));
vi.mock('../ui/TableContextMenu', () => ({ TableContextMenu: (props: ComponentProps<typeof TableContextMenu>) => { ports.menuProps(props); return null; } }));
vi.mock('../ui/FormPromptModal', () => ({ FormPromptModal: (props: ComponentProps<typeof FormPromptModal>) => { ports.promptProps(props); return props.open ? <div data-testid="prompt-fixture"><button onClick={props.onCancel}>close-prompt</button></div> : null; } }));
vi.mock('../../services/api', () => ({ apiService: { getClientsV2: vi.fn(), getUserSetting: vi.fn(), upsertUserSetting: vi.fn(), createClient: vi.fn(), updateClient: vi.fn(),
  deleteClients: vi.fn(), changeClientsResponsible: vi.fn(), getActResponsibles: vi.fn(), createCase: vi.fn(), updateCase: vi.fn(), deleteCase: vi.fn() } }));
const caseRow = (id: number): ClientCase => ({ id, clientId: 1, title: `Дело ${id}`, description: '', status: 'Open', createdAt: '2026-11-01T00:00:00Z' });
const row = (id = 1): Client => ({ id, name: `Клиент ${id}`, phone: '', email: '', company: '', address: '', notes: '', isActive: true, createdAt: '2026-11-01T00:00:00Z', cases: id === 1 ? [caseRow(11), caseRow(12)] : [], contracts: [] });
const payload: ClientInput = { name: 'Новый клиент', phone: '', email: '', company: '', address: '', notes: '', isActive: true };
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
const caseProps = () => ports.caseProps.mock.lastCall![0] as ComponentProps<typeof CaseModal>;
const clientProps = () => ports.clientProps.mock.lastCall![0] as ComponentProps<typeof ClientModal>;
const detailProps = () => ports.detailProps.mock.lastCall![0] as ComponentProps<typeof ClientDetail>;
const promptProps = () => ports.promptProps.mock.lastCall![0] as ComponentProps<typeof FormPromptModal>;
const menuAction = (key: string) => (ports.menuProps.mock.lastCall![0] as ComponentProps<typeof TableContextMenu>).actions.find(action => action.key === key)!;
async function openCase(index = 0) { fireEvent.click((await screen.findAllByTitle('Редактировать дело'))[index]); await screen.findByTestId('case-fixture'); return caseProps(); }
async function openMenu() { const cell = await screen.findByText('Клиент 1'); fireEvent.contextMenu(cell.closest('tr')!); await waitFor(() => expect(menuAction('delete').disabled).toBe(false)); }
async function openPrompt() { await openMenu(); await act(async () => { await menuAction('changeResponsible').onSelect(); }); await screen.findByTestId('prompt-fixture'); return promptProps(); }

describe('client page action ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); localStorage.removeItem('pp.clients_view');
    Object.assign(ports.permissions.clients, { canView: true, canCreate: true, canEdit: true, canDelete: true, viewScope: 'all' }); Object.assign(ports.permissions.cases, { canView: true, canCreate: true, canEdit: true, canDelete: true, viewScope: 'all' });
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [row(1), row(2)], total: 2, page: 1, pageSize: 25 }); vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'test', value: '' }); vi.mocked(apiService.upsertUserSetting).mockResolvedValue({ key: 'test', value: '' });
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([{ id: 7, fullName: 'Сотрудник' }]); vi.mocked(apiService.updateCase).mockResolvedValue(caseRow(11)); vi.mocked(apiService.createClient).mockResolvedValue(row(3));
    vi.mocked(apiService.createCase).mockResolvedValue(caseRow(21)); vi.mocked(apiService.deleteCase).mockResolvedValue(undefined); vi.mocked(apiService.deleteClients).mockResolvedValue({ deleted: 1 }); vi.mocked(apiService.changeClientsResponsible).mockResolvedValue({ updated: 1, responsibleUserId: 7 }); ports.confirm.mockResolvedValue(true); });
  afterEach(() => { session.restore(); localStorage.removeItem('pp.clients_view'); });
  it('client action baseline: a role scope change closes the previous selected client', async () => {
    const view = render(<Clients />); await screen.findByText('Клиент 1'); act(() => window.dispatchEvent(new CustomEvent('open-client-detail', { detail: { clientId: 1 } }))); await screen.findByTestId('detail-fixture');
    ports.permissions.clients.viewScope = 'own'; view.rerender(<Clients />); expect(screen.queryByTestId('detail-fixture')).toBeNull();
  });
  it('client action baseline: a role change closes an old case window', async () => {
    const view = render(<Clients />); await openCase(); ports.permissions.cases.viewScope = 'own'; view.rerender(<Clients />); await screen.findByText('Клиент 1'); expect(screen.queryByTestId('case-fixture')).toBeNull();
  });
  it('client action baseline: a late case save cannot close a newly opened case', async () => {
    const pending = deferred<ClientCase>(); vi.mocked(apiService.updateCase).mockReturnValue(pending.promise); render(<Clients />); const old = await openCase(); let observed!: Promise<unknown>;
    act(() => { observed = Promise.resolve(old.onSave({ title: 'Старое сохранение' })).catch(error => error); }); fireEvent.click(screen.getByText('close-case')); await openCase(1);
    await act(async () => pending.resolve(caseRow(11))); await observed; expect(screen.getByTestId('case-fixture').textContent).toContain('12');
  });
  it('client action baseline: a cached closed-case save cannot dispatch a write', async () => {
    render(<Clients />); const old = await openCase(); fireEvent.click(screen.getByText('close-case')); await act(async () => { await Promise.resolve(old.onSave({ title: 'Закрыто' })).catch(() => {}); }); expect(apiService.updateCase).not.toHaveBeenCalled();
  });
  it('client action baseline: cached quick-create cannot dispatch after closing', async () => {
    render(<Clients />); fireEvent.click(await screen.findByRole('button', { name: 'addClient' })); await screen.findByTestId('quick-fixture'); const old = clientProps(); fireEvent.click(screen.getByText('close-client'));
    await act(async () => { await old.onSubmit(payload).catch(() => {}); }); expect(apiService.createClient).not.toHaveBeenCalled();
  });
  it('client action baseline: an old detail back callback cannot close another client', async () => {
    render(<Clients />); await screen.findByText('Клиент 1'); act(() => window.dispatchEvent(new CustomEvent('open-client-detail', { detail: { clientId: 1 } }))); const old = detailProps();
    act(() => window.dispatchEvent(new CustomEvent('open-client-detail', { detail: { clientId: 2 } }))); act(() => old.onBack()); expect(screen.getByTestId('detail-fixture').textContent).toBe('2');
  });
  it('client action baseline: a bulk confirmation cannot dispatch after permission scope changes', async () => {
    localStorage.setItem('pp.clients_view', 'table'); const confirm = deferred<boolean>(); ports.confirm.mockReturnValue(confirm.promise); const view = render(<Clients />); await openMenu(); act(() => { void menuAction('delete').onSelect(); });
    ports.permissions.clients.viewScope = 'own'; view.rerender(<Clients />); await act(async () => confirm.resolve(true)); expect(apiService.deleteClients).not.toHaveBeenCalled();
  });
  it('client action baseline: duplicate bulk delete does not open two confirmations', async () => {
    localStorage.setItem('pp.clients_view', 'table'); ports.confirm.mockReturnValue(new Promise(() => {})); render(<Clients />); await openMenu(); const remove = menuAction('delete').onSelect;
    act(() => { void remove(); void remove(); }); expect(ports.confirm).toHaveBeenCalledTimes(1);
  });
  it('client action baseline: a late delete error cannot notify another role', async () => {
    localStorage.setItem('pp.clients_view', 'table'); const pending = deferred<{ deleted: number }>(); vi.mocked(apiService.deleteClients).mockReturnValue(pending.promise); const view = render(<Clients />); await openMenu(); act(() => { void menuAction('delete').onSelect(); }); await waitFor(() => expect(apiService.deleteClients).toHaveBeenCalledOnce());
    ports.permissions.clients.viewScope = 'own'; view.rerender(<Clients />); await act(async () => pending.reject(new Error('HTTP 403 Прежний доступ'))); expect(ports.error).not.toHaveBeenCalled();
  });
  it('client action regression: a normal case update retains its payload and updates only the matching cached case', async () => {
    vi.mocked(apiService.updateCase).mockResolvedValue({ ...caseRow(11), title: 'Сохранённое дело', feeAmount: 12.125 }); render(<Clients />); const props = await openCase();
    const original = { ...props.caseData, description: 'Описание при открытии' };
    await act(async () => { await props.onSave({ title: 'Сохранённое дело', feeAmount: 12.125, clearCourtClaimAmount: true }, original); });
    expect(apiService.updateCase).toHaveBeenCalledWith(11, expect.objectContaining({ clientId: 1, title: 'Сохранённое дело', feeAmount: 12.125, clearCourtClaimAmount: true, description: original.description }), expect.objectContaining({ id: 11, description: original.description })); expect(apiService.updateCase).toHaveBeenCalledOnce();
    expect(screen.queryByTestId('case-fixture')).toBeNull(); expect(screen.getByText('Сохранённое дело')).toBeTruthy(); expect(screen.getByText('Дело 12')).toBeTruthy(); expect(apiService.getClientsV2).toHaveBeenCalledOnce();
  });
  it('client action regression: create and delete cases preserve their client and do not issue redundant list reads', async () => {
    render(<Clients />); fireEvent.click((await screen.findAllByTitle('Добавить дело'))[0]); await screen.findByTestId('case-fixture'); await act(async () => { await caseProps().onSave({ title: 'Дело 21' }); }); expect(apiService.createCase).toHaveBeenCalledWith(expect.objectContaining({ clientId: 1 }));
    await screen.findByText('Дело 21'); await openCase(0); const props = caseProps(); await act(async () => { await props.onDelete?.(); }); expect(apiService.deleteCase).toHaveBeenCalledWith(21); expect(screen.queryByText('Дело 21')).toBeNull(); expect(screen.getByText('Дело 11')).toBeTruthy(); expect(apiService.getClientsV2).toHaveBeenCalledOnce();
  });
  it.each(['id', 'client'] as const)('client action regression: a case receipt for another %s cannot replace data or close the window', async kind => {
    vi.mocked(apiService.updateCase).mockResolvedValue({ ...caseRow(kind === 'id' ? 12 : 11), clientId: kind === 'client' ? 2 : 1, title: 'Чужой ответ' }); render(<Clients />); const props = await openCase();
    await act(async () => { await expect(props.onSave({ title: 'Правка' })).rejects.toThrow('подтвердить'); }); expect(screen.getByTestId('case-fixture').textContent).toContain('11'); expect(screen.queryByText('Чужой ответ')).toBeNull();
  });
  it('client action regression: case save and delete cannot overlap in one window', async () => {
    const pending = deferred<ClientCase>(); vi.mocked(apiService.updateCase).mockReturnValue(pending.promise); render(<Clients />); const props = await openCase(); let saved!: Promise<void>; act(() => { saved = Promise.resolve(props.onSave({ title: 'Правка' })); });
    await act(async () => { await expect(Promise.resolve(props.onDelete?.())).rejects.toThrow('выполняется'); }); expect(apiService.deleteCase).not.toHaveBeenCalled(); await act(async () => { pending.resolve(caseRow(11)); await saved; });
  });
  it('client action regression: navigating into a detail closes the hidden case and rejects its old writer', async () => {
    render(<Clients />); const old = await openCase(); act(() => window.dispatchEvent(new CustomEvent('open-client-detail', { detail: { clientId: 2 } }))); await screen.findByTestId('detail-fixture');
    await act(async () => { await expect(old.onSave({ title: 'Старый контекст' })).rejects.toMatchObject({ name: 'StaleUiOperationError' }); }); expect(apiService.updateCase).not.toHaveBeenCalled(); act(() => detailProps().onBack()); await screen.findByText('Клиент 1'); expect(screen.queryByTestId('case-fixture')).toBeNull();
  });
  it('client action regression: current directory denial closes private windows and does not revive them while retrying', async () => {
    render(<Clients />); await openCase(); vi.mocked(apiService.getClientsV2).mockRejectedValueOnce(new Error('HTTP 403 Forbidden'));
    await act(async () => { await menuAction('refresh').onSelect(); }); expect(screen.queryByTestId('case-fixture')).toBeNull(); const pending = deferred<Awaited<ReturnType<typeof apiService.getClientsV2>>>(); vi.mocked(apiService.getClientsV2).mockReturnValueOnce(pending.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить список' })); expect(screen.queryByTestId('case-fixture')).toBeNull(); await act(async () => pending.resolve({ items: [row()], total: 1, page: 1, pageSize: 25 })); expect(screen.queryByTestId('case-fixture')).toBeNull(); await screen.findByText('Клиент 1');
  });
  it('client action regression: a new login invalidates cached case writes before any React render', async () => {
    render(<Clients />); const props = await openCase(); session.replace('actor'); await act(async () => { await expect(props.onSave({ title: 'Старый вход' })).rejects.toMatchObject({ name: 'StaleUiOperationError' }); }); expect(apiService.updateCase).not.toHaveBeenCalled();
  });
  it('client action regression: ordinary token refresh preserves the current case and list request count', async () => {
    const view = render(<Clients />); await openCase(); session.refresh(); view.rerender(<Clients />); expect(screen.getByTestId('case-fixture')).toBeTruthy(); expect(apiService.getClientsV2).toHaveBeenCalledOnce();
  });
  it.each(['view', 'create', 'edit', 'delete'] as const)('client action regression: case %s permission is required in addition to client admission', async kind => {
    if (kind === 'view') ports.permissions.cases.canView = false;
    if (kind === 'create') ports.permissions.cases.canCreate = false;
    if (kind === 'edit') ports.permissions.cases.canEdit = false;
    if (kind === 'delete') ports.permissions.cases.canDelete = false;
    render(<Clients />); await screen.findByText('Клиент 1');
    if (kind === 'view') { expect(screen.queryByText('Дело 11')).toBeNull(); expect(screen.queryByText('Дело 12')).toBeNull(); }
    if (kind === 'create') expect(screen.queryAllByTitle('Добавить дело')).toHaveLength(0);
    if (kind === 'edit') expect(screen.queryAllByTitle('Редактировать дело')).toHaveLength(0);
    if (kind === 'delete') { await openCase(); expect(caseProps().onDelete).toBeUndefined(); }
  });
  it('client action regression: former client container restrictions remain required for case editing', async () => {
    ports.permissions.clients.canEdit = false; ports.permissions.clients.canCreate = false; render(<Clients />); await screen.findByText('Клиент 1'); expect(screen.queryAllByTitle('Добавить дело')).toHaveLength(0); expect(screen.queryAllByTitle('Редактировать дело')).toHaveLength(0);
  });
  it('client action regression: an old detail update cannot patch a different selected client', async () => {
    render(<Clients />); await screen.findByText('Клиент 1'); act(() => window.dispatchEvent(new CustomEvent('open-client-detail', { detail: { clientId: 1 } }))); const old = detailProps();
    act(() => window.dispatchEvent(new CustomEvent('open-client-detail', { detail: { clientId: 2 } }))); act(() => old.onClientUpdated?.({ ...row(2), name: 'Чужая карточка' })); act(() => detailProps().onBack()); await screen.findByText('Клиент 2'); expect(screen.queryByText('Чужая карточка')).toBeNull();
  });
  it('client action regression: a normal bulk delete sends the confirmed ids once and refreshes the current list', async () => {
    localStorage.setItem('pp.clients_view', 'table'); render(<Clients />); await openMenu(); vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [row(2)], total: 1, page: 1, pageSize: 25 }); act(() => { void menuAction('delete').onSelect(); });
    await waitFor(() => expect(ports.success).toHaveBeenCalledWith('Клиент удален.', 'Удаление выполнено')); expect(apiService.deleteClients).toHaveBeenCalledWith([1]); expect(apiService.deleteClients).toHaveBeenCalledOnce(); await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(2));
  });
  it.each(['selection', 'query', 'login'] as const)('client action regression: pending delete confirmation is discarded after %s changes', async kind => {
    localStorage.setItem('pp.clients_view', 'table'); const pending = deferred<boolean>(); ports.confirm.mockReturnValue(pending.promise); render(<Clients />); await openMenu(); act(() => { void menuAction('delete').onSelect(); });
    if (kind === 'selection') fireEvent.contextMenu(screen.getByText('Клиент 2').closest('tr')!);
    if (kind === 'query') fireEvent.change(screen.getByPlaceholderText('search'), { target: { value: 'Другой запрос' } });
    if (kind === 'login') session.replace();
    await act(async () => pending.resolve(true)); expect(apiService.deleteClients).not.toHaveBeenCalled(); expect(ports.success).not.toHaveBeenCalled();
  });
  it('client action regression: stale responsible catalogue cannot open a prompt for another selection', async () => {
    localStorage.setItem('pp.clients_view', 'table'); const pending = deferred<{ id: number; fullName: string }[]>(); vi.mocked(apiService.getActResponsibles).mockReturnValue(pending.promise); render(<Clients />); await openMenu(); act(() => { void menuAction('changeResponsible').onSelect(); });
    fireEvent.contextMenu(screen.getByText('Клиент 2').closest('tr')!); await act(async () => pending.resolve([{ id: 7, fullName: 'Старый сотрудник' }])); expect(screen.queryByTestId('prompt-fixture')).toBeNull(); expect(ports.error).not.toHaveBeenCalled(); expect(vi.mocked(apiService.getActResponsibles).mock.calls[0][0]?.signal?.aborted).toBe(true);
  });
  it('client action regression: responsible assignment sends the captured ids and employee once', async () => {
    localStorage.setItem('pp.clients_view', 'table'); render(<Clients />); const props = await openPrompt(); await act(async () => { await props.onConfirm(); });
    expect(apiService.changeClientsResponsible).toHaveBeenCalledWith([1], 7); expect(apiService.changeClientsResponsible).toHaveBeenCalledOnce(); expect(screen.queryByTestId('prompt-fixture')).toBeNull(); expect(ports.success).toHaveBeenCalledWith('Ответственный обновлен.', 'Изменения сохранены');
  });
  it('client action regression: assignment confirmation cannot follow a selection change or retarget its prompt', async () => {
    localStorage.setItem('pp.clients_view', 'table'); render(<Clients />); const props = await openPrompt(); const pending = deferred<boolean>(); ports.confirm.mockReturnValue(pending.promise); act(() => { void props.onConfirm(); });
    fireEvent.contextMenu(screen.getByText('Клиент 2').closest('tr')!); await act(async () => pending.resolve(true)); expect(apiService.changeClientsResponsible).not.toHaveBeenCalled(); expect(screen.queryByTestId('prompt-fixture')).toBeNull();
  });
  it('client action regression: a late assignment receipt cannot close a replacement prompt', async () => {
    localStorage.setItem('pp.clients_view', 'table'); const pending = deferred<{ updated: number; responsibleUserId: number }>(); vi.mocked(apiService.changeClientsResponsible).mockReturnValue(pending.promise); render(<Clients />); const old = await openPrompt(); let completed!: Promise<void>; act(() => { completed = Promise.resolve(old.onConfirm()); }); await waitFor(() => expect(apiService.changeClientsResponsible).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByText('close-prompt')); await openPrompt(); await act(async () => { pending.resolve({ updated: 1, responsibleUserId: 7 }); await completed; }); expect(screen.getByTestId('prompt-fixture')).toBeTruthy(); expect(ports.success).not.toHaveBeenCalled(); expect(apiService.getActResponsibles).toHaveBeenCalledOnce();
  });
  it('client action regression: current assignment failure stays in its own prompt and clears for a new employee choice', async () => {
    localStorage.setItem('pp.clients_view', 'table'); vi.mocked(apiService.changeClientsResponsible).mockRejectedValue(new Error('Не удалось назначить')); render(<Clients />); const props = await openPrompt(); await act(async () => { await props.onConfirm(); }); expect(promptProps().error).toContain('Не удалось назначить');
    act(() => promptProps().fields[0].onChange('8')); expect(promptProps().error).toBeUndefined(); expect(screen.getByTestId('prompt-fixture')).toBeTruthy();
  });
  it('client action regression: an empty or failed responsible catalogue reports only in its current scope', async () => {
    localStorage.setItem('pp.clients_view', 'table'); vi.mocked(apiService.getActResponsibles).mockResolvedValue([]); render(<Clients />); await openMenu(); await act(async () => { await menuAction('changeResponsible').onSelect(); }); expect(ports.error).toHaveBeenCalledWith('Список ответственных пуст.', 'Смена ответственного'); expect(screen.queryByTestId('prompt-fixture')).toBeNull();
  });
  it('client action boundary: a malformed unsaved case in the server list cannot open an edit writer', async () => {
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [{ ...row(), cases: [caseRow(0)] }], total: 1, page: 1, pageSize: 25 }); render(<Clients />);
    fireEvent.click(await screen.findByTitle('Редактировать дело')); expect(screen.queryByTestId('case-fixture')).toBeNull(); expect(apiService.updateCase).not.toHaveBeenCalled(); expect(apiService.createCase).not.toHaveBeenCalled();
  });
  it('client action regression: a new case retains its draft source through saving and failure until the window is reopened', async () => {
    vi.mocked(apiService.createCase).mockRejectedValue(new Error('Временная ошибка'));
    render(<Clients />); fireEvent.click((await screen.findAllByTitle('Добавить дело'))[0]); await screen.findByTestId('case-fixture');
    const draft = caseProps().caseData;
    await act(async () => { await expect(caseProps().onSave({ title: 'Несохранённый ввод' })).rejects.toThrow('Временная ошибка'); });
    expect(caseProps().caseData).toBe(draft); expect(screen.getByTestId('case-fixture')).toBeTruthy(); expect(apiService.createCase).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByText('close-case')); fireEvent.click(screen.getAllByTitle('Добавить дело')[0]); await screen.findByTestId('case-fixture');
    expect(caseProps().caseData).not.toBe(draft); expect(caseProps().caseData?.clientId).toBe(1);
  });
});
