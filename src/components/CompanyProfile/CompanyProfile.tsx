import { AlertCircle, Building2, Loader2, Settings2 } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useToast } from '../../hooks/useToast';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { apiService, type MainCompanySettingsDto } from '../../services/api';
import type { LegalEntityDetail, LegalEntityInput } from '../../types';
import {
  getStoredCompanyProfileCompanyId,
  getStoredCompanyProfileMode,
  setStoredCompanyProfileCompanyId,
  type CompanyProfileMode,
} from '../../utils/companyProfileBridge';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { LegalEntityModal } from '../LegalEntities/LegalEntityModal';
import { useAsyncOperationOwner } from '../../hooks/useAsyncOperationOwner';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent, type AuthSessionIdentity } from '../../services/authService';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';

export function CompanyProfile() {
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const isAdminUser = isAdmin();
  const canView = isAdminUser || permissions.companyProfile?.canView === true;
  const canEdit = canView && (isAdminUser || permissions.companyProfile?.canEdit === true);
  const { epoch, actor, authenticated } = captureAuthSessionIdentity();
  const session = useMemo(() => ({ epoch, actor, authenticated }), [epoch, actor, authenticated]);
  const scope = JSON.stringify([user?.id, user?.role?.id, isAdminUser, canView, canEdit, permissions.companyProfile?.viewScope, session]);
  return <CompanyProfileScreen key={scope} userId={user?.id} roleId={user?.role?.id}
    permissions={permissions} isAdminUser={isAdminUser} canView={canView} canEdit={canEdit} session={session} />;
}

