import { StrictMode } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyProfile } from './CompanyProfile';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import type { LegalEntityDetail, LegalEntityInput } from '../../types';

const fixture = vi.hoisted(() => ({ userId: 3, roleId: 4, admin: false, view: true, edit: true, scope: 'all', modal: vi.fn(), toast: vi.fn(), navigate: vi.fn() }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: fixture.userId, role: { id: fixture.roleId } }, isAdmin: () => fixture.admin }) }));
vi.mock('../../contexts/TabContext', () => ({ useTabNavigation: () => ({ setActiveTab: fixture.navigate }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ companyProfile: { canView: fixture.view, canEdit: fixture.edit, viewScope: fixture.scope } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: fixture.toast }) }));
vi.mock('../LegalEntities/LegalEntityModal', () => ({ LegalEntityModal: (props: { initial: LegalEntityDetail; onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail>; onBack?: () => void }) => {
  fixture.modal(props); return <span data-testid="profile-name">{props.initial.shortName}</span>;
} }));
vi.mock('../../services/api', () => ({ apiService: { getMainCompanySettings: vi.fn(), getLegalEntity: vi.fn(), updateLegalEntity: vi.fn(), suggestLegalEntities: vi.fn(), findLegalEntityById: vi.fn() } }));

const api = vi.mocked(apiService);
const company = { id: 7, shortName: 'Company seven', clients: [], clientsCount: 0, createdAt: '2026-10-30T00:00:00Z' };
const second = { ...company, id: 8, shortName: 'Company eight' };
const payload = { shortName: 'Changed' };
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const modal = () => fixture.modal.mock.lastCall![0] as { initial: LegalEntityDetail; onSubmit: (value: typeof payload) => Promise<unknown>; onBack: () => void };
const change = (kind: string) => {
  if (kind === 'user') fixture.userId++;
  if (kind === 'role') fixture.roleId++;
  if (kind === 'scope') fixture.scope = 'own';
  if (kind === 'edit') fixture.edit = false;
  if (kind === 'admin') fixture.admin = false;
};

