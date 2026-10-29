import { AlertCircle, Building2, Loader2, Settings2 } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';

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

export function CompanyProfile() {
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const { setActiveTab } = useTabNavigation();
  const toast = useToast();
  const isAdminUser = isAdmin();
  const canView = isAdminUser || permissions.companyProfile?.canView === true;
  const canEdit = canView && (isAdminUser || permissions.companyProfile?.canEdit === true);

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
  const saveContext = useRef(0);

  const loadSettings = useCallback(async () => {
    const request = ++settingsRequest.current;
    try {
      setSettingsLoading(true);
      setSettingsError(null);

      const response = await apiService.getMainCompanySettings();
      if (request !== settingsRequest.current) return;
      setSettings(response);

      const storedCompanyId = getStoredCompanyProfileCompanyId();
      const fallbackCompanyId =
        response.defaultCompanyId != null
          ? String(response.defaultCompanyId)
          : (response.companies[0] ? String(response.companies[0].id) : '');

      const nextSelectedCompanyId =
        storedCompanyId
        && response.companies.some((company) => String(company.id) === storedCompanyId)
          ? storedCompanyId
          : fallbackCompanyId;

      setSelectedCompanyId(nextSelectedCompanyId);
    } catch (error) {
      if (request !== settingsRequest.current) return;
      setSettings(null);
      setSelectedCompanyId('');
      setSettingsError(
        extractDisplayErrorMessage(error, 'Не удалось загрузить список основных компаний.'),
      );
    } finally {
      if (request === settingsRequest.current) setSettingsLoading(false);
    }
  }, []);

  const loadProfile = useCallback(async (companyId: string) => {
    const request = ++profileRequest.current;
    if (!companyId) {
      setProfile(null);
      setProfileError(null);
      return;
    }

    try {
      setProfileLoading(true);
      setProfileError(null);

      const detail = await apiService.getLegalEntity(Number(companyId));
      if (request !== profileRequest.current) return;
      if (detail.id !== Number(companyId)) throw new Error('Сервер вернул другой профиль компании. Откройте карточку заново.');
      setProfile(detail);
    } catch (error) {
      if (request !== profileRequest.current) return;
      setProfile(null);
      setProfileError(
        extractDisplayErrorMessage(error, 'Не удалось загрузить профиль выбранной компании.'),
      );
    } finally {
      if (request === profileRequest.current) setProfileLoading(false);
    }
  }, []);

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

  useEffect(() => () => { ++settingsRequest.current; ++profileRequest.current; ++saveContext.current; }, []);

  useEffect(() => {
    ++saveContext.current;
  }, [canView, canEdit, selectedCompanyId, user?.id, user?.role?.id]);

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
      if (!canEdit || !profile) {
        throw new Error('Компания для редактирования не выбрана.');
      }

      const context = saveContext.current;
      const saved = await apiService.updateLegalEntity(profile.id, payload);
      if (context !== saveContext.current || saved.id !== profile.id) return saved;
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
    [canEdit, profile, toast],
  );

  const handleReadonlySubmit = useCallback(async () => {
    if (!profile) {
      throw new Error('Компания профиля не загружена.');
    }

    return profile;
  }, [profile]);

  const hasConfiguredCompanies = (settings?.companies.length ?? 0) > 0;
  const isReadOnlyProfile = !canEdit || profileMode === 'view';

  if (!canView) return <div className="p-3 lg:p-4">Нет разрешения на просмотр профиля компании.</div>;

  return (
    <div className="space-y-3 p-3 lg:p-4">
      {hasConfiguredCompanies ? (
        <div className="flex flex-wrap items-end gap-3">
          <label className="text-sm text-slate-700">
            Компания
            <select aria-label="Компания профиля" value={selectedCompanyId} onChange={(event) => { ++saveContext.current; setProfile(null); setSelectedCompanyId(event.target.value); }} className="ml-2 rounded-lg border border-slate-300 px-2 py-1.5">
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
          key={`${user?.id}:${user?.role?.id}:${profile.id}:${isReadOnlyProfile}`}
          isOpen
          initial={profile}
          onClose={isReadOnlyProfile ? () => undefined : () => isAdminUser ? setActiveTab('settings') : setProfileMode('view')}
          onBack={isReadOnlyProfile ? undefined : () => isAdminUser ? setActiveTab('settings') : setProfileMode('view')}
          onSubmit={isReadOnlyProfile ? handleReadonlySubmit : handleSubmit}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit={!isReadOnlyProfile}
          allowDelete={false}
          canManageOwnCompanySettings={isAdminUser}
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