function CompanyProfileScreen({ userId, roleId, permissions, isAdminUser, canView, canEdit, session }: {
  userId?: number; roleId?: number; permissions: ReturnType<typeof useRolePermissions>;
  isAdminUser: boolean; canView: boolean; canEdit: boolean; session: AuthSessionIdentity;
}) {
  const { setActiveTab } = useTabNavigation();
  const toast = useToast();

  const [settings, setSettings] = useState<MainCompanySettingsDto | null>(null);
  const [settingsLoading, setSettingsLoading] = useState(true);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [selectedCompanyId, setSelectedCompanyId] = useState('');
  const [profileMode, setProfileMode] = useState<CompanyProfileMode>(() => getStoredCompanyProfileMode() ?? 'view');

  const [profile, setProfile] = useState<LegalEntityDetail | null>(null);
  const [profileLoading, setProfileLoading] = useState(false);
  const [profileError, setProfileError] = useState<string | null>(null);
  const settingsRequest = useRef(0);
  const profileRequest = useRef(0);
  const { capture, isCurrent, invalidate } = useAsyncOperationOwner(JSON.stringify([selectedCompanyId, profile?.id, profileMode]));
  const renderedOwner = capture();

  const loadSettings = useCallback(async () => {
    if (!isAuthSessionIdentityCurrent(session)) return;
    const request = ++settingsRequest.current;
    const current = () => request === settingsRequest.current && isAuthSessionIdentityCurrent(session);
    try {
      setSettingsLoading(true);
      setSettingsError(null);

      const response = await apiService.getMainCompanySettings();
      if (!current()) return;
      setSettings(response);

      const storedCompanyId = getStoredCompanyProfileCompanyId();
      const fallbackCompanyId =
        response.defaultCompanyId != null && response.companies.some(company => company.id === response.defaultCompanyId)
          ? String(response.defaultCompanyId)
          : (response.companies[0] ? String(response.companies[0].id) : '');

      const nextSelectedCompanyId =
        storedCompanyId
        && response.companies.some((company) => String(company.id) === storedCompanyId)
          ? storedCompanyId
          : fallbackCompanyId;

      setSelectedCompanyId(nextSelectedCompanyId);
    } catch (error) {
      if (!current()) return;
      setSettings(null);
      setSelectedCompanyId('');
      setSettingsError(
        extractDisplayErrorMessage(error, 'Не удалось загрузить список основных компаний.'),
      );
    } finally {
      if (current()) setSettingsLoading(false);
    }
  }, [session]);

  const loadProfile = useCallback(async (companyId: string) => {
    if (!isAuthSessionIdentityCurrent(session)) return;
    const request = ++profileRequest.current;
    const current = () => request === profileRequest.current && isAuthSessionIdentityCurrent(session);
    if (!companyId) {
      setProfile(null);
      setProfileError(null);
      return;
    }

    try {
      setProfileLoading(true);
      setProfileError(null);

      const detail = await apiService.getLegalEntity(Number(companyId));
      if (!current()) return;
      if (detail.id !== Number(companyId)) throw new Error('Сервер вернул другой профиль компании. Откройте карточку заново.');
      setProfile(detail);
    } catch (error) {
      if (!current()) return;
      setProfile(null);
      setProfileError(
        extractDisplayErrorMessage(error, 'Не удалось загрузить профиль выбранной компании.'),
      );
    } finally {
      if (current()) setProfileLoading(false);
    }
  }, [session]);

  useEffect(() => {
    if (canView) void loadSettings();
    else {
      ++settingsRequest.current;
      ++profileRequest.current;
      setSettings(null);
      setSelectedCompanyId('');
      setProfile(null);
      setSettingsLoading(false);
    }
  }, [canView, loadSettings]);

  useEffect(() => () => { ++settingsRequest.current; ++profileRequest.current; }, []);

  useEffect(() => {
    if (canView) void loadProfile(selectedCompanyId);
  }, [canView, loadProfile, selectedCompanyId]);

  useEffect(() => {
    if (selectedCompanyId && settings?.companies.some((company) => String(company.id) === selectedCompanyId)) {
      setStoredCompanyProfileCompanyId(selectedCompanyId);
    }
  }, [selectedCompanyId, settings]);

  const handleSubmit = useCallback(
    async (payload: LegalEntityInput) => {
      const current = () => isCurrent(renderedOwner) && isAuthSessionIdentityCurrent(session);
      if (!current()) throw new StaleUiOperationError();
      if (!canEdit || profileMode !== 'edit' || !profile || profile.id !== Number(selectedCompanyId)) {
        throw new Error('Компания для редактирования не выбрана.');
      }

      const saved = await awaitOwnedUiResult(() => apiService.updateLegalEntity(profile.id, payload), current);
      if (saved.id !== profile.id) throw new Error('Сервер вернул другой профиль компании. Откройте карточку заново.');
      setProfile(saved);
      setSettings((prev) => prev
        ? {
            ...prev,
            companies: prev.companies.map((company) => (company.id === saved.id ? saved : company)),
          }
        : prev);
      toast.success('Профиль компании сохранён.', 'Профиль компании');
      return saved;
    },
    [canEdit, profileMode, profile, selectedCompanyId, toast, isCurrent, renderedOwner, session],
  );

  const handleReadonlySubmit = useCallback(async () => {
    if (!isCurrent(renderedOwner) || !isAuthSessionIdentityCurrent(session)) throw new StaleUiOperationError();
    if (!profile) {
      throw new Error('Компания профиля не загружена.');
    }

    return profile;
  }, [profile, isCurrent, renderedOwner, session]);

  const leaveEditor = () => {
    invalidate();
    if (isAdminUser) setActiveTab('settings');
    else setProfileMode('view');
  };

  const hasConfiguredCompanies = (settings?.companies.length ?? 0) > 0;
  const isReadOnlyProfile = !canEdit || profileMode === 'view';

  if (!canView) return <div className="p-3 lg:p-4">Нет разрешения на просмотр профиля компании.</div>;

  return (
    <div className="space-y-3 p-3 lg:p-4">
      {hasConfiguredCompanies ? (
        <div className="flex flex-wrap items-end gap-3">
          <label className="text-sm text-slate-700">
            Компания
            <select aria-label="Компания профиля" value={selectedCompanyId} onChange={(event) => {
              if (!isAuthSessionIdentityCurrent(session) || !settings?.companies.some(company => String(company.id) === event.target.value)) return;
              invalidate(); setProfile(null); setSelectedCompanyId(event.target.value);
            }} className="ml-2 rounded-lg border border-slate-300 px-2 py-1.5">
              {settings?.companies.map((company) => <option key={company.id} value={company.id}>{company.shortName}</option>)}
            </select>
          </label>
          {canEdit && isReadOnlyProfile ? <button type="button" onClick={() => setProfileMode('edit')} className="rounded-lg border border-slate-300 px-3 py-1.5 text-sm">Редактировать профиль</button> : null}
        </div>
      ) : null}
      {settingsError ? (
        <div className="flex items-center gap-2 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
          <AlertCircle className="h-4 w-4 shrink-0" />
          {settingsError}
        </div>
      ) : null}

      {profileError ? (
        <div className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          <AlertCircle className="h-4 w-4 shrink-0" />
          {profileError}
        </div>
      ) : null}

      {settingsLoading || profileLoading ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-10 shadow-sm">
          <div className="flex items-center justify-center gap-3 text-slate-600">
            <Loader2 className="h-5 w-5 animate-spin" />
            Загружаем профиль компании...
          </div>
        </div>
      ) : profile ? (
        <LegalEntityModal
          key={`${userId}:${roleId}:${profile.id}:${isReadOnlyProfile}`}
          isOpen
          initial={profile}
          onClose={isReadOnlyProfile ? () => undefined : leaveEditor}
          onBack={isReadOnlyProfile ? undefined : leaveEditor}
          onSubmit={isReadOnlyProfile ? handleReadonlySubmit : handleSubmit}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit={!isReadOnlyProfile}
          allowDelete={false}
          canManageOwnCompanySettings={isAdminUser}
          canManageCompanyBranches={isAdminUser}
          relatedRecordPermissions={isAdminUser ? undefined : permissions}
          canViewCases={isAdminUser || permissions.cases?.canView === true}
          canViewAccounts={isAdminUser || permissions.accounts?.canView === true}
          canCreateAccounts={isAdminUser || (permissions.accounts?.canView === true && permissions.accounts?.canCreate === true)}
          canViewActs={isAdminUser || permissions.acts?.canView === true}
          canViewPayments={isAdminUser || permissions.payments?.canView === true}
          mode="page"
          titleOverride={isReadOnlyProfile ? 'Профиль компании' : undefined}
          hidePageNavigation={isReadOnlyProfile}
          hideFormActions={isReadOnlyProfile}
          hideDadataSection={isReadOnlyProfile}
          hideMetaInfo={isReadOnlyProfile}
          hideAuditSection={isReadOnlyProfile}
          hideDictionaryButtons={isReadOnlyProfile}
          compactReadOnlyView={isReadOnlyProfile}
        />
      ) : (
        <div className="rounded-2xl border border-dashed border-slate-300 bg-white p-10 text-center shadow-sm">
          <Building2 className="mx-auto h-10 w-10 text-slate-300" />
          <h2 className="mt-4 text-lg font-semibold text-slate-900">
            {isAdminUser ? 'Основные компании пока не настроены' : 'Нет доступных компаний'}
          </h2>
          <p className="mt-2 text-sm text-slate-500">
            {isAdminUser
              ? 'Сначала добавьте одну или несколько основных компаний в настройках, а затем здесь можно будет просматривать их реквизиты.'
              : 'Для просмотра профиля администратор должен назначить вам компанию в карточке сотрудника.'}
          </p>
          {isAdminUser && !hasConfiguredCompanies ? (
            <div className="mt-6">
              <button
                type="button"
                onClick={() => setActiveTab('settings')}
                className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700"
              >
                <Settings2 className="h-4 w-4" />
                Перейти в настройки
              </button>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
