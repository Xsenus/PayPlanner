import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Contacts } from './Contacts';
import { YealinkImportPreviewModal } from './YealinkImportPreviewModal';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import type { YealinkContactImportResultDto } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
const dialogs = vi.hoisted(() => ({ confirm: vi.fn() }));
const state = vi.hoisted(() => ({ user: { id: 7, role: { id: 4 } }, admin: false, permissions: { clients: { canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true, viewScope: 'all' }, legalEntities: { canView: true, canEdit: true, canCreate: true, viewScope: 'all' } } }));
const isAdmin = () => state.admin;
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: state.user, isAdmin }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => state.permissions }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => dialogs }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: vi.fn(), success: vi.fn(), warning: vi.fn(), info: vi.fn() }) }));
vi.mock('../../hooks/usePersistedTableSettings', () => ({ usePersistedTableSettings: () => ({ columnOrder: ['contact'], orderedVisibleColumns: ['contact'], visibleColumns: new Set(['contact']), pageSize: 20, setPageSize: vi.fn(), toggleColumn: vi.fn(), moveColumn: vi.fn(), handleColumnDragStart: vi.fn(), handleColumnDragOver: vi.fn(), handleColumnDrop: vi.fn(), handleColumnDragEnd: vi.fn(), handleResizeStart: vi.fn(), getColumnWidth: () => 320, getTableWidth: () => 1120, resetTableSettings: vi.fn() }) }));
vi.mock('./ContactEditorModal', () => ({ ContactEditorModal: vi.fn(() => null) }));
vi.mock('./YealinkImportPreviewModal', () => ({ YealinkImportPreviewModal: vi.fn(() => <div role="dialog" aria-label="Проверка импорта" />) }));
vi.mock('./ContactMergeModal', () => ({ ContactMergeModal: () => <div role="dialog" aria-label="Объединение" /> }));
vi.mock('../../services/api', () => ({ apiService: { getContacts: vi.fn(), getLegalEntities: vi.fn(), getActResponsibles: vi.fn(), deleteExternalContact: vi.fn(), promoteExternalContact: vi.fn(), downloadYealinkContactsXml: vi.fn(), importYealinkContactsXml: vi.fn() } }));
const contact = (id: number, fullName: string): ContactDirectoryItemDto => ({ sourceType: 'external', category: 'external', sourceId: id, contactType: 'Person', roles: ['Contact'], hasFullProfile: false, fullName, phones: [], emails: [], isActive: true, syncToPhonebook: false });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((finish, fail) => { resolve = finish; reject = fail; }); return { promise, resolve, reject }; }