describe('company profile ownership', () => {
  beforeEach(() => {
    vi.clearAllMocks(); localStorage.clear();
    Object.assign(fixture, { userId: 3, roleId: 4, admin: false, view: true, edit: true, scope: 'all' });
    localStorage.setItem('pp.companyProfileMode', 'edit');
    api.getMainCompanySettings.mockReset().mockResolvedValue({ defaultCompanyId: 7, companies: [company] });
    api.getLegalEntity.mockReset().mockImplementation(async id => id === 8 ? second : company);
    api.updateLegalEntity.mockReset().mockResolvedValue({ ...company, shortName: 'Changed' });
  });

  it.each(['user', 'role', 'scope', 'edit', 'admin'])('replaces the displayed profile and catalogue on %s change', async kind => {
    if (kind === 'admin') fixture.admin = true;
    const view = render(<CompanyProfile />); await screen.findByTestId('profile-name');
    const pending = deferred<Awaited<ReturnType<typeof api.getMainCompanySettings>>>();
    api.getMainCompanySettings.mockReturnValueOnce(pending.promise); change(kind); view.rerender(<CompanyProfile />);
    expect(screen.queryByText(company.shortName, { selector: 'span' })).toBeNull();
    expect(api.getMainCompanySettings).toHaveBeenCalledTimes(2);
    await act(async () => pending.resolve({ defaultCompanyId: 8, companies: [second] }));
    expect((await screen.findByTestId('profile-name')).textContent).toBe(second.shortName);
    expect(api.getLegalEntity).toHaveBeenLastCalledWith(8);
  });

  it.each(['success', 'error'])('ignores a former user catalogue %s and keeps the new profile', async result => {
    const pending = deferred<Awaited<ReturnType<typeof api.getMainCompanySettings>>>();
    api.getMainCompanySettings.mockReturnValueOnce(pending.promise).mockResolvedValueOnce({ defaultCompanyId: 8, companies: [second] });
    const view = render(<CompanyProfile />); fixture.userId++; view.rerender(<CompanyProfile />);
    await waitFor(() => expect(api.getMainCompanySettings).toHaveBeenCalledTimes(2));
    await screen.findByText(second.shortName, { selector: 'span' });
    await act(async () => { if (result === 'success') pending.resolve({ defaultCompanyId: 7, companies: [company] }); else pending.reject(new Error('old catalogue error')); });
    expect(screen.getByTestId('profile-name').textContent).toBe(second.shortName);
    expect(screen.queryByText('old catalogue error')).toBeNull(); expect(api.getLegalEntity).not.toHaveBeenCalledWith(7);
  });

  it.each(['success', 'error'])('ignores the former user profile %s', async result => {
    const pending = deferred<LegalEntityDetail>(); api.getLegalEntity.mockReturnValueOnce(pending.promise);
    const view = render(<CompanyProfile />); await waitFor(() => expect(api.getLegalEntity).toHaveBeenCalledWith(7));
    api.getMainCompanySettings.mockResolvedValueOnce({ defaultCompanyId: 8, companies: [second] }); fixture.userId++; view.rerender(<CompanyProfile />);
    await waitFor(() => expect(api.getMainCompanySettings).toHaveBeenCalledTimes(2)); await screen.findByText(second.shortName, { selector: 'span' });
    await act(async () => { if (result === 'success') pending.resolve(company); else pending.reject(new Error('old profile error')); });
    expect(screen.getByTestId('profile-name').textContent).toBe(second.shortName); expect(screen.queryByText('old profile error')).toBeNull();
  });

  it.each(['catalogue', 'profile'])('does not adopt late %s after login replacement before rerender', async source => {
    const session = documentCopyTestSession();
    try {
      const catalog = deferred<Awaited<ReturnType<typeof api.getMainCompanySettings>>>(); const detail = deferred<LegalEntityDetail>();
      if (source === 'catalogue') api.getMainCompanySettings.mockReturnValueOnce(catalog.promise); else api.getLegalEntity.mockReturnValueOnce(detail.promise);
      render(<CompanyProfile />);
      await waitFor(() => expect(source === 'catalogue' ? api.getMainCompanySettings : api.getLegalEntity).toHaveBeenCalledOnce());
      session.replace();
      await act(async () => { if (source === 'catalogue') catalog.resolve({ defaultCompanyId: 7, companies: [company] }); else detail.resolve(company); });
      expect(screen.queryByTestId('profile-name')).toBeNull();
      if (source === 'catalogue') expect(api.getLegalEntity).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  it.each(['user', 'role', 'scope', 'edit', 'back', 'login', 'unmount'])('refuses an old save callback before a request after %s', async kind => {
    const session = documentCopyTestSession();
    try {
      const view = render(<CompanyProfile />); await screen.findByTestId('profile-name'); const former = modal();
      if (kind === 'login') session.replace(); else if (kind === 'back') act(() => former.onBack()); else if (kind === 'unmount') view.unmount(); else { change(kind); view.rerender(<CompanyProfile />); }
      await expect(former.onSubmit(payload)).rejects.toBeInstanceOf(StaleUiOperationError);
      expect(api.updateLegalEntity).not.toHaveBeenCalled(); expect(fixture.toast).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  it.each(['success', 'error'])('rejects a late save %s after back without returning a DTO to the old form', async result => {
    const pending = deferred<LegalEntityDetail>(); api.updateLegalEntity.mockReturnValueOnce(pending.promise);
    render(<CompanyProfile />); await screen.findByTestId('profile-name'); const former = modal();
    const returned = former.onSubmit(payload); const rejection = expect(returned).rejects.toBeInstanceOf(StaleUiOperationError);
    act(() => former.onBack());
    await act(async () => { if (result === 'success') pending.resolve({ ...company, shortName: 'old saved profile' }); else pending.reject(new Error('old save error')); });
    await rejection; expect(fixture.toast).not.toHaveBeenCalled(); expect(screen.queryByText('old saved profile')).toBeNull();
  });

  it('rejects a saved DTO for a different company', async () => {
    api.updateLegalEntity.mockResolvedValueOnce(second); render(<CompanyProfile />); await screen.findByTestId('profile-name');
    await expect(modal().onSubmit(payload)).rejects.toThrow('другой профиль'); expect(fixture.toast).not.toHaveBeenCalled();
  });

  it.each([false, true])('selects only an assigned fallback when an unavailable default exists and empty=%s', async empty => {
    api.getMainCompanySettings.mockResolvedValueOnce({ defaultCompanyId: 999, companies: empty ? [] : [company] });
    render(<CompanyProfile />); await waitFor(() => expect(screen.queryByText('Загружаем профиль компании...')).toBeNull());
    expect(api.getLegalEntity).not.toHaveBeenCalledWith(999);
    if (empty) expect(api.getLegalEntity).not.toHaveBeenCalled(); else expect((await screen.findByTestId('profile-name')).textContent).toBe(company.shortName);
  });

  it('keeps one read per profile through unrelated rerender and valid session refresh', async () => {
    const session = documentCopyTestSession();
    try {
      const view = render(<CompanyProfile />); await screen.findByTestId('profile-name'); const save = modal().onSubmit;
      session.refresh(); view.rerender(<CompanyProfile />);
      expect(api.getMainCompanySettings).toHaveBeenCalledOnce(); expect(api.getLegalEntity).toHaveBeenCalledOnce();
      await act(async () => { await expect(save(payload)).resolves.toEqual(expect.objectContaining({ id: 7, shortName: 'Changed' })); });
      expect(fixture.toast).toHaveBeenCalledOnce(); expect(api.updateLegalEntity).toHaveBeenCalledExactlyOnceWith(7, payload);
    } finally { session.restore(); }
  });

  it('preserves a current save error for its form', async () => {
    const failure = new Error('current save error'); api.updateLegalEntity.mockRejectedValueOnce(failure);
    render(<CompanyProfile />); await screen.findByTestId('profile-name'); await expect(modal().onSubmit(payload)).rejects.toBe(failure);
    expect(fixture.toast).not.toHaveBeenCalled(); expect(screen.getByTestId('profile-name').textContent).toBe(company.shortName);
  });

  it.each(['success', 'error'])('profile final boundary rejects a pending save %s after a replacement login before rerender', async result => {
    const session = documentCopyTestSession();
    try {
      const pending = deferred<LegalEntityDetail>(); api.updateLegalEntity.mockReturnValueOnce(pending.promise);
      render(<CompanyProfile />); await screen.findByTestId('profile-name'); const returned = modal().onSubmit(payload);
      const rejection = expect(returned).rejects.toBeInstanceOf(StaleUiOperationError); session.replace();
      await act(async () => { if (result === 'success') pending.resolve({ ...company, shortName: 'former login saved' }); else pending.reject(new Error('former login save error')); });
      await rejection; expect(fixture.toast).not.toHaveBeenCalled(); expect(screen.getByTestId('profile-name').textContent).toBe(company.shortName);
    } finally { session.restore(); }
  });

  it('profile final boundary does not revive a callback when the original user returns', async () => {
    const view = render(<CompanyProfile />); await screen.findByTestId('profile-name'); const former = modal().onSubmit;
    fixture.userId++; view.rerender(<CompanyProfile />); await screen.findByTestId('profile-name');
    fixture.userId--; view.rerender(<CompanyProfile />); await screen.findByTestId('profile-name');
    await expect(former(payload)).rejects.toBeInstanceOf(StaleUiOperationError); expect(api.updateLegalEntity).not.toHaveBeenCalled();
    expect(api.getMainCompanySettings).toHaveBeenCalledTimes(3);
  });

  it('profile final boundary invalidates before administrator navigation and rejects an old readonly callback', async () => {
    fixture.admin = true; const view = render(<CompanyProfile />); await screen.findByTestId('profile-name'); const former = modal();
    let returned: Promise<unknown> | undefined;
    fixture.navigate.mockImplementationOnce(() => { returned = former.onSubmit(payload); });
    act(() => former.onBack()); expect(fixture.navigate).toHaveBeenCalledWith('settings');
    await expect(returned).rejects.toBeInstanceOf(StaleUiOperationError); expect(api.updateLegalEntity).not.toHaveBeenCalled();
    fixture.admin = false; fixture.edit = false; view.rerender(<CompanyProfile />); await screen.findByTestId('profile-name'); const readonly = modal().onSubmit;
    fixture.userId++; view.rerender(<CompanyProfile />); await screen.findByTestId('profile-name');
    await expect(readonly(payload)).rejects.toBeInstanceOf(StaleUiOperationError);
  });

  it('profile final boundary stays usable after StrictMode cleanup and ignores its earlier read', async () => {
    const pending = deferred<Awaited<ReturnType<typeof api.getMainCompanySettings>>>();
    api.getMainCompanySettings.mockReturnValueOnce(pending.promise);
    render(<StrictMode><CompanyProfile /></StrictMode>); await screen.findByTestId('profile-name');
    await act(async () => pending.resolve({ defaultCompanyId: 8, companies: [second] }));
    expect(api.getLegalEntity).toHaveBeenCalledExactlyOnceWith(7);
    await act(async () => { await expect(modal().onSubmit(payload)).resolves.toEqual(expect.objectContaining({id:7})); });
    expect(fixture.toast).toHaveBeenCalledOnce(); expect(api.getMainCompanySettings).toHaveBeenCalledTimes(2);
  });
});
