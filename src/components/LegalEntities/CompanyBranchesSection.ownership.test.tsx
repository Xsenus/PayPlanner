import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyBranchesSection } from './CompanyBranchesSection';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { CompanyBranch } from '../../types';
import { StrictMode } from 'react';

const { success, error } = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success, error }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('../../services/api', () => ({ apiService: { getCompanyBranches: vi.fn(), createCompanyBranch: vi.fn(), updateCompanyBranch: vi.fn() } }));
const branch = (companyId = 5, patch: Partial<CompanyBranch> = {}): CompanyBranch => ({ id: companyId, editVersion: 'A'.repeat(64), legalEntityId: companyId, name: `Office ${companyId}`, kind: 'Office', status: 'Active', createdAt: '2026-10-30T00:00:00Z', ...patch });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { resolve, reject, promise }; }
const props = (companyId = 5) => ({ companyId, bankAccounts: [], readOnly: false });
function newEditor(name = 'New office') {
  fireEvent.click(screen.getByRole('button', { name: 'Добавить' }));
  const dialog = screen.getByRole('dialog', { name: 'Подразделение' });
  fireEvent.change(within(dialog).getByText('Название').closest('label')!.querySelector('input')!, { target: { value: name } });
  return dialog;
}

describe('CompanyBranchesSection ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => {
    vi.resetAllMocks(); session = documentCopyTestSession();
    vi.mocked(apiService.getCompanyBranches).mockImplementation(async id => [branch(id)]);
    vi.mocked(apiService.createCompanyBranch).mockImplementation(async id => branch(id));
    vi.mocked(apiService.updateCompanyBranch).mockImplementation(async (id, _, value) => branch(id, value));
  });
  afterEach(() => session.restore());

  it('ownership baseline: ignores an old list after the company changes', async () => {
    const old = deferred<CompanyBranch[]>(); vi.mocked(apiService.getCompanyBranches).mockReturnValueOnce(old.promise);
    const view = render(<CompanyBranchesSection {...props()} />);
    view.rerender(<CompanyBranchesSection {...props(6)} />); await screen.findByText('Office 6');
    await act(async () => old.resolve([branch()]));
    expect(screen.queryByText('Office 5')).toBeNull(); expect(screen.getByText('Office 6')).toBeTruthy();
  });

  it('ownership baseline: removes the editor when the company changes', async () => {
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); newEditor('Private original draft');
    view.rerender(<CompanyBranchesSection {...props(6)} />);
    expect(screen.queryByRole('dialog')).toBeNull(); expect(apiService.createCompanyBranch).not.toHaveBeenCalled();
  });

  it('ownership baseline: an old save cannot close a different company editor', async () => {
    const old = deferred<CompanyBranch>(); vi.mocked(apiService.createCompanyBranch).mockReturnValueOnce(old.promise);
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5');
    fireEvent.click(within(newEditor()).getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiService.createCompanyBranch).toHaveBeenCalledTimes(1));
    view.rerender(<CompanyBranchesSection {...props(6)} />); await screen.findByText('Office 6');
    const leftover = screen.queryByRole('dialog'); if (leftover) fireEvent.click(within(leftover).getByRole('button', { name: 'Закрыть' }));
    newEditor('Current company draft');
    await act(async () => old.resolve(branch(5, { name: 'New office' })));
    expect(screen.getByRole('dialog')).toBeTruthy(); expect(screen.getByDisplayValue('Current company draft')).toBeTruthy(); expect(success).not.toHaveBeenCalled();
  });

  it('ownership baseline: a late archive failure does not notify another company', async () => {
    const old = deferred<CompanyBranch>(); vi.mocked(apiService.updateCompanyBranch).mockReturnValueOnce(old.promise);
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5');
    fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    view.rerender(<CompanyBranchesSection {...props(6)} />); await screen.findByText('Office 6');
    await act(async () => old.reject(new Error('Private old error')));
    expect(error).not.toHaveBeenCalled(); expect(screen.getByText('Office 6')).toBeTruthy();
  });

  it('ownership baseline: serializes duplicate archive actions', async () => {
    const pending = deferred<CompanyBranch>(); vi.mocked(apiService.updateCompanyBranch).mockReturnValue(pending.promise);
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5');
    const button = screen.getByRole('button', { name: 'В архив' }); fireEvent.click(button); fireEvent.click(button);
    await act(async () => pending.resolve(branch(5, { status: 'Archived' })));
    expect(apiService.updateCompanyBranch).toHaveBeenCalledTimes(1);
  });

  it('ownership baseline: revoking edit access removes the editor and cannot send', async () => {
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); newEditor();
    view.rerender(<CompanyBranchesSection {...props()} readOnly />);
    expect(screen.queryByRole('dialog')).toBeNull(); expect(apiService.createCompanyBranch).not.toHaveBeenCalled();
  });

  it('ownership baseline: session replacement without a render cannot archive', async () => {
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); session.replace();
    fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    expect(apiService.updateCompanyBranch).not.toHaveBeenCalled();
  });

  it('ownership guard: late list failure cannot replace the current company', async () => {
    const old = deferred<CompanyBranch[]>(); vi.mocked(apiService.getCompanyBranches).mockReturnValueOnce(old.promise);
    const view = render(<CompanyBranchesSection {...props()} />); const signal = vi.mocked(apiService.getCompanyBranches).mock.calls[0][2]!;
    view.rerender(<CompanyBranchesSection {...props(6)} />); await screen.findByText('Office 6');
    expect(signal.aborted).toBe(true); await act(async () => old.reject(new Error('Private old error')));
    expect(screen.queryByText('Private old error')).toBeNull(); expect(screen.getByText('Office 6')).toBeTruthy();
  });

  it('ownership guard: unmount aborts only the pending read', async () => {
    const old = deferred<CompanyBranch[]>(); vi.mocked(apiService.getCompanyBranches).mockReturnValueOnce(old.promise);
    const view = render(<CompanyBranchesSection {...props()} />); const signal = vi.mocked(apiService.getCompanyBranches).mock.calls[0][2]!;
    view.unmount(); expect(signal.aborted).toBe(true); await act(async () => old.resolve([branch()]));
    expect(success).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled();
  });

  it.each(['login', 'actor', 'logout'] as const)('ownership guard: %s remount removes private state', async change => {
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); newEditor('Private draft');
    session.replace(change); vi.mocked(apiService.getCompanyBranches).mockResolvedValue([]); view.rerender(<CompanyBranchesSection {...props()} />);
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.queryByText('Office 5')).toBeNull();
    if (change === 'logout') expect(screen.queryByTestId('company-branches-section')).toBeNull();
    else await screen.findByText('Подразделения не добавлены.');
  });

  it('ownership guard: a changed authority scope invalidates the same company editor', async () => {
    const view = render(<CompanyBranchesSection {...props()} ownerScope="old-role" />); await screen.findByText('Office 5'); newEditor();
    view.rerender(<CompanyBranchesSection {...props()} ownerScope="new-role" />);
    expect(screen.queryByRole('dialog')).toBeNull(); await screen.findByText('Office 5'); expect(apiService.createCompanyBranch).not.toHaveBeenCalled();
  });

  it('ownership guard: current actions work under StrictMode without a reload loop', async () => {
    render(<StrictMode><CompanyBranchesSection {...props()} /></StrictMode>); await screen.findByText('Office 5');
    expect(vi.mocked(apiService.getCompanyBranches).mock.calls.length).toBeLessThanOrEqual(2);
    expect(newEditor()).toBeTruthy();
  });

  it.each(['foreign', 'duplicate', 'invalid-id', 'not-array'])('ownership guard: rejects a %s list and recovers only on explicit retry', async kind => {
    const malformed = kind === 'foreign' ? [branch(6)] : kind === 'duplicate' ? [branch(), branch()] : kind === 'invalid-id' ? [branch(5, { id: 0 })] : null;
    vi.mocked(apiService.getCompanyBranches).mockResolvedValueOnce(malformed as unknown as CompanyBranch[]);
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Не удалось подтвердить список подразделений этой компании. Повторите загрузку.');
    expect(screen.queryByText('Office 6')).toBeNull(); expect(screen.queryByText('Подразделения не добавлены.')).toBeNull();
    expect(screen.getByRole('button', { name: 'Добавить' })).toHaveProperty('disabled', true);
    expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' })); await screen.findByText('Office 5');
    expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(2);
  });

  it('ownership guard: a failed read is unavailable rather than an empty registry', async () => {
    vi.mocked(apiService.getCompanyBranches).mockRejectedValueOnce(new Error('Temporary read failure'));
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Temporary read failure');
    expect(screen.getByText('Список подразделений недоступен.')).toBeTruthy();
    expect(screen.queryByText('Подразделения не добавлены.')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' })); await screen.findByText('Office 5');
  });

  for (const outcome of ['success', 'failure']) it(`ownership guard: a late save ${outcome} cannot adopt another session without a render`, async () => {
    const old = deferred<CompanyBranch>(); vi.mocked(apiService.createCompanyBranch).mockReturnValueOnce(old.promise);
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); fireEvent.click(within(newEditor()).getByRole('button', { name: 'Сохранить' }));
    session.replace(); await act(async () => outcome === 'success' ? old.resolve(branch()) : old.reject(new Error('Private error')));
    expect(success).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled(); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Private error')).toBeNull();
  });

  it('ownership guard: a late archive success cannot reload another company', async () => {
    const old = deferred<CompanyBranch>(); vi.mocked(apiService.updateCompanyBranch).mockReturnValueOnce(old.promise);
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    view.rerender(<CompanyBranchesSection {...props(6)} />); await screen.findByText('Office 6');
    await act(async () => old.resolve(branch(5, { status: 'Archived' })));
    expect(success).not.toHaveBeenCalled(); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(2);
  });

  it.each(['success', 'failure'])('ownership guard: closing a pending save suppresses its %s UI but refreshes the same registry', async outcome => {
    const old = deferred<CompanyBranch>(); vi.mocked(apiService.createCompanyBranch).mockReturnValueOnce(old.promise);
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); const dialog = newEditor();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' })); fireEvent.click(within(dialog).getByRole('button', { name: 'Закрыть' }));
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.getByRole('button', { name: 'Добавить' })).toHaveProperty('disabled', true);
    await act(async () => outcome === 'success' ? old.resolve(branch()) : old.reject(new Error('Closed editor error')));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Добавить' })).toHaveProperty('disabled', false));
    expect(success).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled(); expect(screen.queryByText('Closed editor error')).toBeNull();
    expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(2);
  });

  it('ownership guard: token refresh and ordinary account rerenders preserve the draft and current save', async () => {
    const saved = branch(5, { id: 51, name: 'Reviewed draft' });
    vi.mocked(apiService.createCompanyBranch).mockResolvedValue(saved);
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); newEditor('Reviewed draft');
    session.refresh(); view.rerender(<CompanyBranchesSection {...props()} />);
    expect(screen.getByDisplayValue('Reviewed draft')).toBeTruthy();
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([saved]); fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить' }));
    await screen.findByText('Reviewed draft'); expect(screen.queryByRole('dialog')).toBeNull(); expect(success).toHaveBeenCalledTimes(1);
    expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(2);
  });

  it('ownership guard: saving locks archive, duplicate save and draft editing until completion', async () => {
    const old = deferred<CompanyBranch>(); vi.mocked(apiService.createCompanyBranch).mockReturnValueOnce(old.promise);
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); const dialog = newEditor(); const save = within(dialog).getByRole('button', { name: 'Сохранить' });
    fireEvent.click(save); fireEvent.click(save); fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    fireEvent.change(within(dialog).getByText('Название').closest('label')!.querySelector('input')!, { target: { value: 'Unsent change' } });
    expect(screen.queryByDisplayValue('Unsent change')).toBeNull(); expect(apiService.createCompanyBranch).toHaveBeenCalledTimes(1); expect(apiService.updateCompanyBranch).not.toHaveBeenCalled();
    await act(async () => old.resolve(branch()));
  });

  it.each(['create-company', 'update-id', 'archive-status'])('ownership guard: a mismatched %s acknowledgement is not successful', async kind => {
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5');
    if (kind === 'create-company') {
      vi.mocked(apiService.createCompanyBranch).mockResolvedValue(branch(6)); fireEvent.click(within(newEditor()).getByRole('button', { name: 'Сохранить' }));
      await screen.findByText(/Не удалось подтвердить сохранение/);
    } else if (kind === 'update-id') {
      vi.mocked(apiService.updateCompanyBranch).mockResolvedValue(branch(5, { id: 77 })); fireEvent.click(screen.getByTitle('Редактировать подразделение'));
      fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить' })); await screen.findByText(/Не удалось подтвердить сохранение/);
    } else {
      vi.mocked(apiService.updateCompanyBranch).mockResolvedValue(branch()); fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
      await waitFor(() => expect(error).toHaveBeenCalled());
    }
    expect(success).not.toHaveBeenCalled(); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(1);
  });

  it('ownership guard: old completion cannot release the next company mutation lock', async () => {
    const first = deferred<CompanyBranch>(); const second = deferred<CompanyBranch>();
    vi.mocked(apiService.createCompanyBranch).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const view = render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); fireEvent.click(within(newEditor()).getByRole('button', { name: 'Сохранить' }));
    view.rerender(<CompanyBranchesSection {...props(6)} />); await screen.findByText('Office 6'); fireEvent.click(within(newEditor('Current draft')).getByRole('button', { name: 'Сохранить' }));
    await act(async () => first.resolve(branch())); expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true);
    await act(async () => second.resolve(branch(6))); expect(success).toHaveBeenCalledTimes(1); expect(apiService.createCompanyBranch).toHaveBeenCalledTimes(2);
  });

  it('branch edit positive: saves existing overrides and preserves signer and notes', async () => {
    const original = branch(5, { kpp: '780201001', signerJson: '{"lastName":"Existing signer"}', notes: 'Existing notes' });
    const saved = { ...original, name: 'Reviewed office', kpp: null };
    vi.mocked(apiService.getCompanyBranches).mockResolvedValueOnce([original]).mockResolvedValue([saved]);
    vi.mocked(apiService.updateCompanyBranch).mockResolvedValue(saved);
    render(<CompanyBranchesSection {...props()} />); await screen.findByText('Office 5'); fireEvent.click(screen.getByTitle('Редактировать подразделение'));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByText('Название').closest('label')!.querySelector('input')!, { target: { value: ' Reviewed office ' } });
    fireEvent.change(within(dialog).getByPlaceholderText('Пусто — КПП компании'), { target: { value: ' ' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' }));
    await screen.findByText('Reviewed office'); expect(screen.queryByRole('dialog')).toBeNull();
    expect(apiService.updateCompanyBranch).toHaveBeenCalledExactlyOnceWith(5, 5, expect.objectContaining({ name: 'Reviewed office', kpp: null, signerJson: original.signerJson, notes: original.notes, status: 'Active' }));
    expect(apiService.createCompanyBranch).not.toHaveBeenCalled(); expect(success).toHaveBeenCalledTimes(1); expect(apiService.getCompanyBranches).toHaveBeenCalledTimes(2);
  });
});
