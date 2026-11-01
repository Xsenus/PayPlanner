import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ClientModal } from './ClientModal';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { Client, LegalEntityDetail, LegalEntityDadataDetails } from '../../types';

const ports = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock('../../hooks/useTranslation', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: ports.confirm }) }));
vi.mock('../../hooks/useFormRequirements', () => ({ useFormRequirements: () => ({ isRequired: () => false }) }));
vi.mock('../../services/api', () => ({ apiService: { createLegalEntity: vi.fn(), findLegalEntityById: vi.fn() } }));

const company: LegalEntityDetail = { id: 31, shortName: 'Организация', clients: [], clientsCount: 0, createdAt: '2026-11-01T00:00:00Z' };
const details: LegalEntityDadataDetails = { inn: '7701000000', shortName: 'Найденная компания', address: 'Тестовый адрес' };
const client = (id = 1): Client => ({ id, name: `Клиент ${id}`, phone: '', email: '', company: '', address: '', notes: '', isActive: true, createdAt: '2026-11-01T00:00:00Z' });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
const input = (name: string) => document.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
const submit = () => fireEvent.submit(document.querySelector('form')!);
function legal() { fireEvent.click(screen.getByRole('button', { name: 'Юридическое лицо' })); fireEvent.change(input('legalInn'), { target: { value: '7701000000' } }); fireEvent.change(input('legalName'), { target: { value: 'Организация' } }); }
function setup() { const props = { isOpen: true, ownerScope: 'all', onClose: vi.fn(), onSubmit: vi.fn().mockResolvedValue(undefined) }; return { ...render(<ClientModal {...props} />), props }; }

