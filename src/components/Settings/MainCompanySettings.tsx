import { Building2, Loader2, PencilLine, Plus, Star, Trash2 } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';

import { useTabNavigation } from '../../contexts/TabContext';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { apiService, type MainCompanySettingsDto } from '../../services/api';
import type { LegalEntitySummary } from '../../types';
import { setStoredCompanyProfileCompanyId, setStoredCompanyProfileMode } from '../../utils/companyProfileBridge';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';
import { useToast } from '../../hooks/useToast';

function buildCompanyOption(entity: LegalEntitySummary): LookupSelectOption {
  const descriptionParts = [
    entity.fullName?.trim() || null,
    entity.inn ? `ИНН ${entity.inn}` : null,
    entity.kpp ? `КПП ${entity.kpp}` : null,
  ].filter(Boolean) as string[];

  return {
    value: String(entity.id),
    label: entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`,
    description: descriptionParts.join(' • ') || undefined,
    keywords: [
      entity.shortName,
      entity.fullName ?? '',
      entity.inn ?? '',
      entity.kpp ?? '',
      entity.ogrn ?? '',
      entity.address ?? '',
      entity.director ?? '',
    ].filter(Boolean),
  };
}

function buildSnapshot(settings: MainCompanySettingsDto | null) {
  return JSON.stringify({
    defaultCompanyId: settings?.defaultCompanyId ?? null,
    companyIds: (settings?.companies ?? []).map((company) => company.id).sort((left, right) => left - right),
  });
}

export function MainCompanySettings() {
  const { setActiveTab } = useTabNavigation();
  const toast = useToast();
  const {
    legalEntities,
    error: legalEntitiesError,
    refresh: refreshLegalEntities,
  } = useLegalEntities();

  const [settings, setSettings] = useState<MainCompanySettingsDto | null>(null);
  const [savedSnapshot, setSavedSnapshot] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedCompanyToAddId, setSelectedCompanyToAddId] = useState('');

  const loadSettings = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const response = await apiService.getMainCompanySettings();
      setSettings(response);
      setSavedSnapshot(buildSnapshot(response));
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить настройки основных компаний.'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  const selectedCompanies = useMemo(
    () => settings?.companies ?? [],
    [settings],
  );

  const selectedCompanyIds = useMemo(
    () => new Set(selectedCompanies.map((company) => company.id)),
    [selectedCompanies],
  );

  const companyPool = useMemo(() => {
    const items = new Map<number, LegalEntitySummary>();

    for (const company of selectedCompanies) {
      items.set(company.id, company);
    }

    for (const company of legalEntities) {
      items.set(company.id, company);
    }

    return [...items.values()].sort((left, right) =>
      (left.shortName || left.fullName || '').localeCompare(
        right.shortName || right.fullName || '',
        'ru',
        { sensitivity: 'base' },
      ),
    );
  }, [legalEntities, selectedCompanies]);

  const addableOptions = useMemo(
    () => companyPool
      .filter((company) => !selectedCompanyIds.has(company.id))
      .map(buildCompanyOption),
    [companyPool, selectedCompanyIds],
  );

  const hasChanges = useMemo(
    () => buildSnapshot(settings) !== savedSnapshot,
    [savedSnapshot, settings],
  );

  const handleAddCompany = useCallback(() => {
    const companyId = Number(selectedCompanyToAddId);
    if (!companyId) {
      return;
    }

    const company = companyPool.find((item) => item.id === companyId);
    if (!company || selectedCompanyIds.has(companyId)) {
      return;
    }

    setSettings((prev) => {
      const currentCompanies = prev?.companies ?? [];
      const nextCompanies = [...currentCompanies, company].sort((left, right) =>
        (left.shortName || left.fullName || '').localeCompare(
          right.shortName || right.fullName || '',
          'ru',
          { sensitivity: 'base' },
        ),
      );

      return {
        companies: nextCompanies,
        defaultCompanyId: prev?.defaultCompanyId ?? companyId,
      };
    });
    setSelectedCompanyToAddId('');
    setMessage(null);
  }, [companyPool, selectedCompanyIds, selectedCompanyToAddId]);

  const handleRemoveCompany = useCallback((companyId: number) => {
    setSettings((prev) => {
      if (!prev) {
        return prev;
      }

      const nextCompanies = prev.companies.filter((company) => company.id !== companyId);
      const nextDefaultCompanyId = prev.defaultCompanyId === companyId
        ? (nextCompanies[0]?.id ?? null)
        : prev.defaultCompanyId;

      return {
        companies: nextCompanies,
        defaultCompanyId: nextDefaultCompanyId,
      };
    });
    setMessage(null);
  }, []);

  const handleOpenCompanyProfile = useCallback((companyId: number) => {
    setStoredCompanyProfileCompanyId(companyId);
    setStoredCompanyProfileMode('edit');
    setActiveTab('companyProfile');
  }, [setActiveTab]);

  const handleSave = useCallback(async () => {
    try {
      setSaving(true);
      setError(null);
      setMessage(null);

      const payload = {
        companyIds: (settings?.companies ?? []).map((company) => company.id),
        defaultCompanyId: settings?.defaultCompanyId ?? null,
      };

      const response = await apiService.updateMainCompanySettings(payload);
      setSettings(response);
      setSavedSnapshot(buildSnapshot(response));
      setMessage('Настройки основных компаний сохранены.');
      toast.success('Настройки основных компаний сохранены.', 'Настройки');
      await refreshLegalEntities();
    } catch (saveError) {
      const nextError = extractDisplayErrorMessage(saveError, 'Не удалось сохранить основные компании.');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setSaving(false);
    }
  }, [refreshLegalEntities, settings, toast]);

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h4 className="text-xl font-semibold text-gray-900">Основные компании</h4>
          <p className="mt-1 max-w-3xl text-sm text-gray-600">
            Здесь задаётся список компаний, которые используются как внутренние компании сервиса.
            После сохранения они скрываются из раздела «Юридические лица», а профиль компании и документы
            работают уже с этим отдельным списком.
          </p>
        </div>

        {loading ? (
          <span className="inline-flex items-center gap-2 text-sm text-slate-500">
            <Loader2 className="h-4 w-4 animate-spin" />
            Загружаем настройки...
          </span>
        ) : null}
      </div>

      <div className="mt-6 space-y-4">
        <div className="space-y-4">
          <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
            <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_auto] md:items-end">
              <LookupSelect
                label="Добавить компанию"
                value={selectedCompanyToAddId}
                options={addableOptions}
                onChange={setSelectedCompanyToAddId}
                placeholder={addableOptions.length > 0 ? 'Выберите юридическое лицо' : 'Все доступные компании уже добавлены'}
                title="Добавление основной компании"
                searchPlaceholder="Поиск по названию, ИНН, КПП или адресу"
                noOptionsText="Нет доступных компаний для добавления"
                disabled={loading || saving || addableOptions.length === 0}
                helperText="В список можно добавить несколько компаний. Одна из них будет использоваться по умолчанию в документах."
              />

              <button
                type="button"
                onClick={handleAddCompany}
                disabled={loading || saving || !selectedCompanyToAddId}
                className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300"
              >
                <Plus className="h-4 w-4" />
                Добавить
              </button>
            </div>
          </div>

          {selectedCompanies.length === 0 ? (
            <div className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-5 py-8 text-center text-sm text-slate-500">
              Основные компании пока не выбраны. Добавь хотя бы одну компанию, чтобы использовать её в профиле компании и документах.
            </div>
          ) : (
            <div className="space-y-3">
              {selectedCompanies.map((company) => {
                const isDefault = settings?.defaultCompanyId === company.id;

                return (
                  <div
                    key={company.id}
                    className={`rounded-2xl border px-4 py-4 transition ${
                      isDefault
                        ? 'border-amber-200 bg-amber-50/70'
                        : 'border-slate-200 bg-white'
                    }`}
                  >
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                      <div className="min-w-0">
                        <div className="flex items-center gap-2">
                          <Building2 className="h-4 w-4 shrink-0 text-slate-400" />
                          <h5 className="truncate text-base font-semibold text-slate-900">{company.shortName}</h5>
                          {isDefault ? (
                            <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2.5 py-1 text-xs font-semibold text-amber-800">
                              <Star className="h-3.5 w-3.5" />
                              По умолчанию
                            </span>
                          ) : null}
                        </div>

                        <div className="mt-2 text-sm text-slate-600">
                          {company.fullName || 'Полное наименование не указано'}
                        </div>
                        <div className="mt-1 text-xs text-slate-500">
                          {[company.inn ? `ИНН ${company.inn}` : null, company.kpp ? `КПП ${company.kpp}` : null]
                            .filter(Boolean)
                            .join(' • ') || 'Реквизиты не заполнены'}
                        </div>
                      </div>

                      <div className="flex flex-wrap gap-2">
                        <button
                          type="button"
                          onClick={() => handleOpenCompanyProfile(company.id)}
                          disabled={saving}
                          className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                        >
                          <PencilLine className="h-4 w-4" />
                          Редактировать
                        </button>

                        <button
                          type="button"
                          onClick={() => setSettings((prev) => prev ? ({ ...prev, defaultCompanyId: company.id }) : prev)}
                          disabled={saving || isDefault}
                          className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                        >
                          <Star className="h-4 w-4" />
                          Использовать по умолчанию
                        </button>

                        <button
                          type="button"
                          onClick={() => handleRemoveCompany(company.id)}
                          disabled={saving}
                          className="inline-flex items-center gap-2 rounded-xl border border-rose-200 bg-white px-3 py-2 text-sm font-medium text-rose-600 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60"
                        >
                          <Trash2 className="h-4 w-4" />
                          Убрать
                        </button>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="space-y-4">
          {(error || legalEntitiesError || message) ? (
            <div className="space-y-3">
              {error ? (
                <div className="rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
                  {error}
                </div>
              ) : null}
              {legalEntitiesError ? (
                <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                  {legalEntitiesError}
                </div>
              ) : null}
              {message ? (
                <div className="rounded-2xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">
                  {message}
                </div>
              ) : null}
            </div>
          ) : null}

          <div className="flex justify-end">
            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={loading || saving || !hasChanges}
              className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Сохранить
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
