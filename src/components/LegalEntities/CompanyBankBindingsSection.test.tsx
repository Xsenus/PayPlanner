import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyBankConnectionsSection } from './CompanyBankConnectionsSection';
import { apiService } from '../../services/api';
import { bankConnectionFixture, bankReportFixture, bankVersion, bindingFixture } from '../../test/companyBankBindingFixtures';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { CompanyBankMatchReport } from '../../services/companyBankBindings';

const ui = vi.hoisted(() => ({ confirm: vi.fn(), success: vi.fn(), error: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: ui.confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ui }));
vi.mock('../../services/api', () => ({ apiService: { getBankConnections: vi.fn(), getCompanyBankAccountMatches: vi.fn(), bindCompanyBankAccount: vi.fn(), revokeCompanyBankAccount: vi.fn(), syncBankConnectionAccounts: vi.fn(), syncBankConnectionStatements: vi.fn(), updateBankAccountSettings: vi.fn(), getBankSyncLogs: vi.fn() } }));
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
const props = { companyId: 5, readOnly: false, ownerScope: 'original' };
async function open() { fireEvent.click(await screen.findByRole('button', { name: 'Сверить счета профиля' })); await screen.findByLabelText('Банковский источник'); }
function chooseBank() { fireEvent.click(screen.getByLabelText('Банковский источник')); fireEvent.click(screen.getByRole('option', { name: /^Сбербанк · 40702810200000000001 · RUB · БИК 044525225(?: · источник synthetic)?$/ })); }
function chooseProfile(profileId: string) { fireEvent.click(screen.getByLabelText('Счёт профиля')); fireEvent.click(screen.getByRole('option', { name: `Основной расчётный — ${profileId}` })); }
function choose(profileId = 'profile-A') { chooseBank(); chooseProfile(profileId); }