describe('quick client action ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); ports.confirm.mockResolvedValue(true); vi.mocked(apiService.createLegalEntity).mockResolvedValue(company); vi.mocked(apiService.findLegalEntityById).mockResolvedValue(details); });
  afterEach(() => session.restore());

  it('quick client baseline: closing during company creation prevents the next client write', async () => {
    const pending = deferred<LegalEntityDetail>(); vi.mocked(apiService.createLegalEntity).mockReturnValue(pending.promise); const view = setup(); legal(); submit(); await waitFor(() => expect(apiService.createLegalEntity).toHaveBeenCalledOnce());
    fireEvent.click(screen.getAllByRole('button', { name: 'cancel' }).at(-1)!); await act(async () => pending.resolve(company)); expect(view.props.onSubmit).not.toHaveBeenCalled(); expect(view.props.onClose).toHaveBeenCalledOnce();
  });
  it('quick client baseline: a closed but not yet unmounted form cannot start company creation', async () => {
    setup(); legal(); fireEvent.click(screen.getAllByRole('button', { name: 'cancel' }).at(-1)!); submit(); await act(async () => {}); expect(apiService.createLegalEntity).not.toHaveBeenCalled();
  });
  it('quick client baseline: two form submits cannot dispatch company creation twice', async () => {
    vi.mocked(apiService.createLegalEntity).mockReturnValue(new Promise(() => {})); setup(); legal(); act(() => { submit(); submit(); }); expect(apiService.createLegalEntity).toHaveBeenCalledOnce();
  });
  it('quick client baseline: a late lookup cannot overwrite a manually edited name', async () => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); fireEvent.change(input('legalName'), { target: { value: 'Мой ввод' } });
    await act(async () => pending.resolve(details)); expect(input('legalName').value).toBe('Мой ввод'); expect(screen.queryByText('Данные найдены и подставлены.')).toBeNull();
  });
  it('quick client baseline: an old lookup cannot return an individual form to legal mode', async () => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); fireEvent.click(screen.getByRole('button', { name: 'Физическое лицо' }));
    await act(async () => pending.resolve(details)); expect(input('firstName')).not.toBeNull(); expect(input('legalInn')).toBeNull();
  });
  it('quick client baseline: a previous scope lookup failure is absent from the new form', async () => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); const view = setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); view.rerender(<ClientModal {...view.props} ownerScope="own" />);
    fireEvent.click(screen.getByRole('button', { name: 'Юридическое лицо' })); await act(async () => pending.reject(new Error('Старый отказ'))); expect(screen.queryByText('Старый отказ')).toBeNull(); expect(input('legalInn').value).toBe('');
  });
  it('quick client baseline: closing a delete confirmation prevents the later delete callback', async () => {
    const pending = deferred<boolean>(); ports.confirm.mockReturnValue(pending.promise); const onDelete = vi.fn().mockResolvedValue(undefined); const props = { isOpen: true, client: client(), onClose: vi.fn(), onSubmit: vi.fn(), onDelete }; render(<ClientModal {...props} />);
    fireEvent.click(screen.getByRole('button', { name: 'delete' })); fireEvent.click(screen.getAllByRole('button', { name: 'cancel' }).at(-1)!); await act(async () => pending.resolve(true)); expect(onDelete).not.toHaveBeenCalled();
  });
  it('quick client baseline: a confirmed company is reused after a client save failure', async () => {
    const view = setup(); view.props.onSubmit.mockRejectedValueOnce(new Error('Клиент не сохранён')); legal(); submit(); await screen.findByText('Клиент не сохранён'); submit(); await waitFor(() => expect(view.props.onClose).toHaveBeenCalledOnce());
    expect(apiService.createLegalEntity).toHaveBeenCalledOnce(); expect(view.props.onSubmit).toHaveBeenCalledTimes(2); expect(view.props.onSubmit).toHaveBeenLastCalledWith(expect.objectContaining({ legalEntityId: 31 }));
  });
  it('quick client baseline: parent invalidation before render blocks direct company creation', async () => {
    let active = true; const editorOwner = { scope: 'parent', capture: () => active ? { isCurrent: () => active } : null }; const props = { isOpen: true, editorOwner, onClose: vi.fn(), onSubmit: vi.fn() }; render(<ClientModal {...props} />); legal(); active = false; submit(); await act(async () => {}); expect(apiService.createLegalEntity).not.toHaveBeenCalled();
  });
  it('quick client baseline: a changed login blocks lookup before a React render', async () => {
    setup(); legal(); session.replace('actor'); fireEvent.click(screen.getByTitle('Найти по ИНН')); await act(async () => {}); expect(apiService.findLegalEntityById).not.toHaveBeenCalled();
  });
  it('quick client baseline: a late client save cannot close a replacement client editor', async () => {
    const pending = deferred<void>(); const props = { isOpen: true, client: client(1), onClose: vi.fn(), onSubmit: vi.fn().mockReturnValue(pending.promise) }; const view = render(<ClientModal {...props} />); submit(); await waitFor(() => expect(props.onSubmit).toHaveBeenCalledOnce()); view.rerender(<ClientModal {...props} client={client(2)} />);
    await act(async () => pending.resolve()); expect(props.onClose).not.toHaveBeenCalled(); expect(input('firstName').value).toBe('2');
  });
  it('quick client regression: ordinary legal creation keeps the original payload and links the confirmed id once', async () => {
    const view = setup(); legal(); submit(); await waitFor(() => expect(view.props.onClose).toHaveBeenCalledOnce());
    expect(apiService.createLegalEntity).toHaveBeenCalledWith(expect.objectContaining({ formRequirementsSection: 'clients-modal-legal', shortName: 'Организация', inn: '7701000000', clientIds: [] }));
    expect(view.props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ name: 'Организация', legalEntityId: 31, company: 'Организация' })); expect(apiService.createLegalEntity).toHaveBeenCalledOnce(); expect(view.props.onSubmit).toHaveBeenCalledOnce();
  });
  it('quick client regression: an individual save never creates a company', async () => {
    const view = setup(); fireEvent.change(input('firstName'), { target: { value: 'Анна' } }); submit(); await waitFor(() => expect(view.props.onClose).toHaveBeenCalledOnce()); expect(apiService.createLegalEntity).not.toHaveBeenCalled(); expect(view.props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ name: 'Анна', legalEntityId: null }));
  });
  it('quick client regression: an existing legal association is preserved without another company write', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined); const props = { isOpen: true, client: { ...client(), legalEntityId: 55 }, onClose: vi.fn(), onSubmit }; render(<ClientModal {...props} />); legal(); submit(); await waitFor(() => expect(props.onClose).toHaveBeenCalledOnce()); expect(apiService.createLegalEntity).not.toHaveBeenCalled(); expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ legalEntityId: 55 }));
  });
  it('quick client regression: ordinary lookup preserves later comments and fills legal details', async () => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); const view = setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН'));
    fireEvent.change(document.querySelector('textarea[name="comment"]')!, { target: { value: 'Новый комментарий' } }); await act(async () => pending.resolve({ ...details, phones: ['+70000000000'], emails: ['test@example.local'] }));
    expect(input('legalName').value).toBe(details.shortName); expect(screen.getByText('Адрес: Тестовый адрес')).toBeTruthy(); expect((document.querySelector('textarea') as HTMLTextAreaElement).value).toBe('Новый комментарий'); submit(); await waitFor(() => expect(view.props.onSubmit).toHaveBeenCalled()); expect(view.props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ address: 'Тестовый адрес', email: 'test@example.local', phone: '+70000000000' }));
  });
  it.each(['inn', 'kind'] as const)('quick client regression: a %s round trip cannot revive an old lookup', async kind => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); const signal = vi.mocked(apiService.findLegalEntityById).mock.calls[0][1]!.signal!;
    if (kind === 'inn') { fireEvent.change(input('legalInn'), { target: { value: '7702000000' } }); fireEvent.change(input('legalInn'), { target: { value: '7701000000' } }); }
    else { fireEvent.click(screen.getByRole('button', { name: 'Физическое лицо' })); fireEvent.click(screen.getByRole('button', { name: 'Юридическое лицо' })); }
    expect(signal.aborted).toBe(true); await act(async () => pending.resolve(details)); expect(input('legalName').value).toBe('Организация'); expect(screen.queryByText('Данные найдены и подставлены.')).toBeNull();
  });
  it.each(['cross', 'escape'] as const)('quick client regression: %s closes and aborts lookup before delayed unmount', async exit => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); const view = setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); const signal = vi.mocked(apiService.findLegalEntityById).mock.calls[0][1]!.signal!;
    if (exit === 'cross') fireEvent.click(screen.getAllByRole('button', { name: 'cancel' })[0]); else fireEvent.keyDown(window, { key: 'Escape' });
    expect(signal.aborted).toBe(true); await act(async () => pending.reject(new Error('Поздняя ошибка'))); expect(view.props.onClose).toHaveBeenCalledOnce(); expect(screen.queryByText('Поздняя ошибка')).toBeNull(); fireEvent.click(screen.getByTitle('Найти по ИНН')); expect(apiService.findLegalEntityById).toHaveBeenCalledOnce();
  });
  it('quick client regression: ordinary token refresh retains input and pending lookup', async () => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); const view = setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); session.refresh(); view.rerender(<ClientModal {...view.props} />); expect(input('legalInn').value).toBe('7701000000'); await act(async () => pending.resolve(details)); expect(input('legalName').value).toBe(details.shortName); expect(apiService.findLegalEntityById).toHaveBeenCalledOnce();
  });
  it('quick client regression: a confirmed new company cannot leak into a new scope', async () => {
    const view = setup(); view.props.onSubmit.mockRejectedValueOnce(new Error('Ошибка клиента')); legal(); submit(); await screen.findByText('Ошибка клиента'); view.rerender(<ClientModal {...view.props} ownerScope="other" />); expect(screen.queryByText(/Организация уже создана/)).toBeNull(); legal(); submit(); await waitFor(() => expect(view.props.onClose).toHaveBeenCalledOnce()); expect(apiService.createLegalEntity).toHaveBeenCalledTimes(2);
  });
  it('quick client regression: switching to an individual after a failed legal save does not attach the new company', async () => {
    const view = setup(); view.props.onSubmit.mockRejectedValueOnce(new Error('Ошибка клиента')); legal(); submit(); await screen.findByText('Ошибка клиента'); fireEvent.click(screen.getByRole('button', { name: 'Физическое лицо' })); submit(); await waitFor(() => expect(view.props.onClose).toHaveBeenCalledOnce()); expect(view.props.onSubmit).toHaveBeenLastCalledWith(expect.objectContaining({ legalEntityId: null })); expect(apiService.createLegalEntity).toHaveBeenCalledOnce();
  });
  it('quick client regression: an invalid company acknowledgement cannot become a client association', async () => {
    vi.mocked(apiService.createLegalEntity).mockResolvedValue({ ...company, id: 0 }); const view = setup(); legal(); submit(); await screen.findByText(/Не удалось подтвердить созданную организацию/); expect(view.props.onSubmit).not.toHaveBeenCalled(); expect(view.props.onClose).not.toHaveBeenCalled();
  });
  it.each(['other-inn', 'phones', 'null'] as const)('quick client regression: malformed lookup %s preserves the form', async kind => {
    const value = kind === 'other-inn' ? { ...details, inn: '7702000000' } : kind === 'phones' ? { ...details, phones: [123] } : null;
    vi.mocked(apiService.findLegalEntityById).mockResolvedValue(value as unknown as LegalEntityDadataDetails); setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); await screen.findByText(/Не удалось подтвердить организацию/); expect(input('legalName').value).toBe('Организация');
  });
  it('quick client regression: null optional phone and email lists remain compatible', async () => {
    vi.mocked(apiService.findLegalEntityById).mockResolvedValue({ ...details, phones: null, emails: null } as unknown as LegalEntityDadataDetails); setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); await screen.findByText('Данные найдены и подставлены.'); expect(input('legalName').value).toBe(details.shortName);
  });
  it('quick client regression: a current lookup error allows one explicit retry and keeps input', async () => {
    vi.mocked(apiService.findLegalEntityById).mockRejectedValueOnce(new Error('Поиск недоступен')); setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); await screen.findByText('Поиск недоступен'); expect(input('legalName').value).toBe('Организация'); expect(apiService.findLegalEntityById).toHaveBeenCalledOnce(); fireEvent.click(screen.getByTitle('Найти по ИНН')); await screen.findByText('Данные найдены и подставлены.'); expect(apiService.findLegalEntityById).toHaveBeenCalledTimes(2);
  });
  it('quick client regression: deletion confirmation locks duplicate delete and save actions', async () => {
    const pending = deferred<boolean>(); ports.confirm.mockReturnValue(pending.promise); const props = { isOpen: true, client: client(), onClose: vi.fn(), onSubmit: vi.fn(), onDelete: vi.fn().mockResolvedValue(undefined) }; render(<ClientModal {...props} />); const remove = screen.getByRole('button', { name: 'delete' }); act(() => { fireEvent.click(remove); fireEvent.click(remove); submit(); }); expect(ports.confirm).toHaveBeenCalledOnce(); expect(props.onSubmit).not.toHaveBeenCalled(); await act(async () => pending.resolve(false)); expect(props.onDelete).not.toHaveBeenCalled(); fireEvent.click(screen.getByRole('button', { name: 'delete' })); expect(ports.confirm).toHaveBeenCalledTimes(2);
  });
  it('quick client regression: a current delete failure is visible in its own form', async () => {
    const props = { isOpen: true, client: client(), onClose: vi.fn(), onSubmit: vi.fn(), onDelete: vi.fn().mockRejectedValue(new Error('Удаление недоступно')) }; render(<ClientModal {...props} />); fireEvent.click(screen.getByRole('button', { name: 'delete' })); await screen.findByText('Удаление недоступно'); expect(props.onClose).not.toHaveBeenCalled(); expect((screen.getByRole('button', { name: 'delete' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it('quick client regression: saving aborts lookup and locks input through both write stages', async () => {
    const lookup = deferred<LegalEntityDadataDetails>(); const created = deferred<LegalEntityDetail>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(lookup.promise); vi.mocked(apiService.createLegalEntity).mockReturnValue(created.promise); const view = setup(); legal(); fireEvent.click(screen.getByTitle('Найти по ИНН')); const signal = vi.mocked(apiService.findLegalEntityById).mock.calls[0][1]!.signal!; submit(); expect(signal.aborted).toBe(true);
    fireEvent.change(input('legalName'), { target: { value: 'Несохранённая подмена' } }); fireEvent.click(screen.getByRole('button', { name: 'Физическое лицо' })); expect(input('legalName').value).toBe('Организация'); await act(async () => lookup.resolve(details)); expect(input('legalName').value).toBe('Организация'); await act(async () => created.resolve(company)); await waitFor(() => expect(view.props.onClose).toHaveBeenCalledOnce()); expect(view.props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ name: 'Организация' }));
  });
  it('quick client regression: repeated lookup clicks start only one pending request', async () => {
    const pending = deferred<LegalEntityDadataDetails>(); vi.mocked(apiService.findLegalEntityById).mockReturnValue(pending.promise); setup(); legal(); const search = screen.getByTitle('Найти по ИНН'); act(() => { fireEvent.click(search); fireEvent.click(search); }); expect(apiService.findLegalEntityById).toHaveBeenCalledOnce(); await act(async () => pending.resolve(details)); await screen.findByText('Данные найдены и подставлены.');
  });
  it.each(['close', 'scope'] as const)('quick client confirmation: %s cancels the previous delete dialog', async mode => {
    const pending = deferred<boolean>(); ports.confirm.mockReturnValue(pending.promise); const props = { isOpen: true, ownerScope: 'all', client: client(), onClose: vi.fn(), onSubmit: vi.fn(), onDelete: vi.fn() }; const view = render(<ClientModal {...props} />); fireEvent.click(screen.getByRole('button', { name: 'delete' }));
    const signal = ports.confirm.mock.calls[0][0].signal as AbortSignal; expect(signal.aborted).toBe(false);
    if (mode === 'close') fireEvent.click(screen.getAllByRole('button', { name: 'cancel' }).at(-1)!); else view.rerender(<ClientModal {...props} ownerScope="own" />);
    expect(signal.aborted).toBe(true); await act(async () => pending.resolve(true)); expect(props.onDelete).not.toHaveBeenCalled();
  });
});
