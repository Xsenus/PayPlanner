import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyBranchesSection } from './CompanyBranchesSection';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { CompanyBranch } from '../../types';

const { confirm, success, error } = vi.hoisted(() => ({ confirm: vi.fn(), success: vi.fn(), error: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success, error }) }));
vi.mock('../../services/api', () => ({ apiService: { getCompanyBranches: vi.fn(), createCompanyBranch: vi.fn(), updateCompanyBranch: vi.fn() } }));
const A = 'A'.repeat(64), B = 'B'.repeat(64);
const branch = (patch: Partial<CompanyBranch> = {}): CompanyBranch => ({ id: 8, legalEntityId: 5, name: 'Office', kind: 'Office', status: 'Active', createdAt: '2026-10-31T00:00:00Z', editVersion: A, signerJson: '{"name":"Old signer"}', notes: 'Old notes', ...patch });
const conflict = () => new Error('HTTP 409 at /branches/8 - {"code":"CompanyBranchVersionConflict","message":"Подразделение изменено другим пользователем."}');
const props = { companyId: 5, bankAccounts: [], readOnly: false };
const saveButton = () => within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить' });
const input = () => within(screen.getByRole('dialog')).getByText('Название').closest('label')!.querySelector('input')!;
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
async function open() { await screen.findByText('Office'); fireEvent.click(screen.getByTitle('Редактировать подразделение')); fireEvent.change(input(), { target: { value: 'My draft' } }); }
async function rejectSave() { await open(); fireEvent.click(saveButton()); await screen.findByText('Подразделение изменено другим пользователем.'); }