describe('explicit company bank bindings', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => {
    vi.resetAllMocks(); session = documentCopyTestSession(); ui.confirm.mockResolvedValue(true);
    vi.mocked(apiService.getBankConnections).mockImplementation(async companyId => [bankConnectionFixture(companyId)]);
    vi.mocked(apiService.getCompanyBankAccountMatches).mockImplementation(async companyId => bankReportFixture(companyId));
    vi.mocked(apiService.bindCompanyBankAccount).mockResolvedValue({ bindingId: 77, state: 'Bound', isReplay: false });
    vi.mocked(apiService.revokeCompanyBankAccount).mockResolvedValue({ bindingId: 77, state: 'Revoked', isReplay: false });
  });
  afterEach(() => session.restore());

  it.each(['unique', 'ambiguous'] as const)('requires both explicit choices for %s candidates and refreshes authoritative bindings after confirmation', async kind => {
    const initial = bankReportFixture(); if (kind === 'ambiguous') { initial.groups[0].profileMatchStatus = 'Ambiguous'; initial.groups[0].profileCandidates.push({ id: 'profile-B', name: 'Второй расчётный' }); }
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(initial).mockResolvedValueOnce({ ...bankReportFixture(), version: bankVersion('B'), activeBindings: [bindingFixture()] });
    render(<CompanyBankConnectionsSection {...props} />); await open();
    expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled(); expect(ui.confirm).not.toHaveBeenCalled(); expect(screen.queryByRole('button', { name: 'Подтвердить связь' })).toBeNull();
    chooseBank();
    expect(screen.getByLabelText('Счёт профиля')).toHaveProperty('value', ''); expect(screen.getByRole('button', { name: 'Подтвердить связь' })).toHaveProperty('disabled', true);
    chooseProfile('profile-A'); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
    await screen.findByText('Подтверждённые связи компании (1)');
    expect(apiService.bindCompanyBankAccount).toHaveBeenCalledExactlyOnceWith(11, { bankAccountId: 101, profileAccountId: 'profile-A', expectedVersion: bankVersion() }, expect.any(AbortSignal));
    expect(ui.success).toHaveBeenCalledOnce(); expect(screen.queryByText('Сервер подтвердил действие, но сверку нужно проверить заново. Обновите сверку перед следующим действием.')).toBeNull();
    expect(screen.getByLabelText('Банковский источник')).toHaveProperty('value', ''); expect(apiService.getBankConnections).toHaveBeenCalledTimes(1);
  });

  it('preserves a long case-sensitive selected identity without guessing from its name', async () => {
    const id = 'Case-' + 'x'.repeat(14000); const report = bankReportFixture(); report.groups[0].profileCandidates[0].id = id;
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, activeBindings: [{ ...bindingFixture(), profileAccountId: id }] });
    render(<CompanyBankConnectionsSection {...props} />); await open(); choose(id); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
    await waitFor(() => expect(apiService.bindCompanyBankAccount).toHaveBeenCalledOnce()); expect(vi.mocked(apiService.bindCompanyBankAccount).mock.calls[0][1].profileAccountId).toBe(id);
    await screen.findByText('Подтверждённые связи компании (1)');
  });

  it.each(['row', 'profile'] as const)('does not confirm unknown %s restrictions', async location => {
    const report = bankReportFixture(); if (location === 'row') report.accounts[0].issues = ['FutureRestriction']; else report.profileIssues = ['FutureRestriction'];
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValue(report); render(<CompanyBankConnectionsSection {...props} />); await open(); choose();
    const button = screen.getByRole('button', { name: 'Подтвердить связь' }); expect(button).toHaveProperty('disabled', true); fireEvent.click(button); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled();
  });

  it('allows explicit duplicate-source/manual-candidate review without merging a money account', async () => {
    const report = bankReportFixture(); report.accounts[0].issues = ['MultipleBankSources', 'ManualMoneyCandidatesRequireReview']; report.groups[0].bankAccountIds.push(102); report.groups[0].manualMoneyAccountCandidateIds = [88]; report.accounts[0].linkedMoneyAccountIds = [];
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, activeBindings: [bindingFixture()] });
    render(<CompanyBankConnectionsSection {...props} />); await open(); choose(); expect(screen.getByRole('button', { name: 'Подтвердить связь' })).toHaveProperty('disabled', false);
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' })); await screen.findByText('Подтверждённые связи компании (1)');
    expect(Object.keys(vi.mocked(apiService.bindCompanyBankAccount).mock.calls[0][1]).sort()).toEqual(['bankAccountId', 'expectedVersion', 'profileAccountId']);
  });

  it.each(['readonly', 'draft'] as const)('permits reading but denies both commands in %s scope', async mode => {
    const report = { ...bankReportFixture(), activeBindings: [bindingFixture()] }; vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValue(report);
    render(<CompanyBankConnectionsSection {...props} readOnly={mode === 'readonly'} profileDirty={mode === 'draft'} />); await open();
    const button = screen.getByRole('button', { name: 'Снять связь' }); expect(button).toHaveProperty('disabled', true); fireEvent.click(button);
    expect(apiService.revokeCompanyBankAccount).not.toHaveBeenCalled(); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled(); expect(ui.confirm).not.toHaveBeenCalled();
  });

  it('shows cleanup for an archived/former own profile and revokes with its fresh version', async () => {
    const report = { ...bankReportFixture(), canBind: false, profileIssues: ['CompanyUnavailable', 'CompanyNotOwn'], groups: [], accounts: [], activeBindings: [bindingFixture()] };
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, version: bankVersion('B'), activeBindings: [] });
    render(<CompanyBankConnectionsSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Сверить счета профиля' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Снять связь' })); await screen.findByText('Подтверждённые связи компании (0)');
    expect(apiService.revokeCompanyBankAccount).toHaveBeenCalledExactlyOnceWith(11, 77, bankVersion(), expect.any(AbortSignal)); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled();
  });

  it('shares one operation with ordinary bank writes and cancels a confirmation without reloading or posting', async () => {
    const confirm = deferred<boolean>(); ui.confirm.mockReturnValue(confirm.promise); render(<CompanyBankConnectionsSection {...props} />); await open(); choose();
    const button = screen.getByRole('button', { name: 'Подтвердить связь' }); fireEvent.click(button); fireEvent.click(button); fireEvent.click(screen.getByRole('button', { name: 'Обновить счета' }));
    expect(ui.confirm).toHaveBeenCalledTimes(1); expect(apiService.syncBankConnectionAccounts).not.toHaveBeenCalled();
    await act(async () => confirm.resolve(false)); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled(); expect(apiService.getCompanyBankAccountMatches).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText('Счёт профиля')).toHaveProperty('value', 'profile-A');
  });

  it('clears the old version after a failed post and requires a fresh explicit choice', async () => {
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(bankReportFixture()).mockResolvedValueOnce({ ...bankReportFixture(), version: bankVersion('B') })
      .mockResolvedValueOnce({ ...bankReportFixture(), version: bankVersion('C'), activeBindings: [bindingFixture()] });
    vi.mocked(apiService.bindCompanyBankAccount).mockRejectedValueOnce(new Error('HTTP 409 Conflict: changed')).mockResolvedValueOnce({ bindingId: 77, state: 'Bound', isReplay: false });
    render(<CompanyBankConnectionsSection {...props} />); await open(); choose(); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
    await screen.findByText('Данные или результат действия требуют сверки. Обновите сверку перед следующим действием.');
    expect(screen.queryByLabelText('Счёт профиля')).toBeNull(); expect(apiService.bindCompanyBankAccount).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить сверку счетов' })); await screen.findByLabelText('Банковский источник'); choose(); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
    await screen.findByText('Подтверждённые связи компании (1)'); expect(vi.mocked(apiService.bindCompanyBankAccount).mock.calls.map(x => x[1].expectedVersion)).toEqual([bankVersion(), bankVersion('B')]);
  });

  type Scope = 'company' | 'readonly' | 'authority' | 'draft' | 'session' | 'unmount';
  const scopes: Scope[] = ['company', 'readonly', 'authority', 'draft', 'session', 'unmount'];
  function replace(view: ReturnType<typeof render>, scope: Scope) {
    if (scope === 'unmount') view.unmount(); else if (scope === 'session') { session.replace(); view.rerender(<CompanyBankConnectionsSection {...props} />); }
    else view.rerender(<CompanyBankConnectionsSection {...props} {...(scope === 'company' ? { companyId: 6 } : scope === 'readonly' ? { readOnly: true } : scope === 'draft' ? { profileDirty: true } : { ownerScope: 'new-rights' })} />);
  }
  for (const scope of scopes) {
    it.each(['success', 'failure'] as const)(`pending read cannot disclose data or errors after ${scope}: %s`, async result => {
      const pending = deferred<CompanyBankMatchReport>(); vi.mocked(apiService.getCompanyBankAccountMatches).mockReturnValue(pending.promise);
      const view = render(<CompanyBankConnectionsSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Сверить счета профиля' }));
      const signal = vi.mocked(apiService.getCompanyBankAccountMatches).mock.calls[0][2]!; replace(view, scope); expect(signal.aborted).toBe(true);
      await act(async () => { if (result === 'success') pending.resolve(bankReportFixture()); else pending.reject(new Error('Private old bank failure')); });
      expect(screen.queryByLabelText('Счёт профиля')).toBeNull(); expect(screen.queryByText(/Private old bank failure/)).toBeNull(); expect(ui.error).not.toHaveBeenCalled();
    });
    it(`pending confirmation cannot dispatch after ${scope}`, async () => {
      const confirm = deferred<boolean>(); ui.confirm.mockReturnValue(confirm.promise); const view = render(<CompanyBankConnectionsSection {...props} />); await open(); choose(); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
      const signal = ui.confirm.mock.calls[0][0].signal as AbortSignal; replace(view, scope); expect(signal.aborted).toBe(true);
      await act(async () => confirm.resolve(true)); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled(); expect(ui.success).not.toHaveBeenCalled(); expect(ui.error).not.toHaveBeenCalled();
    });
    it.each(['success', 'failure'] as const)(`pending post cannot notify, adopt or reload after ${scope}: %s`, async result => {
      const pending = deferred<Awaited<ReturnType<typeof apiService.bindCompanyBankAccount>>>(); vi.mocked(apiService.bindCompanyBankAccount).mockReturnValue(pending.promise);
      const view = render(<CompanyBankConnectionsSection {...props} />); await open(); choose(); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
      await waitFor(() => expect(apiService.bindCompanyBankAccount).toHaveBeenCalledOnce()); replace(view, scope);
      await act(async () => { if (result === 'success') pending.resolve({ bindingId: 77, state: 'Bound', isReplay: false }); else pending.reject(new Error('Private old bank failure')); });
      expect(apiService.getCompanyBankAccountMatches).toHaveBeenCalledTimes(1); expect(ui.success).not.toHaveBeenCalled(); expect(ui.error).not.toHaveBeenCalled();
    });
  }

  it('changing session without rendering prevents a stale outgoing command', async () => {
    render(<CompanyBankConnectionsSection {...props} />); await open(); choose(); session.replace(); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' })); expect(ui.confirm).not.toHaveBeenCalled(); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled();
  });

  it('reloading connection data discards an older report read', async () => {
    const pending = deferred<CompanyBankMatchReport>(); vi.mocked(apiService.getCompanyBankAccountMatches).mockReturnValue(pending.promise);
    render(<CompanyBankConnectionsSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Сверить счета профиля' }));
    fireEvent.click(screen.getByRole('button', { name: 'Перезагрузить данные подключений' })); await waitFor(() => expect(apiService.getBankConnections).toHaveBeenCalledTimes(2));
    await act(async () => pending.resolve({ ...bankReportFixture(), activeBindings: [bindingFixture()] })); expect(screen.queryByText('Подтверждённые связи компании (1)')).toBeNull();
  });

  it('displays and confirms source requisites from the versioned report, not an earlier connection list', async () => {
    const report = bankReportFixture(); const source = report.accounts[0];
    source.accountNumber = '40702810200000000002'; source.provider = 'Tochka'; source.externalAccountId = 'current-source';
    report.groups[0].profileCandidates[0].name = 'Текущий счёт';
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, activeBindings: [{ ...bindingFixture(), accountNumber: source.accountNumber }] });
    render(<CompanyBankConnectionsSection {...props} />); await open(); fireEvent.click(screen.getByLabelText('Банковский источник'));
    fireEvent.click(screen.getByRole('option', { name: 'Точка · 40702810200000000002 · RUB · БИК 044525225' }));
    fireEvent.click(screen.getByLabelText('Счёт профиля')); fireEvent.click(screen.getByRole('option', { name: 'Текущий счёт — profile-A' }));
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' })); await screen.findByText('Подтверждённые связи компании (1)');
    expect(ui.confirm.mock.calls[0][0].description).toContain('40702810200000000002'); expect(ui.confirm.mock.calls[0][0].description).not.toContain('40702810200000000001');
    expect(apiService.bindCompanyBankAccount).toHaveBeenCalledWith(11, { bankAccountId: 101, profileAccountId: 'profile-A', expectedVersion: report.version }, expect.any(AbortSignal));
  });

  it('uses newly reported sources and distinguishes equal account numbers by the chosen bank ID', async () => {
    const report = bankReportFixture(); report.accounts.push({ ...report.accounts[0], bankAccountId: 102, externalAccountId: 'second-source' }); report.groups[0].bankAccountIds.push(102);
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, activeBindings: [{ ...bindingFixture(), bankAccountId: 102 }] });
    render(<CompanyBankConnectionsSection {...props} />); await open(); fireEvent.click(screen.getByLabelText('Банковский источник'));
    expect(screen.getByRole('option', { name: /источник synthetic$/ })).toBeTruthy(); fireEvent.click(screen.getByRole('option', { name: /источник second-source$/ }));
    chooseProfile('profile-A'); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' })); await screen.findByText('Подтверждённые связи компании (1)');
    expect(apiService.bindCompanyBankAccount).toHaveBeenCalledWith(11, { bankAccountId: 102, profileAccountId: 'profile-A', expectedVersion: report.version }, expect.any(AbortSignal));
  });

  it('keeps confirmation lists bounded in the DOM', async () => {
    const report = { ...bankReportFixture(), activeBindings: Array.from({ length: 21 }, (_, i) => ({ ...bindingFixture(), id: i + 1, bankAccountId: i + 1 })) };
    vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValue(report); render(<CompanyBankConnectionsSection {...props} />); await open();
    const region = within(screen.getByTestId('company-bank-bindings-11')); expect(region.getAllByRole('button', { name: 'Снять связь' })).toHaveLength(20);
    fireEvent.click(region.getByRole('button', { name: 'Показать ещё связи' })); expect(region.getAllByRole('button', { name: 'Снять связь' })).toHaveLength(21);
  });
});