const preview: YealinkContactImportResultDto = { dryRun: true, scanned: 1, withPhones: 1, created: 1, updated: 0, skippedExisting: 0, skippedNoPhone: 0, skippedDuplicateInFile: 0, skippedByUser: 0, skippedInvalid: 0, details: [{ rowId: 1, name: 'Импортируемый контакт', phones: ['+70000000000'], action: 'WouldCreateExternal', matchedPhones: [] }] };
const file = new File(['<phonebook/>'], 'safe.xml', { type: 'application/xml' });
function pickFile(view: ReturnType<typeof render>) {
 fireEvent.click(screen.getByRole('button', { name: 'Импорт XML' }));
 fireEvent.change(view.container.querySelector('input[type=file]')!, { target: { files: [file] } });
}
describe('Contacts direct action owner', () => {
 beforeEach(() => {
  vi.resetAllMocks(); localStorage.clear(); sessionStorage.clear();
  state.user = { id: 7, role: { id: 4 } }; state.admin = true;
  state.permissions.clients = { canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true, viewScope: 'all' };
  state.permissions.legalEntities = { canView: true, canEdit: true, canCreate: true, viewScope: 'all' };
  vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'Исходный контакт')]);
  vi.mocked(apiService.getLegalEntities).mockResolvedValue([]); vi.mocked(apiService.getActResponsibles).mockResolvedValue([]);
  vi.mocked(apiService.importYealinkContactsXml).mockResolvedValue(preview);
 });
 it('contact actions baseline: a late dry-run response cannot open the previous employees import preview', async () => {
  const pending = deferred<YealinkContactImportResultDto>(); vi.mocked(apiService.importYealinkContactsXml).mockReturnValueOnce(pending.promise);
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view);
  state.user = { id: 8, role: { id: 4 } }; view.rerender(<Contacts />);
  await act(async () => pending.resolve(preview)); expect(screen.queryByRole('dialog', { name: 'Проверка импорта' })).toBeNull();
 });
 it('contact actions baseline: import confirmation from the previous role never posts the real import', async () => {
  const confirmation = deferred<boolean>(); dialogs.confirm.mockReturnValueOnce(confirmation.promise);
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view);
  await waitFor(() => expect(vi.mocked(YealinkImportPreviewModal).mock.lastCall).toBeTruthy());
  act(() => vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].onConfirm());
  state.user = { id: 7, role: { id: 42 } }; view.rerender(<Contacts />);
  expect(dialogs.confirm.mock.calls[0][0].signal.aborted).toBe(true);
  await act(async () => confirmation.resolve(true)); expect(apiService.importYealinkContactsXml).toHaveBeenCalledTimes(1);
 });
 it('contact actions baseline: revoking administrator rights hides merge and import previews synchronously', async () => {
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view);
  await screen.findByRole('dialog', { name: 'Проверка импорта' }); fireEvent.click(screen.getByRole('button', { name: 'Объединить дубли' }));
  expect(screen.getByRole('dialog', { name: 'Объединение' })).toBeTruthy(); state.admin = false; view.rerender(<Contacts />);
  expect(screen.queryByRole('dialog', { name: 'Объединение' })).toBeNull(); expect(screen.queryByRole('dialog', { name: 'Проверка импорта' })).toBeNull();
 });
 it('contact actions baseline: a late XML response never creates a download under another employee', async () => {
  const create = vi.spyOn(URL, 'createObjectURL'); const pending = deferred<{ blob: Blob; fileName: string }>(); vi.mocked(apiService.downloadYealinkContactsXml).mockReturnValueOnce(pending.promise);
  try {
   const view = render(<Contacts />); await screen.findByText('Исходный контакт'); fireEvent.click(screen.getByRole('button', { name: 'Скачать Yealink XML' }));
   state.user = { id: 8, role: { id: 4 } }; view.rerender(<Contacts />);
   await act(async () => pending.resolve({ blob: new Blob(['old']), fileName: 'old.xml' })); expect(create).not.toHaveBeenCalled();
  } finally { create.mockRestore(); }
 });
 it('contact actions baseline: obsolete promotion confirmation never creates a full profile', async () => {
  const confirmation = deferred<boolean>(); dialogs.confirm.mockReturnValueOnce(confirmation.promise);
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); fireEvent.click(screen.getByRole('button', { name: 'Оформить' }));
  state.permissions.clients.canCreate = false; state.admin = false; view.rerender(<Contacts />);
  await act(async () => confirmation.resolve(true)); expect(apiService.promoteExternalContact).not.toHaveBeenCalled();
 });
 it('contact actions current import: confirmation is single-flight and uses the frozen selected decisions', async () => {
  const confirmation = deferred<boolean>(); dialogs.confirm.mockReturnValueOnce(confirmation.promise);
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view);
  await screen.findByRole('dialog', { name: 'Проверка импорта' });
  const first = vi.mocked(YealinkImportPreviewModal).mock.lastCall![0];
  act(() => first.onChangeDecision(1, 'createClient'));
  const current = vi.mocked(YealinkImportPreviewModal).mock.lastCall![0];
  act(() => { first.onConfirm(); current.onConfirm(); });
  expect(dialogs.confirm).toHaveBeenCalledTimes(1);
  act(() => { first.onChangeDecision(1, 'skip'); first.onClose(); });
  expect(vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].decisions).toEqual({ 1: 'createClient' });
  await act(async () => confirmation.resolve(true));
  expect(apiService.importYealinkContactsXml).toHaveBeenCalledTimes(2);
  expect(vi.mocked(apiService.importYealinkContactsXml).mock.calls[1]).toEqual([file, { decisions: [{ rowId: 1, action: 'createClient' }] }]);
  expect(screen.queryByRole('dialog', { name: 'Проверка импорта' })).toBeNull();
 });
 it('contact actions stale import: dispatched result cannot reload or close another owners preview', async () => {
  const result = deferred<YealinkContactImportResultDto>(); dialogs.confirm.mockResolvedValueOnce(true);
  vi.mocked(apiService.importYealinkContactsXml).mockResolvedValueOnce(preview).mockReturnValueOnce(result.promise).mockResolvedValueOnce(preview);
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view);
  await screen.findByRole('dialog', { name: 'Проверка импорта' }); act(() => vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].onConfirm());
  await waitFor(() => expect(apiService.importYealinkContactsXml).toHaveBeenCalledTimes(2));
  state.user = { id: 8, role: { id: 4 } }; view.rerender(<Contacts />); await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(2));
  pickFile(view); await screen.findByRole('dialog', { name: 'Проверка импорта' });
  await act(async () => result.resolve({ ...preview, dryRun: false }));
  expect(apiService.getContacts).toHaveBeenCalledTimes(2); expect(screen.getByRole('dialog', { name: 'Проверка импорта' })).toBeTruthy();
 });
 it('contact actions current import: cancellation and failure preserve the preview for an explicit retry', async () => {
  dialogs.confirm.mockResolvedValueOnce(false).mockResolvedValue(true);
  vi.mocked(apiService.importYealinkContactsXml).mockResolvedValueOnce(preview).mockRejectedValueOnce(new Error('Временный отказ импорта')).mockResolvedValueOnce({ ...preview, dryRun: false });
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view); await screen.findByRole('dialog', { name: 'Проверка импорта' });
  await act(async () => vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].onConfirm());
  expect(apiService.importYealinkContactsXml).toHaveBeenCalledTimes(1); expect(screen.getByRole('dialog', { name: 'Проверка импорта' })).toBeTruthy();
  await act(async () => vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].onConfirm());
  expect(screen.getByRole('dialog', { name: 'Проверка импорта' })).toBeTruthy(); expect(vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].importing).toBe(false);
  await act(async () => vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].onConfirm());
  expect(apiService.importYealinkContactsXml).toHaveBeenCalledTimes(3); expect(screen.queryByRole('dialog', { name: 'Проверка импорта' })).toBeNull();
 });
 it('contact actions held import: a closed preview cannot alter or confirm its replacement', async () => {
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view); await screen.findByRole('dialog', { name: 'Проверка импорта' });
  const old = vi.mocked(YealinkImportPreviewModal).mock.lastCall![0]; act(() => old.onClose());
  pickFile(view); await screen.findByRole('dialog', { name: 'Проверка импорта' });
  act(() => { old.onChangeDecision(1, 'createClient'); old.onConfirm(); old.onClose(); });
  expect(dialogs.confirm).not.toHaveBeenCalled(); expect(vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].decisions).toEqual({ 1: 'createExternal' });
  expect(screen.getByRole('dialog', { name: 'Проверка импорта' })).toBeTruthy();
 });
 it('contact actions file chooser: selecting a file from an obsolete chooser never starts a dry run', async () => {
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); fireEvent.click(screen.getByRole('button', { name: 'Импорт XML' }));
  state.user = { id: 8, role: { id: 4 } }; view.rerender(<Contacts />);
  fireEvent.change(view.container.querySelector('input[type=file]')!, { target: { files: [file] } });
  expect(apiService.importYealinkContactsXml).not.toHaveBeenCalled();
 });
 it('contact actions current download: duplicate clicks stay single-flight and revoke the URL after a click failure', async () => {
  const pending = deferred<{ blob: Blob; fileName: string }>(); vi.mocked(apiService.downloadYealinkContactsXml).mockReturnValueOnce(pending.promise);
  const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fixture'); const revoke = vi.spyOn(URL, 'revokeObjectURL');
  const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => { throw new Error('Click failed'); });
  try {
   render(<Contacts />); await screen.findByText('Исходный контакт'); const button = screen.getByRole('button', { name: 'Скачать Yealink XML' }); fireEvent.click(button); fireEvent.click(button);
   expect(apiService.downloadYealinkContactsXml).toHaveBeenCalledTimes(1);
   await act(async () => pending.resolve({ blob: new Blob(['safe']), fileName: 'safe.xml' }));
   expect(revoke).toHaveBeenCalledWith('blob:fixture'); expect(document.querySelector('a[download]')).toBeNull();
   expect(screen.getByRole('button', { name: 'Скачать Yealink XML' }).matches(':disabled')).toBe(false);
  } finally { create.mockRestore(); revoke.mockRestore(); click.mockRestore(); }
 });
 it('contact actions promotion: late result never navigates or reloads under a different role', async () => {
  const pending = deferred<ContactDirectoryItemDto>(); dialogs.confirm.mockResolvedValue(true); vi.mocked(apiService.promoteExternalContact).mockReturnValueOnce(pending.promise);
  const open = vi.fn(); const view = render(<Contacts onOpenClient={open} />); await screen.findByText('Исходный контакт'); fireEvent.click(screen.getByRole('button', { name: 'Оформить' }));
  await waitFor(() => expect(apiService.promoteExternalContact).toHaveBeenCalledTimes(1));
  state.user = { id: 7, role: { id: 42 } }; view.rerender(<Contacts />); await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(2));
  await act(async () => pending.resolve({ ...contact(1, 'Исходный контакт'), linkedClientId: 77 })); expect(open).not.toHaveBeenCalled(); expect(apiService.getContacts).toHaveBeenCalledTimes(2);
 });
 it('contact actions current promotion: current payload and navigation are retained after a successful refresh', async () => {
  dialogs.confirm.mockResolvedValue(true); vi.mocked(apiService.promoteExternalContact).mockResolvedValue({ ...contact(1, 'Исходный контакт'), linkedClientId: 77 });
  const open = vi.fn(); render(<Contacts onOpenClient={open} />); await screen.findByText('Исходный контакт'); fireEvent.click(screen.getByRole('button', { name: 'Оформить' }));
  await waitFor(() => expect(open).toHaveBeenCalledWith(77));
  expect(apiService.promoteExternalContact).toHaveBeenCalledWith(1, { target: 'client', role: 'Client' }); expect(apiService.getContacts).toHaveBeenCalledTimes(2);
 });
 it('contact actions navigation: a replaced login blocks a mounted opening button before parent render', async () => {
  const session = documentCopyTestSession();
  try {
   vi.mocked(apiService.getContacts).mockResolvedValue([{ ...contact(1, 'Исходный контакт'), linkedClientId: 77 }]);
   const open = vi.fn(); render(<Contacts onOpenClient={open} />); await screen.findByText('Исходный контакт');
   session.replace('login'); fireEvent.click(screen.getByRole('button', { name: 'Открыть' })); expect(open).not.toHaveBeenCalled();
  } finally { session.restore(); }
 });
 it('contact actions decisions: existing rows default to skip and impossible or unknown choices are rejected', async () => {
  vi.mocked(apiService.importYealinkContactsXml).mockResolvedValueOnce({ ...preview, details: [
   { ...preview.details[0], action: 'Existing' }, { ...preview.details[0], rowId: 2 },
  ] });
  const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view); await screen.findByRole('dialog', { name: 'Проверка импорта' });
  const current = vi.mocked(YealinkImportPreviewModal).mock.lastCall![0]; expect(current.decisions).toEqual({ 1: 'skip', 2: 'createExternal' });
  act(() => { current.onChangeDecision(1, 'createClient'); current.onChangeDecision(2, 'updateExisting'); current.onChangeDecision(999, 'createExternal'); });
  expect(vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].decisions).toEqual({ 1: 'skip', 2: 'createExternal' });
  act(() => current.onChangeDecision(1, 'updateExisting')); expect(vi.mocked(YealinkImportPreviewModal).mock.lastCall![0].decisions[1]).toBe('updateExisting');
 });
 it('contact actions organization: the legal-entity create permission and supplier target remain independent of client creation', async () => {
  state.admin = false; state.permissions.clients.canCreate = false;
  const organization = { ...contact(1, 'Контрольная организация'), contactType: 'Organization' as const, roles: ['Supplier' as const] };
  vi.mocked(apiService.getContacts).mockResolvedValue([organization]);
  vi.mocked(apiService.promoteExternalContact).mockResolvedValue({ ...organization, linkedLegalEntityId: 301 }); dialogs.confirm.mockResolvedValue(true);
  const open = vi.fn(); render(<Contacts onOpenLegalEntity={open} />); await screen.findByText('Контрольная организация');
  fireEvent.click(screen.getByRole('button', { name: 'Оформить' })); await waitFor(() => expect(open).toHaveBeenCalledWith(301));
  expect(apiService.promoteExternalContact).toHaveBeenCalledWith(1, { target: 'legalEntity', role: 'Supplier' });
 });
 it('contact actions directory denial: a current forbidden read closes action previews and cancels the old export', async () => {
  const pending = deferred<{ blob: Blob; fileName: string }>(); vi.mocked(apiService.downloadYealinkContactsXml).mockReturnValueOnce(pending.promise);
  vi.mocked(apiService.getContacts).mockResolvedValueOnce([contact(1, 'Исходный контакт')]).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт'));
  const create = vi.spyOn(URL, 'createObjectURL');
  try {
   const view = render(<Contacts />); await screen.findByText('Исходный контакт'); pickFile(view); await screen.findByRole('dialog', { name: 'Проверка импорта' });
   fireEvent.click(screen.getByRole('button', { name: 'Объединить дубли' })); fireEvent.click(screen.getByRole('button', { name: 'Скачать Yealink XML' }));
   fireEvent.change(screen.getByRole('searchbox', { name: 'Поиск' }), { target: { value: 'отказ' } });
   await screen.findByText('Недостаточно прав для выполнения действия.');
   expect(screen.queryByRole('dialog')).toBeNull(); expect(vi.mocked(apiService.downloadYealinkContactsXml).mock.calls[0][0]?.signal?.aborted).toBe(true);
   await act(async () => pending.resolve({ blob: new Blob(['old']), fileName: 'old.xml' })); expect(create).not.toHaveBeenCalled();
  } finally { create.mockRestore(); }
 });
});