describe('CompanyBranchesSection expected version', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => {
    vi.resetAllMocks(); session = documentCopyTestSession();
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([branch()]);
    vi.mocked(apiService.updateCompanyBranch).mockRejectedValue(conflict()); confirm.mockResolvedValue(true);
  });
  afterEach(() => session.restore());

  it('sends the original version and preserves the draft without auto-retry on conflict', async () => {
    render(<CompanyBranchesSection {...props} />); await rejectSave();
    expect(apiService.updateCompanyBranch).toHaveBeenCalledExactlyOnceWith(5, 8, expect.objectContaining({ name: 'My draft', expectedVersion: A, signerJson: branch().signerJson, notes: 'Old notes' }));
    expect(input().value).toBe('My draft'); expect(saveButton()).toHaveProperty('disabled', true); expect(confirm).not.toHaveBeenCalled(); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(1); expect(success).not.toHaveBeenCalled();
  });

  it.each([undefined, null, '', 'bad', 'Z'.repeat(64)])('blocks saving an existing branch with invalid baseline %s', async version => {
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([branch({ editVersion: version })]);
    render(<CompanyBranchesSection {...props} />); await open(); fireEvent.click(saveButton());
    expect(apiService.updateCompanyBranch).not.toHaveBeenCalled(); expect(input().value).toBe('My draft'); expect(saveButton()).toHaveProperty('disabled', true); expect(screen.getByRole('button', { name: 'Загрузить сохранённые данные' })).toBeTruthy();
  });

  it('cancelled reload retains the whole draft and original version without a GET', async () => {
    confirm.mockResolvedValue(false); render(<CompanyBranchesSection {...props} />); await rejectSave();
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить сохранённые данные' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Загрузить сохранённые данные' })).toHaveProperty('disabled', false));
    expect(input().value).toBe('My draft'); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(1); expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(1); expect(saveButton()).toHaveProperty('disabled', true);
  });

  it('explicit confirmed reload replaces all fields including hidden signer/notes before the next save', async () => {
    render(<CompanyBranchesSection {...props} />); await rejectSave();
    const current = branch({ name: 'Current office', editVersion: B, signerJson: '{"name":"Current signer"}', notes: 'Current notes', kpp: '780201001', status: 'Archived' });
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([current]);
    vi.mocked(apiService.updateCompanyBranch).mockResolvedValue(current);
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить сохранённые данные' }));
    await waitFor(() => expect(input().value).toBe('Current office'));
    expect(confirm).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ signal: expect.any(AbortSignal), confirmLabel: 'Заменить ввод' }));
    expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(1); fireEvent.click(saveButton());
    await waitFor(() => expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.updateCompanyBranch).mock.calls[1]).toEqual([5, 8, expect.objectContaining({ expectedVersion: B, name: 'Current office', signerJson: current.signerJson, notes: 'Current notes', kpp: '780201001', status: 'Archived' })]);
  });

  it.each(['network', 'missing', 'foreign', 'invalid-version'])('failed reload %s never rebases the preserved draft', async kind => {
    render(<CompanyBranchesSection {...props} />); await rejectSave();
    if (kind === 'network') vi.mocked(apiService.getCompanyBranches).mockRejectedValue(new Error('Network unavailable'));
    else vi.mocked(apiService.getCompanyBranches).mockResolvedValue(kind === 'missing' ? [] : [branch(kind === 'foreign' ? { legalEntityId: 6 } : { editVersion: null })]);
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить сохранённые данные' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Загрузить сохранённые данные' })).toHaveProperty('disabled', false));
    expect(input().value).toBe('My draft'); expect(saveButton()).toHaveProperty('disabled', true); expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(1); expect(success).not.toHaveBeenCalled();
  });

  it.each(['close', 'company', 'rights', 'session'])('reload confirmation cannot act after %s', async change => {
    const approval = deferred<boolean>(); confirm.mockReturnValue(approval.promise);
    const view = render(<CompanyBranchesSection {...props} />); await rejectSave();
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить сохранённые данные' }));
    const signal = confirm.mock.calls[0][0].signal as AbortSignal;
    if (change === 'close') fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Закрыть' }));
    else if (change === 'company') { vi.mocked(apiService.getCompanyBranches).mockResolvedValue([branch({ legalEntityId: 6 })]); view.rerender(<CompanyBranchesSection {...props} companyId={6} />); }
    else if (change === 'rights') view.rerender(<CompanyBranchesSection {...props} readOnly />);
    else session.replace();
    const reads = vi.mocked(apiService.getCompanyBranches).mock.calls.length;
    await act(async () => approval.resolve(true));
    if (change !== 'session') expect(signal.aborted).toBe(true);
    expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(reads); expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(1); expect(success).not.toHaveBeenCalled();
  });

  it('a late reload response cannot replace a new editor after the old editor is closed', async () => {
    const pending = deferred<CompanyBranch[]>(); render(<CompanyBranchesSection {...props} />); await rejectSave();
    vi.mocked(apiService.getCompanyBranches).mockReturnValue(pending.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить сохранённые данные' }));
    await waitFor(() => expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(2));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Закрыть' }));
    await act(async () => pending.resolve([branch({ name: 'Current office', editVersion: B })]));
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.getByText('Current office')).toBeTruthy(); expect(success).not.toHaveBeenCalled();
  });

  it('archive conflict leaves the row untouched and requires a fresh list and a new explicit action', async () => {
    render(<CompanyBranchesSection {...props} />); await screen.findByText('Office');
    fireEvent.click(screen.getByRole('button', { name: 'В архив' })); await screen.findByRole('button', { name: 'Обновить подразделения' });
    expect(apiService.updateCompanyBranch).toHaveBeenCalledExactlyOnceWith(5, 8, expect.objectContaining({ expectedVersion: A, status: 'Archived' }));
    expect(screen.getByRole('button', { name: 'В архив' })).toHaveProperty('disabled', true); expect(screen.getByText('Активно')).toBeTruthy();
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([branch({ editVersion: B, notes: 'Fresh notes' })]);
    vi.mocked(apiService.updateCompanyBranch).mockResolvedValue(branch({ editVersion: B, status: 'Archived' }));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить подразделения' })); await waitFor(() => expect(screen.getByRole('button', { name: 'В архив' })).toHaveProperty('disabled', false));
    expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(1); fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    await waitFor(() => expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.updateCompanyBranch).mock.calls[1][2]).toEqual(expect.objectContaining({ expectedVersion: B, notes: 'Fresh notes', status: 'Archived' }));
  });

  it('an old server response remains readable but cannot be archived without a version', async () => {
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([branch({ editVersion: null })]);
    render(<CompanyBranchesSection {...props} />); await screen.findByText('Office'); fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    expect(apiService.updateCompanyBranch).not.toHaveBeenCalled(); expect(screen.getByRole('button', { name: 'Обновить подразделения' })).toBeTruthy();
  });

  it('a duplicate-name conflict keeps normal correction available and never reloads automatically', async () => {
    vi.mocked(apiService.updateCompanyBranch).mockRejectedValue(new Error('HTTP 409 at /branches/8 - {"message":"Название уже используется."}'));
    render(<CompanyBranchesSection {...props} />); await open(); fireEvent.click(saveButton()); await screen.findByText('Название уже используется.');
    expect(saveButton()).toHaveProperty('disabled', false); expect(screen.queryByRole('button', { name: 'Загрузить сохранённые данные' })).toBeNull(); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(1);
  });
});
