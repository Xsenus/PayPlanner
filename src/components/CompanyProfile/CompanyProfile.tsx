import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertCircle, Building2, Loader2 } from 'lucide-react';

import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useDialog } from '../../hooks/useDialog';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { LegalEntityDetail, LegalEntityInput, LegalEntitySummary } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { LegalEntityModal } from '../LegalEntities/LegalEntityModal';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';

function isNotFoundError(error: unknown) {
  return error instanceof Error && error.message.includes('HTTP 404');
}

function buildCompanyOption(entity: LegalEntitySummary | LegalEntityDetail): LookupSelectOption {
  const descriptionParts = [
    entity.fullName?.trim() || null,
    entity.inn ? `ИНН ${entity.inn}` : null,
    entity.kpp ? `КПП ${entity.kpp}` : null,
  ].filter(Boolean) as string[];

  return {
    value: String(entity.id),
    label: entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`,
    description: descriptionParts.join(' | ') || undefined,
    keywords: [
      entity.shortName,
      entity.fullName ?? '',
      entity.inn ?? '',
      entity.kpp ?? '',
      entity.ogrn ?? '',
      entity.address ?? '',
      entity.email ?? '',
      entity.phone ?? '',
      entity.director ?? '',
    ].filter(Boolean),
  };
}

export function CompanyProfile() {
  const { isAdmin } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const { confirm } = useDialog();
  const toast = useToast();
  const isAdminUser = isAdmin();

  const {
    legalEntities,
    loading: legalEntitiesLoading,
    error: legalEntitiesError,
    refresh: refreshLegalEntities,
  } = useLegalEntities();

  const [profile, setProfile] = useState<LegalEntityDetail | null>(null);
  const [profileLoading, setProfileLoading] = useState(true);
  const [profileError, setProfileError] = useState<string | null>(null);
  const [selectedLegalEntityId, setSelectedLegalEntityId] = useState('');
  const [switchingMainCompany, setSwitchingMainCompany] = useState(false);

  const companyOptions = useMemo(() => {
    const items = new Map<string, LookupSelectOption>();

    if (profile) {
      items.set(String(profile.id), buildCompanyOption(profile));
    }

    for (const entity of legalEntities) {
      items.set(String(entity.id), buildCompanyOption(entity));
    }

    return [...items.values()].sort((left, right) =>
      left.label.localeCompare(right.label, 'ru', { sensitivity: 'base' }),
    );
  }, [legalEntities, profile]);

  const selectedCompanyOption = useMemo(
    () => companyOptions.find((option) => option.value === selectedLegalEntityId) ?? null,
    [companyOptions, selectedLegalEntityId],
  );

  const loadCompanyProfile = useCallback(async () => {
    try {
      setProfileLoading(true);
      setProfileError(null);

      const detail = await apiService.getCompanyProfileLegalEntity();
      setProfile(detail);
    } catch (error) {
      setProfile(null);

      if (isNotFoundError(error)) {
        setProfileError(
          isAdminUser
            ? 'Головная организация пока не выбрана. Назначьте основную компанию из списка ниже.'
            : 'Головная организация пока не настроена. Обратитесь к администратору.',
        );
      } else {
        setProfileError(
          extractDisplayErrorMessage(error, 'Не удалось загрузить профиль компании.'),
        );
      }
    } finally {
      setProfileLoading(false);
    }
  }, [isAdminUser]);

  useEffect(() => {
    void loadCompanyProfile();
  }, [loadCompanyProfile]);

  useEffect(() => {
    if (!isAdminUser) {
      return;
    }

    if (profile?.id) {
      const profileId = String(profile.id);
      setSelectedLegalEntityId((prev) => (prev === profileId ? prev : profileId));
      return;
    }

    setSelectedLegalEntityId((prev) => prev || companyOptions[0]?.value || '');
  }, [companyOptions, isAdminUser, profile?.id]);

  const handleSetMainCompany = useCallback(async () => {
    if (!selectedLegalEntityId) {
      setProfileError('Выберите юридическое лицо, которое нужно сделать основной компанией.');
      return;
    }

    if (String(profile?.id ?? '') === selectedLegalEntityId) {
      return;
    }

    const approved = await confirm({
      title: 'Смена основной компании',
      description: selectedCompanyOption
        ? `Сделать "${selectedCompanyOption.label}" основной компанией? Профиль компании и связанные выборы будут использовать именно её.`
        : 'Сделать выбранную компанию основной?',
      confirmLabel: 'Сделать основной',
      cancelLabel: 'Отмена',
      variant: 'primary',
    });

    if (!approved) {
      return;
    }

    try {
      setSwitchingMainCompany(true);
      setProfileError(null);

      const detail = await apiService.setCompanyProfileLegalEntity(Number(selectedLegalEntityId));
      setProfile(detail);
      setSelectedLegalEntityId(String(detail.id));
      await refreshLegalEntities();
      toast.success('Основная компания обновлена.', 'Профиль компании');
    } catch (error) {
      const message = extractDisplayErrorMessage(
        error,
        'Не удалось назначить головную организацию.',
      );
      setProfileError(message);
      toast.error(message, 'Профиль компании');
    } finally {
      setSwitchingMainCompany(false);
    }
  }, [confirm, profile?.id, refreshLegalEntities, selectedCompanyOption, selectedLegalEntityId, toast]);

  const handleSubmit = useCallback(
    async (payload: LegalEntityInput) => {
      if (!profile) {
        throw new Error('Головная организация не выбрана.');
      }

      const saved = await apiService.updateLegalEntity(profile.id, payload);
      setProfile(saved);
      await refreshLegalEntities();
      toast.success('Профиль компании сохранён.', 'Профиль компании');
      return saved;
    },
    [profile, refreshLegalEntities, toast],
  );

  return (
    <div className="space-y-6 p-6">
      <section className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div>
            <h1 className="text-2xl font-semibold text-slate-900">Профиль компании</h1>
            <p className="mt-1 text-sm text-slate-500">
              Профиль компании редактирует то же юридическое лицо, которое используется как
              головная организация.
            </p>
          </div>

          {isAdminUser ? (
            <div className="w-full max-w-xl space-y-3">
              <LookupSelect
                label="Основная компания"
                value={selectedLegalEntityId}
                options={companyOptions}
                onChange={setSelectedLegalEntityId}
                placeholder={
                  legalEntitiesLoading ? 'Загрузка списка компаний...' : 'Выберите юридическое лицо'
                }
                title="Выбор основной компании"
                searchPlaceholder="Поиск по названию, ИНН, КПП или адресу"
                helperText="Выбор сохраняется в системных настройках базы данных и используется как головная организация."
                noOptionsText="Юридические лица не найдены"
                disabled={legalEntitiesLoading || switchingMainCompany || companyOptions.length === 0}
              />

              <div className="flex justify-end">
                <button
                  type="button"
                  onClick={() => void handleSetMainCompany()}
                  disabled={
                    legalEntitiesLoading
                    || switchingMainCompany
                    || !selectedLegalEntityId
                    || String(profile?.id ?? '') === selectedLegalEntityId
                  }
                  className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300"
                >
                  {switchingMainCompany ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                  Сделать основной
                </button>
              </div>
            </div>
          ) : null}
        </div>

        {legalEntitiesError && isAdminUser ? (
          <div className="mt-4 flex items-center gap-2 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
            <AlertCircle className="h-4 w-4 shrink-0" />
            {legalEntitiesError}
          </div>
        ) : null}

        {profileError ? (
          <div className="mt-4 flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
            <AlertCircle className="h-4 w-4 shrink-0" />
            {profileError}
          </div>
        ) : null}
      </section>

      {profileLoading ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-10 shadow-sm">
          <div className="flex items-center justify-center gap-3 text-slate-600">
            <Loader2 className="h-5 w-5 animate-spin" />
            Загрузка профиля компании...
          </div>
        </div>
      ) : profile ? (
        <LegalEntityModal
          isOpen
          initial={profile}
          onClose={() => setActiveTab('legalEntities')}
          onBack={() => setActiveTab('legalEntities')}
          onSubmit={handleSubmit}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit
          allowDelete={false}
          mode="page"
        />
      ) : (
        <div className="rounded-2xl border border-dashed border-slate-300 bg-white p-10 text-center shadow-sm">
          <Building2 className="mx-auto h-10 w-10 text-slate-300" />
          <h2 className="mt-4 text-lg font-semibold text-slate-900">
            Профиль компании пока не настроен
          </h2>
          <p className="mt-2 text-sm text-slate-500">
            {isAdminUser
              ? 'Выберите юридическое лицо и назначьте его основной компанией.'
              : 'Ожидается настройка головной организации администратором.'}
          </p>
        </div>
      )}
    </div>
  );
}
