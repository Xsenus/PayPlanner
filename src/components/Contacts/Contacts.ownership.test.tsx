import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Contacts } from './Contacts';
import { ContactEditorModal } from './ContactEditorModal';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
const dialogs = vi.hoisted(() => ({ confirm: vi.fn() }));
const state = vi.hoisted(() => ({ user: { id: 7, role: { id: 4 } }, admin: false, permissions: { clients: { canView: true, canCreate: true, canEdit: true, canDelete: true, viewScope: 'all' }, legalEntities: { canView: true, canEdit: true, canCreate: true, viewScope: 'all' } } }));
const isAdmin = () => state.admin;
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: state.user, isAdmin }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => state.permissions }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => dialogs }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: vi.fn(), success: vi.fn(), warning: vi.fn(), info: vi.fn() }) }));
vi.mock('../../hooks/usePersistedTableSettings', () => ({ usePersistedTableSettings: () => ({ columnOrder: ['contact'], orderedVisibleColumns: ['contact'], visibleColumns: new Set(['contact']), pageSize: 20, setPageSize: vi.fn(), toggleColumn: vi.fn(), moveColumn: vi.fn(), handleColumnDragStart: vi.fn(), handleColumnDragOver: vi.fn(), handleColumnDrop: vi.fn(), handleColumnDragEnd: vi.fn(), handleResizeStart: vi.fn(), getColumnWidth: () => 320, getTableWidth: () => 1120, resetTableSettings: vi.fn() }) }));
vi.mock('./ContactEditorModal', () => ({ ContactEditorModal: vi.fn(() => null) }));
vi.mock('./YealinkImportPreviewModal', () => ({ YealinkImportPreviewModal: () => null }));
vi.mock('../../services/api', () => ({ apiService: { getContacts: vi.fn(), getLegalEntities: vi.fn(), getActResponsibles: vi.fn(), deleteExternalContact: vi.fn() } }));
const contact = (id: number, fullName: string): ContactDirectoryItemDto => ({ sourceType: 'external', category: 'external', sourceId: id, contactType: 'Person', roles: ['Contact'], hasFullProfile: false, fullName, phones: [], emails: [], isActive: true, syncToPhonebook: false });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((finish, fail) => { resolve = finish; reject = fail; }); return { promise, resolve, reject }; }
describe('Contacts current read owner', () => {
 beforeEach(() => { vi.resetAllMocks(); localStorage.clear(); sessionStorage.clear(); state.user = { id: 7, role: { id: 4 } }; state.admin = false; state.permissions.clients = { canView: true, canCreate: true, canEdit: true, canDelete: true, viewScope: 'all' }; state.permissions.legalEntities = { canView: true, canEdit: true, canCreate: true, viewScope: 'all' }; vi.mocked(apiService.getContacts).mockResolvedValue([]); vi.mocked(apiService.getLegalEntities).mockResolvedValue([]); vi.mocked(apiService.getActResponsibles).mockResolvedValue([]); });
 it('contacts baseline: role change hides old rows and starts a fresh directory read', async () => {
  const next = deferred<ContactDirectoryItemDto[]>();
  vi.mocked(apiService.getContacts).mockResolvedValueOnce([contact(1, 'Прежний набор')]).mockReturnValueOnce(next.promise);
  const view = render(<Contacts />); await screen.findByText('Прежний набор');
  state.user = { ...state.user, role: { id: 42 } }; view.rerender(<Contacts />);
  expect(screen.queryByText('Прежний набор')).toBeNull();
  await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(2));
  await act(async () => next.resolve([contact(2, 'Текущий набор')])); await screen.findByText('Текущий набор');
 });
 it('contacts baseline: old admin catalogues never enter a restricted editor after a late response', async () => {
  const entities = deferred<Awaited<ReturnType<typeof apiService.getLegalEntities>>>(); const responsibles = deferred<Awaited<ReturnType<typeof apiService.getActResponsibles>>>();
  state.admin = true; vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(entities.promise); vi.mocked(apiService.getActResponsibles).mockReturnValueOnce(responsibles.promise);
  const view = render(<Contacts />); await waitFor(() => expect(apiService.getActResponsibles).toHaveBeenCalledTimes(1));
  const entitySignal = vi.mocked(apiService.getLegalEntities).mock.calls[0][1]?.signal;
  const responsibleSignal = vi.mocked(apiService.getActResponsibles).mock.calls[0][0]?.signal;
  state.admin = false; state.user = { ...state.user, role: { id: 43 } }; state.permissions.legalEntities.canView = false; view.rerender(<Contacts />);
  await act(async () => { entities.resolve([{ id: 301, shortName: 'Прежняя компания', fullName: 'Прежняя компания', clients: [], clientsCount: 0, createdAt: '2026-11-01T00:00:00Z' }]); responsibles.resolve([{ id: 9, fullName: 'Прежний ответственный' }]); });
  const props = vi.mocked(ContactEditorModal).mock.lastCall![0]; expect(props.legalEntities).toEqual([]); expect(props.responsibles).toEqual([]);
  expect(entitySignal?.aborted).toBe(true); expect(responsibleSignal?.aborted).toBe(true);
 });
 it('contacts baseline: legal entity permission disables its lookup while client contacts remain readable', async () => {
  state.permissions.legalEntities.canView = false; render(<Contacts />);
  await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(1)); expect(apiService.getLegalEntities).not.toHaveBeenCalled();
 });
 it('contacts baseline: current permissions change the editor scope without changing the role ID', async () => {
  const view = render(<Contacts />); const scope = vi.mocked(ContactEditorModal).mock.lastCall![0].ownerScope;
  state.permissions.clients.viewScope = 'own'; view.rerender(<Contacts />);
  expect(vi.mocked(ContactEditorModal).mock.lastCall![0].ownerScope).not.toBe(scope);
 });
 it('contacts ownership: denying both directory permissions hides rows and closes the editor without another read', async () => {
  vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'Скрываемая запись')]);
  const view = render(<Contacts />); await screen.findByText('Скрываемая запись');
  fireEvent.click(screen.getByRole('button', { name: 'Редактировать Скрываемая запись' }));
  expect(vi.mocked(ContactEditorModal).mock.lastCall![0].open).toBe(true);
  state.permissions.clients.canView = false; state.permissions.legalEntities.canView = false;
  view.rerender(<Contacts />);
  expect(screen.getByText('Нет доступа к контактам.')).toBeTruthy();
  expect(screen.queryByText('Скрываемая запись')).toBeNull();
  expect(apiService.getContacts).toHaveBeenCalledTimes(1);
 });
 it('contacts ownership: token refresh and ordinary parent renders keep one read and the existing editor contact', async () => {
  const session = documentCopyTestSession();
  try {
   vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'Текущий контакт')]);
   const view = render(<Contacts />); await screen.findByText('Текущий контакт');
   fireEvent.click(screen.getByRole('button', { name: 'Редактировать Текущий контакт' }));
   const previous = vi.mocked(ContactEditorModal).mock.lastCall![0]; session.refresh(); view.rerender(<Contacts />);
   const current = vi.mocked(ContactEditorModal).mock.lastCall![0];
   expect(current.open).toBe(true); expect(current.contact).toBe(previous.contact); expect(current.ownerScope).toBe(previous.ownerScope);
   expect(apiService.getContacts).toHaveBeenCalledTimes(1); expect(apiService.getLegalEntities).toHaveBeenCalledTimes(1);
  } finally { session.restore(); }
 });
 it('contacts ownership: revoked create and edit permissions disable create and hide edit without denying directory access', async () => {
  state.permissions.clients.canCreate = false; state.permissions.clients.canEdit = false;
  vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'Читаемый контакт')]);
  render(<Contacts />); await screen.findByText('Читаемый контакт');
  expect(screen.getByRole('button', { name: /Добавить контакт/ }).matches(":disabled")).toBe(true);
  expect(screen.queryByRole('button', { name: 'Редактировать Читаемый контакт' })).toBeNull();
 });
 it('contacts ownership: obsolete delete confirmation never dispatches a mutation', async () => {
  const confirmation = deferred<boolean>(); dialogs.confirm.mockReturnValueOnce(confirmation.promise);
  vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'Удаляемый контакт')]);
  const view = render(<Contacts />); await screen.findByText('Удаляемый контакт');
  fireEvent.click(screen.getByRole('button', { name: 'Удалить Удаляемый контакт' }));
  state.user = { id: 8, role: { id: 4 } }; view.rerender(<Contacts />);
  await act(async () => confirmation.resolve(true)); expect(apiService.deleteExternalContact).not.toHaveBeenCalled();
 });
 it('contacts ownership: a late delete cannot remove the next owners row with the same ID', async () => {
  const deletion = deferred<void>(); dialogs.confirm.mockResolvedValueOnce(true);
  vi.mocked(apiService.deleteExternalContact).mockReturnValueOnce(deletion.promise);
  vi.mocked(apiService.getContacts).mockResolvedValueOnce([contact(1, 'Старый контакт')]).mockResolvedValueOnce([contact(1, 'Новый контакт')]);
  const view = render(<Contacts />); await screen.findByText('Старый контакт'); fireEvent.click(screen.getByRole('button', { name: 'Удалить Старый контакт' }));
  await waitFor(() => expect(apiService.deleteExternalContact).toHaveBeenCalledTimes(1));
  state.user = { id: 8, role: { id: 4 } }; view.rerender(<Contacts />); await screen.findByText('Новый контакт');
  await act(async () => deletion.resolve()); expect(screen.getByText('Новый контакт')).toBeTruthy();
 });
 it('contacts denial: current 403 clears loaded rows and closes the existing editor', async () => {
  const response = deferred<ContactDirectoryItemDto[]>();
  vi.mocked(apiService.getContacts).mockResolvedValueOnce([contact(1, 'Отозванный контакт')]).mockReturnValueOnce(response.promise);
  const view = render(<Contacts />); await screen.findByText('Отозванный контакт');
  fireEvent.click(screen.getByRole('button', { name: 'Редактировать Отозванный контакт' }));
  const p = vi.mocked(ContactEditorModal).mock.lastCall![0]; act(() => p.onSaved(contact(1, 'Ответ сохранения')));
  await act(async () => response.reject(new Error('HTTP 403 - Доступ закрыт')));
  expect(screen.queryByText('Отозванный контакт')).toBeNull(); expect(vi.mocked(ContactEditorModal).mock.lastCall![0].open).toBe(false);
  expect(screen.getByText('Недостаточно прав для выполнения действия.')).toBeTruthy(); view.unmount();
 });
 it('contacts late failure: a previous role cannot replace the current directory with its error', async () => {
  const previous = deferred<ContactDirectoryItemDto[]>();
  vi.mocked(apiService.getContacts).mockReturnValueOnce(previous.promise).mockResolvedValueOnce([contact(2, 'Доступная запись')]);
  const view = render(<Contacts />); await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(1));
  const signal = vi.mocked(apiService.getContacts).mock.calls[0][1]?.signal;
  state.user = { id: 7, role: { id: 42 } }; view.rerender(<Contacts />); await screen.findByText('Доступная запись');
  await act(async () => previous.reject(new Error('Ошибка прежнего каталога')));
  expect(signal?.aborted).toBe(true); expect(screen.queryByText('Ошибка прежнего каталога')).toBeNull(); expect(screen.getByText('Доступная запись')).toBeTruthy();
 });
});
