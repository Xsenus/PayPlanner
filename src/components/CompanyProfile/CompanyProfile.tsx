import { AlertCircle, Building2, Loader2, Settings2 } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';

import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useToast } from '../../hooks/useToast';
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
  const { isAdmin } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const toast = useToast();
  const isAdminUser = isAdmin();

  const [settings, setSettings] = useState<MainCompanySettingsDto | null>(null);
  const [settingsLoading, setSettingsLoading] = useState(true);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [selectedCompanyId, setSelectedCompanyId] = useState('');
  const [profileMode] = useState<CompanyProfileMode>(() => getStoredCompanyProfileMode() ?? 'view');

  const [profile, setProfile] = useState<LegalEntityDetail | null>(null);
  const [profileLoading, setProfileLoading] = useState(false);
  const [profileError, setProfileError] = useState<string | null>(null);

  const loadSettings = useCallback(async () => {
    try {
      setSettingsLoading(true);
      setSettingsError(null);

      const response = await apiService.getMainCompanySettings();
      setSettings(response);

      const storedCompanyId = getStoredCompanyProfileCompanyId();
      const fallbackCompanyId =
        response.defaultCompanyId != null
          ? String(response.defaultCompanyId)
          : (response.companies[0] ? String(response.companies[0].id) : '');

      const nextSelectedCompanyId =
        profileMode === 'edit'
        && storedCompanyId
        && response.companies.some((company) => String(company.id) === storedCompanyId)
          ? storedCompanyId
          : fallbackCompanyId;

      setSelectedCompanyId(nextSelectedCompanyId);
    } catch (error) {
      setSettings(null);
      setSelectedCompanyId('');
      setSettingsError(
        extractDisplayErrorMessage(error, 'Не удалось загрузить список основных компаний.'),
      );
    } finally {
      setSettingsLoading(false);
    }
  }, [profileMode]);

  const loadProfile = useCallback(async (companyId: string) => {
    if (!companyId) {
      setProfile(null);
      setProfileError(null);
      return;
    }

    try {
      setProfileLoading(true);
      setProfileError(null);

      const detail = await apiService.getLegalEntity(Number(companyId));
      setProfile(detail);
    } catch (error) {
      setProfile(null);
      setProfileError(
        extractDisplayErrorMessage(error, 'Не удалось загрузить профиль выбранной компании.'),
      );
    } finally {
      setProfileLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  useEffect(() => {
    void loadProfile(selectedCompanyId);
  }, [loadProfile, selectedCompanyId]);

  useEffect(() => {
    setStoredCompanyProfileCompanyId(selectedCompanyId);
  }, [selectedCompanyId]);

  const handleSubmit = useCallback(
    async (payload: LegalEntityInput) => {
      if (!profile) {
        throw new Error('Компания для редактирования не выбрана.');
      }

      const saved = await apiService.updateLegalEntity(profile.id, payload);
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
    [profile, toast],
  );

  const handleReadonlySubmit = useCallback(async () => {
    if (!profile) {
      throw new Error('Компания профиля не загружена.');
    }

    return profile;
  }, [profile]);

  const hasConfiguredCompanies = (settings?.companies.length ?? 0) > 0;
  const isReadOnlyProfile = profileMode === 'view';

  return (
    <div className="space-y-6 p-6">
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
          isOpen
          initial={profile}
          onClose={isReadOnlyProfile ? () => undefined : () => setActiveTab('settings')}
          onBack={isReadOnlyProfile ? undefined : () => setActiveTab('settings')}
          onSubmit={isReadOnlyProfile ? handleReadonlySubmit : handleSubmit}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit={!isReadOnlyProfile}
          allowDelete={false}
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
            Основные компании пока не настроены
          </h2>
          <p className="mt-2 text-sm text-slate-500">
            {isAdminUser
              ? 'Сначала добавьте одну или несколько основных компаний в настройках, а затем здесь можно будет просматривать их реквизиты.'
              : 'Попросите администратора настроить основные компании в разделе настроек.'}
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
