import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyProfile } from './CompanyProfile';
import TopBar from '../TopBar/TopBar';
import { canAccessTab } from '../../appRouting';
import { defaultRestrictedRolePermissions } from '../../types/permissions';
import { apiService } from '../../services/api';

const fixture = vi.hoisted(() => ({ admin: false, view: false, edit: false, modal: vi.fn(), navigate: vi.fn() }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 3, role: { id: 4 } }, isAdmin: () => fixture.admin }) }));
vi.mock('../../contexts/TabContext', () => ({ useTabNavigation: () => ({ setActiveTab: fixture.navigate }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ companyProfile: { canView: fixture.view, canEdit: fixture.edit } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: vi.fn() }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('../LegalEntities/LegalEntityModal', () => ({ LegalEntityModal: (props: { initial: { id: number; shortName: string }; allowEdit: boolean; onSubmit: (payload: { shortName: string }) => Promise<unknown> }) => {
  fixture.modal(props);
  return <div><span>{props.initial.shortName}</span>{props.allowEdit ? <button onClick={() => void props.onSubmit({ shortName: 'Changed' })}>Save fixture</button> : null}</div>;
} }));
vi.mock('../../services/api', () => ({ apiService: { getMainCompanySettings: vi.fn(), getLegalEntity: vi.fn(), updateLegalEntity: vi.fn(), suggestLegalEntities: vi.fn(), findLegalEntityById: vi.fn(), getUnreadUserNotificationCount: vi.fn() } }));
const api = vi.mocked(apiService);
const company = { id: 7, shortName: 'Assigned company', clients: [], clientsCount: 0, createdAt: '2026-10-23T00:00:00Z' };

describe('company profile access boundaries', () => {
  beforeEach(() => {
    vi.clearAllMocks(); window.localStorage.clear(); fixture.admin = false; fixture.view = false; fixture.edit = false;
    api.getMainCompanySettings.mockResolvedValue({ defaultCompanyId: 7, companies: [company] });
    api.getLegalEntity.mockResolvedValue(company); api.updateLegalEntity.mockResolvedValue({ ...company, shortName: 'Changed' });
    api.getUnreadUserNotificationCount.mockResolvedValue(0);
  });

  it.each([[false, false], [false, true]])('denies direct component entry without view even when edit=%s', async (view, edit) => {
    fixture.view = view; fixture.edit = edit;
    window.localStorage.setItem('pp.companyProfileMode', 'edit');
    render(<CompanyProfile />);
    expect(screen.getByText('Нет разрешения на просмотр профиля компании.')).toBeTruthy();
    expect(api.getMainCompanySettings).not.toHaveBeenCalled(); expect(api.getLegalEntity).not.toHaveBeenCalled();
  });

  it('keeps a viewer read-only even if the stored mode requests editing', async () => {
    fixture.view = true; window.localStorage.setItem('pp.companyProfileMode', 'edit');
    render(<CompanyProfile />); await screen.findByText('Assigned company', { selector: 'span' });
    expect(fixture.modal.mock.lastCall?.[0].allowEdit).toBe(false);
    expect(screen.queryByRole('button', { name: 'Редактировать профиль' })).toBeNull();
    expect(api.updateLegalEntity).not.toHaveBeenCalled();
  });

  it('allows a granted editor to edit the assigned profile', async () => {
    fixture.view = true; fixture.edit = true; render(<CompanyProfile />);
    await screen.findByText('Assigned company', { selector: 'span' });
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать профиль' }));
    expect(fixture.modal.mock.lastCall?.[0]).toEqual(expect.objectContaining({
      canManageOwnCompanySettings: false, canViewAccounts: false, canCreateAccounts: false,
      canViewActs: false, canViewPayments: false, canViewCases: false,
      relatedRecordPermissions: { companyProfile: { canView: true, canEdit: true } },
    }));
    fireEvent.click(screen.getByRole('button', { name: 'Save fixture' }));
    await waitFor(() => expect(api.updateLegalEntity).toHaveBeenCalledWith(7, { shortName: 'Changed' }));
  });

  it('returns a delegated editor to viewing without opening administrator settings', async () => {
    fixture.view = true; fixture.edit = true; render(<CompanyProfile />);
    await screen.findByText('Assigned company', { selector: 'span' });
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать профиль' }));
    act(() => fixture.modal.mock.lastCall?.[0].onBack());
    expect(fixture.modal.mock.lastCall?.[0].allowEdit).toBe(false);
    expect(fixture.navigate).not.toHaveBeenCalled();
  });

  it('ignores a delayed save after switching to another company', async () => {
    fixture.view = true; fixture.edit = true;
    const secondCompany = { ...company, id: 8, shortName: 'Second company' };
    api.getMainCompanySettings.mockResolvedValue({ defaultCompanyId: 7, companies: [company, secondCompany] });
    api.getLegalEntity.mockImplementation(async (id) => id === 8 ? secondCompany : company);
    let finishSave!: (value: typeof company) => void;
    api.updateLegalEntity.mockReturnValueOnce(new Promise((resolve) => { finishSave = resolve; }));
    render(<CompanyProfile />);
    await screen.findByText('Assigned company', { selector: 'span' });
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать профиль' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save fixture' }));
    await waitFor(() => expect(api.updateLegalEntity).toHaveBeenCalledWith(7, { shortName: 'Changed' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Компания профиля' }), { target: { value: '8' } });
    await screen.findByText('Second company', { selector: 'span' });
    await act(async () => finishSave({ ...company, shortName: 'Late first-company save' }));

    expect(screen.getByRole('combobox', { name: 'Компания профиля' })).toHaveProperty('value', '8');
    expect(screen.getByText('Second company', { selector: 'span' })).toBeTruthy();
    expect(screen.queryByText('Late first-company save', { selector: 'span' })).toBeNull();
    api.updateLegalEntity.mockResolvedValueOnce({ ...secondCompany, shortName: 'Second company saved' });
    fireEvent.click(screen.getByRole('button', { name: 'Save fixture' }));
    await waitFor(() => expect(api.updateLegalEntity).toHaveBeenLastCalledWith(8, { shortName: 'Changed' }));
  });

  it.each(['view', 'edit'] as const)('ignores a delayed save after %s permission was revoked and restored', async (permission) => {
    fixture.view = true; fixture.edit = true;
    let finishSave!: (value: typeof company) => void;
    api.updateLegalEntity.mockReturnValueOnce(new Promise((resolve) => { finishSave = resolve; }));
    const view = render(<CompanyProfile />);
    await screen.findByText('Assigned company', { selector: 'span' });
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать профиль' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save fixture' }));
    await waitFor(() => expect(api.updateLegalEntity).toHaveBeenCalledTimes(1));
    fixture[permission] = false; view.rerender(<CompanyProfile />);
    fixture[permission] = true; view.rerender(<CompanyProfile />);
    await screen.findByText('Assigned company', { selector: 'span' });
    await act(async () => finishSave({ ...company, shortName: 'Late revoked-context save' }));

    expect(screen.getByText('Assigned company', { selector: 'span' })).toBeTruthy();
    expect(screen.queryByText('Late revoked-context save', { selector: 'span' })).toBeNull();
  });

  it('rejects a stored foreign company and loads only the allowed fallback', async () => {
    fixture.view = true; window.localStorage.setItem('pp.companyProfileSelectedCompanyId', '999');
    render(<CompanyProfile />); await screen.findByText('Assigned company', { selector: 'span' });
    expect(api.getLegalEntity).toHaveBeenCalledWith(7); expect(api.getLegalEntity).not.toHaveBeenCalledWith(999);
  });

  it('keeps the existing owner access', async () => {
    fixture.admin = true; render(<CompanyProfile />); await screen.findByText('Assigned company', { selector: 'span' });
    expect(screen.getByRole('button', { name: 'Редактировать профиль' })).toBeTruthy();
    expect(fixture.modal.mock.lastCall?.[0].relatedRecordPermissions).toBeUndefined();
  });

  it('retains the selected assigned company while the list is loading', async () => {
    fixture.view = true; window.localStorage.setItem('pp.companyProfileSelectedCompanyId', '7');
    api.getMainCompanySettings.mockResolvedValue({ defaultCompanyId: 8, companies: [company, { ...company, id: 8 }] });
    render(<CompanyProfile />); await screen.findByText('Assigned company', { selector: 'span' });
    expect(api.getLegalEntity).toHaveBeenCalledWith(7); expect(api.getLegalEntity).not.toHaveBeenCalledWith(8);
  });

  it('clears displayed data and editing immediately after permission revocation', async () => {
    fixture.view = true; fixture.edit = true; const view = render(<CompanyProfile />);
    await screen.findByText('Assigned company', { selector: 'span' });
    fixture.view = false; fixture.edit = false; view.rerender(<CompanyProfile />);
    expect(screen.queryByText('Assigned company', { selector: 'span' })).toBeNull();
    expect(screen.getByText('Нет разрешения на просмотр профиля компании.')).toBeTruthy();
  });

  it('ignores a profile response arriving after revocation', async () => {
    fixture.view = true; let finish!: (value: typeof company) => void;
    api.getLegalEntity.mockReturnValue(new Promise((resolve) => { finish = resolve; }));
    const view = render(<CompanyProfile />); await waitFor(() => expect(api.getLegalEntity).toHaveBeenCalledWith(7));
    fixture.view = false; view.rerender(<CompanyProfile />); await act(async () => finish(company));
    expect(screen.queryByText('Assigned company', { selector: 'span' })).toBeNull();
  });

  it.each([false, true])('shows the company menu only with view permission=%s', async (view) => {
    fixture.view = view;
    render(<TopBar title="CRM" onSignOut={vi.fn()} onOpenProfile={vi.fn()} onOpenNotifications={vi.fn()} onOpenAppVersion={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Меню пользователя' }));
    expect(Boolean(screen.queryByRole('button', { name: 'Профиль компании' }))).toBe(view);
    if (view) { fireEvent.click(screen.getByRole('button', { name: 'Профиль компании' })); expect(fixture.navigate).toHaveBeenCalledWith('companyProfile'); }
  });

  it('protects direct routing separately from legal-entity and financial permissions', () => {
    const permissions = { ...defaultRestrictedRolePermissions, legalEntities: { ...defaultRestrictedRolePermissions.legalEntities, canView: true, canEdit: true }, accounts: { ...defaultRestrictedRolePermissions.accounts, canView: true } };
    expect(canAccessTab('companyProfile', permissions)).toBe(false);
    expect(canAccessTab('accounts', permissions)).toBe(true);
    expect(canAccessTab('companyProfile', { ...permissions, companyProfile: { ...permissions.companyProfile, canView: true } })).toBe(true);
    expect(canAccessTab('companyProfile', permissions, { isAdminRole: true })).toBe(true);
  });
});
