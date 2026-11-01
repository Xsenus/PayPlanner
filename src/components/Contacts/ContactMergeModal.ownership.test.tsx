import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ContactMergeModal } from './ContactMergeModal';
import { apiService, type ContactDirectoryItemDto, type ContactMergePreviewDto } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';

const confirm = vi.hoisted(() => vi.fn());
const success = vi.hoisted(() => vi.fn());
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success, error: vi.fn() }) }));
vi.mock('../../services/api', () => ({ apiService: { getContacts: vi.fn(), previewContactMerge: vi.fn(), mergeContacts: vi.fn() } }));

function contact(sourceId: number): ContactDirectoryItemDto {
  return { sourceId, sourceType: 'external', category: 'people', contactType: 'Person', roles: ['Contact'], fullName: `Контакт ${sourceId}`, phones: ['+70000000000'], emails: [], hasFullProfile: false, isActive: true, syncToPhonebook: false };
}
const preview: ContactMergePreviewDto = { canMerge: true, conflicts: [], affectedLinks: 3, primaryName: 'Контакт 1', duplicateName: 'Контакт 2', phones: [], emails: [] };
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function props() { return { ownerScope: 'all', onMerged: vi.fn(), onClose: vi.fn() }; }
async function selectPair(duplicate = 2) {
  await screen.findAllByRole('option', { name: /Контакт 1/ });
  fireEvent.change(screen.getByRole('combobox', { name: 'Основная карточка' }), { target: { value: 'external:1' } });
  fireEvent.change(screen.getByRole('combobox', { name: 'Дубль в архив' }), { target: { value: `external:${duplicate}` } });
}

