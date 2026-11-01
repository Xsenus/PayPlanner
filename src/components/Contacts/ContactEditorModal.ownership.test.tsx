import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ContactEditorModal } from './ContactEditorModal';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
const toasts = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => toasts }));
vi.mock('../../hooks/useFormRequirements', () => ({ useFormRequirements: () => ({ isRequired: () => false }) }));
vi.mock('../ui/ObjectAuditModal', () => ({ ObjectAuditLauncher: () => null }));
vi.mock('../../services/api', () => ({ apiService: { updateExternalContact: vi.fn(), updateClientContact: vi.fn(), createExternalContact: vi.fn() } }));
const contact = (sourceId: number): ContactDirectoryItemDto => ({ sourceId, sourceType: 'external', category: 'external', contactType: 'Person', fullName: `Контакт ${sourceId}`, roles: ['Contact'], hasFullProfile: false, phones: [], emails: [], isActive: true, syncToPhonebook: false });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((finish, fail) => { resolve = finish; reject = fail; }); return { promise, resolve, reject }; }
function props() { return { open: true, mode: 'edit-external' as const, contact: contact(1), legalEntities: [], responsibles: [], canManageResponsible: false, ownerScope: 'all', onClose: vi.fn(), onSaved: vi.fn() }; }
describe('Contact editor operation owner', () => {
 beforeEach(() => { vi.resetAllMocks(); localStorage.clear(); vi.mocked(apiService.updateExternalContact).mockResolvedValue(contact(1)); });
 it('contact editor baseline: late save of another contact never closes or refreshes the new editor', async () => {
  const pending = deferred<ContactDirectoryItemDto>(); vi.mocked(apiService.updateExternalContact).mockReturnValueOnce(pending.promise);
  const p = props(); const view = render(<ContactEditorModal {...p} />); await screen.findByDisplayValue('Контакт 1'); fireEvent.click(screen.getByRole('button', { name: /Сохранить/ }));
  view.rerender(<ContactEditorModal {...p} contact={contact(2)} ownerScope="own" />); await screen.findByDisplayValue('Контакт 2');
  await act(async () => pending.resolve(contact(1)));
  expect(p.onSaved).not.toHaveBeenCalled(); expect(p.onClose).not.toHaveBeenCalled(); expect(toasts.success).not.toHaveBeenCalled();
 });
 it('contact editor baseline: replaced login blocks a held save before the parent rerenders', async () => {
  const session = documentCopyTestSession(); try { const p = props(); render(<ContactEditorModal {...p} />); await screen.findByDisplayValue('Контакт 1'); session.replace('login'); fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); expect(apiService.updateExternalContact).not.toHaveBeenCalled(); } finally { session.restore(); }
 });
 it('contact editor baseline: close invalidates a pending save even when the controlled host keeps it mounted', async () => {
  const pending = deferred<ContactDirectoryItemDto>(); vi.mocked(apiService.updateExternalContact).mockReturnValueOnce(pending.promise);
  const p = props(); render(<ContactEditorModal {...p} />); await screen.findByDisplayValue('Контакт 1'); fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); expect(p.onClose).toHaveBeenCalledTimes(1);
  await act(async () => pending.resolve(contact(1))); expect(p.onSaved).not.toHaveBeenCalled(); expect(p.onClose).toHaveBeenCalledTimes(1);
 });
 it.each(['create-external', 'edit-external', 'edit-client'] as const)('contact editor current save: %s keeps its API and payload', async mode => {
  const p = props(); const item = { ...p.contact, sourceType: mode === 'edit-client' ? 'client' as const : 'external' as const, legalEntityId: 301, responsibleUserId: 9, phones: ['+70000000000'], emails: ['contact@local.invalid'] };
  const endpoint = mode === 'create-external' ? apiService.createExternalContact : mode === 'edit-client' ? apiService.updateClientContact : apiService.updateExternalContact;
  vi.mocked(endpoint).mockResolvedValue(item);
  render(<ContactEditorModal {...p} mode={mode} contact={item} />);
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(p.onSaved).toHaveBeenCalledWith(item));
  const payload = { fullName: 'Контакт 1', phone: '+70000000000', email: 'contact@local.invalid', legalEntityId: 301 };
  if (mode === 'create-external') expect(apiService.createExternalContact).toHaveBeenCalledWith(expect.objectContaining(payload));
  else expect(endpoint).toHaveBeenCalledWith(1, expect.objectContaining(payload));
  expect(p.onClose).toHaveBeenCalledTimes(1); expect(toasts.success).toHaveBeenCalledTimes(1);
 });
 it('contact editor current save: failure preserves edits and a retry submits once while fields stay disabled', async () => {
  const pending = deferred<ContactDirectoryItemDto>();
  vi.mocked(apiService.updateExternalContact).mockRejectedValueOnce(new Error('Временная ошибка')).mockReturnValueOnce(pending.promise);
  const p = props(); render(<ContactEditorModal {...p} />);
  const input = screen.getByDisplayValue('Контакт 1'); fireEvent.change(input, { target: { value: 'Новый текст' } });
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); await screen.findByText('Временная ошибка');
  expect(screen.getByDisplayValue('Новый текст').matches(":disabled")).toBe(false); expect(p.onClose).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); fireEvent.click(screen.getByRole('button', { name: /Сохранить/ }));
  expect(apiService.updateExternalContact).toHaveBeenCalledTimes(2); expect(input.matches(":disabled")).toBe(true); expect(screen.getByRole('button', { name: 'Отмена' }).matches(":disabled")).toBe(false);
  await act(async () => pending.resolve(contact(1))); expect(p.onSaved).toHaveBeenCalledTimes(1);
 });
 it('contact editor current save: same-user token refresh retains draft and pending result', async () => {
  const session = documentCopyTestSession();
  try {
   const pending = deferred<ContactDirectoryItemDto>(); vi.mocked(apiService.updateExternalContact).mockReturnValueOnce(pending.promise);
   const p = props(); const view = render(<ContactEditorModal {...p} />);
   fireEvent.change(screen.getByDisplayValue('Контакт 1'), { target: { value: 'Сохранённый ввод' } });
   fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); session.refresh(); view.rerender(<ContactEditorModal {...p} />);
   expect(screen.getByDisplayValue('Сохранённый ввод').matches(":disabled")).toBe(true); await act(async () => pending.resolve(contact(1)));
   expect(p.onSaved).toHaveBeenCalledTimes(1); expect(p.onClose).toHaveBeenCalledTimes(1);
  } finally { session.restore(); }
 });
 it('contact editor ownership: denied save ignores a previous failure and dispatches no new request', async () => {
  const pending = deferred<ContactDirectoryItemDto>(); vi.mocked(apiService.updateExternalContact).mockReturnValueOnce(pending.promise);
  const p = props(); const view = render(<ContactEditorModal {...p} />); fireEvent.click(screen.getByRole('button', { name: /Сохранить/ }));
  view.rerender(<ContactEditorModal {...p} allowSave={false} />);
  await act(async () => pending.reject(new Error('Ошибка прежней формы')));
  expect(screen.queryByText('Ошибка прежней формы')).toBeNull(); expect(toasts.error).not.toHaveBeenCalled(); expect(p.onSaved).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: /Сохранить/ }).matches(":disabled")).toBe(true); expect(screen.getByDisplayValue('Контакт 1').matches(":disabled")).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); expect(apiService.updateExternalContact).toHaveBeenCalledTimes(1);
 });
 it('contact editor catalogues: loading and denial preserve assigned IDs and draft until explicit retry', async () => {
  const p = props(); const item = { ...p.contact, legalEntityId: 301, responsibleUserId: 9 };
  const retry = vi.fn(); const view = render(<ContactEditorModal {...p} contact={item} canManageResponsible legalEntitiesLoading responsiblesLoading />);
  fireEvent.change(screen.getByDisplayValue('Контакт 1'), { target: { value: 'Мой черновик' } });
  expect((screen.getByTestId('contact-legal-entity-select') as HTMLSelectElement).value).toBe('301'); expect((screen.getByTestId('contact-responsible-select') as HTMLSelectElement).value).toBe('9');
  view.rerender(<ContactEditorModal {...p} contact={item} canManageResponsible legalEntitiesError="Доступ к организациям закрыт" responsiblesError="Доступ к сотрудникам закрыт" onRetryLegalEntities={retry} />);
  expect(screen.getByDisplayValue('Мой черновик')).toBeTruthy(); expect((screen.getByTestId('contact-legal-entity-select') as HTMLSelectElement).value).toBe('301');
  expect((screen.getByTestId('contact-responsible-select') as HTMLSelectElement).value).toBe('9'); expect(screen.getByTestId('contact-legal-entity-select').matches(":disabled")).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку организаций' })); expect(retry).toHaveBeenCalledTimes(1);
  expect(apiService.updateExternalContact).not.toHaveBeenCalled();
 });
 it('contact editor validation: a blank name never dispatches a write', () => {
  const p = props(); render(<ContactEditorModal {...p} mode="create-external" contact={null} />);
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ })); expect(screen.getByText('Укажите ФИО контакта')).toBeTruthy();
  expect(apiService.createExternalContact).not.toHaveBeenCalled();
 });
 it('contact editor held selection: saving ignores a previous select callback and retries the original type', async () => {
  const pending = deferred<ContactDirectoryItemDto>(); vi.mocked(apiService.updateExternalContact).mockReturnValueOnce(pending.promise);
  const p = props(); const view = render(<ContactEditorModal {...p} />);
  const type = view.container.querySelector('select')!;
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ }));
  fireEvent.change(type, { target: { value: 'Organization' } });
  await act(async () => pending.reject(new Error('Повторите сохранение')));
  expect(screen.getByDisplayValue('Контакт 1')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(p.onSaved).toHaveBeenCalledTimes(1));
  expect(apiService.updateExternalContact).toHaveBeenLastCalledWith(1, expect.objectContaining({ contactType: 'Person' }));
 });
});