describe('Contact merge operation owner', () => {
  beforeEach(() => {
    vi.resetAllMocks(); localStorage.clear();
    vi.mocked(apiService.getContacts).mockResolvedValue([contact(1), contact(2), contact(3)]);
    vi.mocked(apiService.previewContactMerge).mockResolvedValue(preview);
    vi.mocked(apiService.mergeContacts).mockResolvedValue(preview);
    confirm.mockResolvedValue(true);
  });

  it('merge owner: disabled access starts no directory read', () => {
    render(<ContactMergeModal {...props()} enabled={false} />);
    expect(apiService.getContacts).not.toHaveBeenCalled(); expect(screen.queryByRole('dialog')).toBeNull();
  });
  it('merge owner: a new scope discards and aborts the previous contacts response', async () => {
    const old = deferred<ContactDirectoryItemDto[]>(); vi.mocked(apiService.getContacts).mockReturnValueOnce(old.promise).mockResolvedValueOnce([contact(3)]);
    const p = props(); const view = render(<ContactMergeModal {...p} />);
    await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(apiService.getContacts).mock.calls[0][1]?.signal;
    view.rerender(<ContactMergeModal {...p} ownerScope="own" />); await screen.findAllByRole('option', { name: /Контакт 3/ });
    await act(async () => old.resolve([contact(1)]));
    expect(signal?.aborted).toBe(true); expect(screen.queryByRole('option', { name: /Контакт 1/ })).toBeNull();
  });
  it('merge pair: changing the duplicate cancels and rejects an obsolete successful preview', async () => {
    const old = deferred<ContactMergePreviewDto>(); vi.mocked(apiService.previewContactMerge).mockReturnValueOnce(old.promise).mockResolvedValueOnce({ ...preview, canMerge: false, affectedLinks: 8, conflicts: ['Новая пара запрещена'] });
    render(<ContactMergeModal {...props()} />); await selectPair(); await waitFor(() => expect(apiService.previewContactMerge).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(apiService.previewContactMerge).mock.calls[0][1]?.signal;
    fireEvent.change(screen.getByRole('combobox', { name: 'Дубль в архив' }), { target: { value: 'external:3' } });
    await screen.findByText('Новая пара запрещена'); await act(async () => old.resolve(preview));
    expect(signal?.aborted).toBe(true); expect(screen.getByText('Будет перенесено связей: 8')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Объединить и архивировать' }).matches(':disabled')).toBe(true);
  });
  it('merge pair: an invalid pair stops the pending indicator and cannot merge a previous result', async () => {
    const pending = deferred<ContactMergePreviewDto>(); vi.mocked(apiService.previewContactMerge).mockReturnValueOnce(pending.promise);
    render(<ContactMergeModal {...props()} />); await selectPair(); await screen.findByText('Проверяем связи и права…');
    fireEvent.change(screen.getByRole('combobox', { name: 'Дубль в архив' }), { target: { value: 'external:1' } });
    expect(screen.queryByText('Проверяем связи и права…')).toBeNull(); await act(async () => pending.resolve(preview));
    fireEvent.click(screen.getByRole('button', { name: 'Объединить и архивировать' })); expect(apiService.mergeContacts).not.toHaveBeenCalled();
  });
  it('merge confirmation: another scope never dispatches an approved old pair', async () => {
    const approval = deferred<boolean>(); confirm.mockReturnValueOnce(approval.promise);
    const p = props(); const view = render(<ContactMergeModal {...p} />); await selectPair(); await screen.findByText('Будет перенесено связей: 3');
    fireEvent.click(screen.getByRole('button', { name: 'Объединить и архивировать' }));
    view.rerender(<ContactMergeModal {...p} ownerScope="own" />); expect(confirm.mock.calls[0][0].signal.aborted).toBe(true);
    await act(async () => approval.resolve(true));
    expect(apiService.mergeContacts).not.toHaveBeenCalled(); expect(p.onMerged).not.toHaveBeenCalled();
  });
  it('merge session: replaced login rejects a held confirmation before another render', async () => {
    const session = documentCopyTestSession();
    try {
      const approval = deferred<boolean>(); confirm.mockReturnValueOnce(approval.promise);
      render(<ContactMergeModal {...props()} />); await selectPair(); await screen.findByText('Будет перенесено связей: 3');
      fireEvent.click(screen.getByRole('button', { name: 'Объединить и архивировать' })); session.replace('login');
      await act(async () => approval.resolve(true)); expect(apiService.mergeContacts).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });
  it('merge result: an already dispatched old merge cannot close or update the replacement window', async () => {
    const pending = deferred<ContactMergePreviewDto>(); vi.mocked(apiService.mergeContacts).mockReturnValueOnce(pending.promise);
    const p = props(); const view = render(<ContactMergeModal {...p} />); await selectPair(); await screen.findByText('Будет перенесено связей: 3');
    fireEvent.click(screen.getByRole('button', { name: 'Объединить и архивировать' })); await waitFor(() => expect(apiService.mergeContacts).toHaveBeenCalledTimes(1));
    view.rerender(<ContactMergeModal {...p} ownerScope="own" />); await act(async () => pending.resolve(preview));
    expect(p.onMerged).not.toHaveBeenCalled(); expect(p.onClose).not.toHaveBeenCalled(); expect(success).not.toHaveBeenCalled();
  });
  it('merge current: one confirmation freezes selection and allows an explicit retry after failure', async () => {
    const approval = deferred<boolean>(); confirm.mockReturnValueOnce(approval.promise).mockResolvedValue(true);
    vi.mocked(apiService.mergeContacts).mockRejectedValueOnce(new Error('Повторите объединение')).mockResolvedValueOnce(preview);
    const p = props(); render(<ContactMergeModal {...p} />); await selectPair(); await screen.findByText('Будет перенесено связей: 3');
    const button = screen.getByRole('button', { name: 'Объединить и архивировать' }); fireEvent.click(button); fireEvent.click(button);
    expect(confirm).toHaveBeenCalledTimes(1);
    const select = screen.getByRole('combobox', { name: 'Дубль в архив' }); expect(select.matches(':disabled')).toBe(true);
    fireEvent.change(select, { target: { value: 'external:3' } }); await act(async () => approval.resolve(true));
    expect((select as HTMLSelectElement).value).toBe('external:2'); expect(screen.getByText('Повторите объединение')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Объединить и архивировать' })); await waitFor(() => expect(p.onMerged).toHaveBeenCalledTimes(1));
    expect(apiService.mergeContacts).toHaveBeenLastCalledWith({ primarySourceType: 'external', primarySourceId: 1, duplicateSourceType: 'external', duplicateSourceId: 2 });
    expect(p.onClose).toHaveBeenCalledTimes(1);
  });
  it('merge retry: failed preview is repeated only on request and restores the same selected pair', async () => {
    vi.mocked(apiService.previewContactMerge).mockRejectedValueOnce(new Error('HTTP 403 - Проверка запрещена')).mockResolvedValueOnce(preview);
    render(<ContactMergeModal {...props()} />); await selectPair(); await screen.findByText('Недостаточно прав для выполнения действия.');
    expect(apiService.previewContactMerge).toHaveBeenCalledTimes(1); fireEvent.click(screen.getByRole('button', { name: 'Повторить проверку' }));
    await screen.findByText('Будет перенесено связей: 3'); expect(apiService.previewContactMerge).toHaveBeenCalledTimes(2); expect(apiService.getContacts).toHaveBeenCalledTimes(1);
  });
  it('merge read retry: an unavailable directory has an explicit retry without a preview or mutation', async () => {
    vi.mocked(apiService.getContacts).mockRejectedValueOnce(new Error('Каталог недоступен')).mockResolvedValueOnce([contact(1), contact(2)]);
    render(<ContactMergeModal {...props()} />); await screen.findByText('Каталог недоступен'); fireEvent.click(screen.getByRole('button', { name: 'Загрузить контакты повторно' }));
    await screen.findAllByRole('option', { name: /Контакт 1/ }); expect(apiService.getContacts).toHaveBeenCalledTimes(2); expect(apiService.previewContactMerge).not.toHaveBeenCalled();
  });
  it('merge parent render: an unchanged read owner object and refreshed token retain one directory read', async () => {
    const session = documentCopyTestSession();
    function Parent() { const owner = useDocumentDeliveryOwner('parent'); return <ContactMergeModal {...p} readOwner={owner} />; }
    const p = props();
    try {
      const view = render(<Parent />); await selectPair(); await screen.findByText('Будет перенесено связей: 3');
      session.refresh(); view.rerender(<Parent />);
      expect(apiService.getContacts).toHaveBeenCalledTimes(1); expect(apiService.previewContactMerge).toHaveBeenCalledTimes(1);
      expect((screen.getByRole('combobox', { name: 'Дубль в архив' }) as HTMLSelectElement).value).toBe('external:2');
    } finally { session.restore(); }
  });
  it('merge close: closing aborts a pending preview and suppresses its late error in a mounted host', async () => {
    const pending = deferred<ContactMergePreviewDto>(); vi.mocked(apiService.previewContactMerge).mockReturnValueOnce(pending.promise);
    const p = props(); render(<ContactMergeModal {...p} />); await selectPair(); await screen.findByText('Проверяем связи и права…');
    const signal = vi.mocked(apiService.previewContactMerge).mock.calls[0][1]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); await act(async () => pending.reject(new Error('Прежняя ошибка')));
    expect(p.onClose).toHaveBeenCalledTimes(1); expect(signal?.aborted).toBe(true); expect(screen.queryByRole('dialog')).toBeNull();
  });
});
