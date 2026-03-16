import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import type { FormRequirementSectionMetadataDto, InvoiceLegacyMigrationSummaryDto, InvoiceNumberingSettingsDto, LegacyAddressMigrationResult } from '../../services/api';

type SettingsSection = 'general' | 'integrations' | 'import-addresses' | 'dictionary-merge' | 'form-requirements';
type MergeKind = 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes';

export function AppSettings() {
  const [activeSection, setActiveSection] = useState<SettingsSection>('integrations');
  const [loading, setLoading] = useState(false);
  const [validateWithDadata, setValidateWithDadata] = useState(true);
  const [result, setResult] = useState<LegacyAddressMigrationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dadataToken, setDadataToken] = useState('');
  const [dadataSecret, setDadataSecret] = useState('');
  const [credentialsLoading, setCredentialsLoading] = useState(false);
  const [credentialsMessage, setCredentialsMessage] = useState<string | null>(null);
  const [mergeKind, setMergeKind] = useState<MergeKind>('organization-forms');
  const [duplicates, setDuplicates] = useState<Array<{ canonical: string; items: Array<{ id: number; name: string; description: string; colorHex: string; isActive: boolean }> }>>([]);
  const [mergeLoading, setMergeLoading] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const [mergeMessage, setMergeMessage] = useState<string | null>(null);
  const [selectedGroupKey, setSelectedGroupKey] = useState<string>('');
  const [preferredId, setPreferredId] = useState<number | null>(null);
  const [requiredFieldKeys, setRequiredFieldKeys] = useState<Set<string>>(new Set());
  const [formRequirementSections, setFormRequirementSections] = useState<FormRequirementSectionMetadataDto[]>([]);
  const [selectedRequirementsSection, setSelectedRequirementsSection] = useState<string>('');
  const [requirementsLoading, setRequirementsLoading] = useState(false);
  const [requirementsMessage, setRequirementsMessage] = useState<string | null>(null);
  const [invoiceNumbering, setInvoiceNumbering] = useState<InvoiceNumberingSettingsDto>({ startNumber: 1, allocationStrategy: 'max-plus-one' });
  const [invoiceNumberingLoading, setInvoiceNumberingLoading] = useState(false);
  const [invoiceNumberingMessage, setInvoiceNumberingMessage] = useState<string | null>(null);
  const [invoiceMigrationLoading, setInvoiceMigrationLoading] = useState(false);
  const [invoiceMigrationError, setInvoiceMigrationError] = useState<string | null>(null);
  const [invoiceMigrationResult, setInvoiceMigrationResult] = useState<InvoiceLegacyMigrationSummaryDto | null>(null);

  const activeSectionTitle = useMemo(() => {
    if (activeSection === 'general') return 'Основные';
    if (activeSection === 'integrations') return 'Ключи интеграций';
    if (activeSection === 'dictionary-merge') return 'Слияние дублей справочников';
    if (activeSection === 'form-requirements') return 'Обязательные поля форм';
    return 'Импорт · Перенос адресов';
  }, [activeSection]);

  const loadFormRequirements = async () => {
    setRequirementsLoading(true);
    setRequirementsMessage(null);
    try {
      const [data, sections] = await Promise.all([
        apiService.getFormRequirements(),
        apiService.getFormRequirementSections(),
      ]);
      setRequiredFieldKeys(new Set((data ?? []).filter((x) => x.isRequired).map((x) => `${x.section}:${x.fieldKey}`)));
      setFormRequirementSections(sections ?? []);
      if ((sections ?? []).length > 0) {
        setSelectedRequirementsSection((prev) => prev || sections[0].key);
      }
    } catch (e) {
      setRequirementsMessage(e instanceof Error ? e.message : 'Не удалось загрузить обязательные поля');
    } finally {
      setRequirementsLoading(false);
    }
  };

  const toggleRequiredField = async (section: string, fieldKey: string, isRequired: boolean) => {
    setRequirementsLoading(true);
    setRequirementsMessage(null);
    try {
      await apiService.upsertFormRequirement({ section, fieldKey, isRequired });
      await loadFormRequirements();
      setRequirementsMessage('Настройки обязательных полей сохранены');
    } catch (e) {
      setRequirementsMessage(e instanceof Error ? e.message : 'Не удалось сохранить настройку');
      setRequirementsLoading(false);
    }
  };

  const loadDuplicates = async (kind: MergeKind) => {
    setMergeLoading(true);
    setMergeError(null);
    setMergeMessage(null);
    try {
      const data = await apiService.getSimpleDictionaryDuplicates(kind);
      setDuplicates(data ?? []);
      const first = (data ?? [])[0];
      setSelectedGroupKey(first?.canonical ?? '');
      setPreferredId(first?.items?.[0]?.id ?? null);
    } catch (e) {
      setMergeError(e instanceof Error ? e.message : 'Не удалось загрузить дубли');
      setDuplicates([]);
      setSelectedGroupKey('');
      setPreferredId(null);
    } finally {
      setMergeLoading(false);
    }
  };

  const runMerge = async () => {
    const group = duplicates.find((item) => item.canonical === selectedGroupKey);
    if (!group || group.items.length < 2 || !preferredId) {
      setMergeError('Выберите группу дублей и базовое значение');
      return;
    }
    setMergeLoading(true);
    setMergeError(null);
    setMergeMessage(null);
    try {
      const primary = group.items.find((item) => item.id === preferredId) ?? group.items[0];
      const result = await apiService.mergeSimpleDictionaryDuplicates(mergeKind, {
        sourceIds: group.items.map((item) => item.id),
        preferredId: primary.id,
        name: primary.name,
        description: primary.description,
        colorHex: primary.colorHex,
        isActive: primary.isActive,
      });
      setMergeMessage(`Объединение выполнено: создано "${result.name}", обновлено юрлиц: ${result.replacedInLegalEntities}`);
      await loadDuplicates(mergeKind);
      window.dispatchEvent(new CustomEvent('dicts:changed'));
    } catch (e) {
      setMergeError(e instanceof Error ? e.message : 'Не удалось выполнить объединение');
    } finally {
      setMergeLoading(false);
    }
  };


  const loadDaDataCredentials = async () => {
    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      const [tokenSetting, secretSetting] = await Promise.all([
        apiService.getUserSetting('integrations.dadata.apiKey').catch(() => null),
        apiService.getUserSetting('integrations.dadata.secret').catch(() => null),
      ]);
      setDadataToken(tokenSetting?.value ?? '');
      setDadataSecret(secretSetting?.value ?? '');
    } catch (e) {
      setCredentialsMessage(e instanceof Error ? e.message : 'Не удалось загрузить ключи DaData');
    } finally {
      setCredentialsLoading(false);
    }
  };

  const saveDaDataCredentials = async () => {
    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      await Promise.all([
        apiService.upsertUserSetting('integrations.dadata.apiKey', dadataToken.trim()),
        apiService.upsertUserSetting('integrations.dadata.secret', dadataSecret.trim()),
      ]);
      setCredentialsMessage('Ключи DaData сохранены');
    } catch (e) {
      setCredentialsMessage(e instanceof Error ? e.message : 'Не удалось сохранить ключи DaData');
    } finally {
      setCredentialsLoading(false);
    }
  };

  const loadInvoiceNumbering = async () => {
    setInvoiceNumberingLoading(true);
    setInvoiceNumberingMessage(null);
    try {
      const response = await apiService.getInvoiceNumberingSettings();
      setInvoiceNumbering(response);
    } catch (e) {
      setInvoiceNumberingMessage(e instanceof Error ? e.message : 'Не удалось загрузить настройки нумерации счетов');
    } finally {
      setInvoiceNumberingLoading(false);
    }
  };

  const saveInvoiceNumbering = async () => {
    setInvoiceNumberingLoading(true);
    setInvoiceNumberingMessage(null);
    try {
      const response = await apiService.updateInvoiceNumberingSettings(invoiceNumbering);
      setInvoiceNumbering(response);
      setInvoiceNumberingMessage('Настройки нумерации счетов сохранены');
    } catch (e) {
      setInvoiceNumberingMessage(e instanceof Error ? e.message : 'Не удалось сохранить настройки нумерации счетов');
    } finally {
      setInvoiceNumberingLoading(false);
    }
  };

  const runMigration = async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await apiService.migrateLegacyLegalEntityAddresses(validateWithDadata);
      setResult(response);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось выполнить перенос адресов');
    } finally {
      setLoading(false);
    }
  };

  const runInvoiceMigration = async () => {
    setInvoiceMigrationLoading(true);
    setInvoiceMigrationError(null);
    try {
      const response = await apiService.migrateLegacyInvoiceLinks();
      setInvoiceMigrationResult(response);
    } catch (e) {
      setInvoiceMigrationError(e instanceof Error ? e.message : 'Не удалось выполнить перенос счетов');
    } finally {
      setInvoiceMigrationLoading(false);
    }
  };

  useEffect(() => {
    if (activeSection === 'integrations') {
      void loadDaDataCredentials();
    }
    if (activeSection === 'general') {
      void loadInvoiceNumbering();
    }
    if (activeSection === 'dictionary-merge') {
      void loadDuplicates(mergeKind);
    }
    if (activeSection === 'form-requirements') {
      void loadFormRequirements();
    }
  }, [activeSection, mergeKind]);

  return (
    <div className="p-6 lg:p-8">
      <div className="w-full">
        <div className="grid grid-cols-1 lg:grid-cols-[280px_1fr] gap-6">
          <aside className="bg-white border border-gray-200 rounded-2xl shadow-sm p-4 h-fit">
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Настройки</h2>

            <nav aria-label="Разделы настроек" className="space-y-2">
              <button
                type="button"
                onClick={() => setActiveSection('general')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'general'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Основные
              </button>

              <button
                type="button"
                onClick={() => setActiveSection('integrations')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'integrations'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Ключи интеграций
              </button>

              <button
                type="button"
                onClick={() => setActiveSection('form-requirements')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'form-requirements'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Обязательные поля форм
              </button>

              <div className="rounded-xl border border-gray-200 bg-gray-50/60 p-2">
                <div className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">Импорт</div>
                <button
                  type="button"
                  onClick={() => setActiveSection('import-addresses')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'import-addresses'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Перенос адресов
                </button>
              </div>

              <div className="rounded-xl border border-gray-200 bg-gray-50/60 p-2">
                <div className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">Справочники</div>
                <button
                  type="button"
                  onClick={() => setActiveSection('dictionary-merge')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'dictionary-merge'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Объединение дублей
                </button>
              </div>
            </nav>
          </aside>

          <section className="space-y-6">
            <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6">
              <h3 className="text-lg font-semibold text-gray-900">{activeSectionTitle}</h3>
              <p className="text-sm text-gray-600 mt-1">
                {activeSection === 'general'
                  ? 'Базовые параметры системы. Раздел подготовлен для последующего расширения.'
                  : activeSection === 'integrations'
                    ? 'Хранение и управление ключами доступа к внешним сервисам.'
                    : activeSection === 'form-requirements'
                      ? 'Администратор определяет обязательные поля отдельно для каждого раздела.'
                    : activeSection === 'dictionary-merge'
                      ? 'Безопасное транзакционное объединение дублей в справочниках с заменой ссылок во всех карточках юрлиц.'
                    : 'Перенос старых адресов из legacy-поля Address в модель адресов для юр. и физ. лиц.'}
              </p>
            </div>

            {activeSection === 'general' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div>
                  <h4 className="text-base font-semibold text-gray-900">Нумерация счетов</h4>
                  <p className="text-sm text-gray-600 mt-1">Номер сбрасывается каждый год. Можно выбрать стартовый номер и стратегию подбора следующего номера.</p>
                </div>

                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Стартовый номер года</span>
                  <input
                    type="number"
                    min={1}
                    value={invoiceNumbering.startNumber}
                    onChange={(e) => setInvoiceNumbering((prev) => ({ ...prev, startNumber: Math.max(1, Number(e.target.value) || 1) }))}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>

                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Стратегия следующего номера</span>
                  <select
                    value={invoiceNumbering.allocationStrategy}
                    onChange={(e) => setInvoiceNumbering((prev) => ({ ...prev, allocationStrategy: e.target.value as InvoiceNumberingSettingsDto['allocationStrategy'] }))}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  >
                    <option value="max-plus-one">Максимальный номер + 1</option>
                    <option value="first-free">Первый свободный номер от стартового</option>
                  </select>
                </label>

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={saveInvoiceNumbering}
                    disabled={invoiceNumberingLoading}
                    className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {invoiceNumberingLoading ? 'Сохранение…' : 'Сохранить'}
                  </button>
                  {invoiceNumberingMessage && <span className="text-sm text-gray-600">{invoiceNumberingMessage}</span>}
                </div>
              </div>
            )}

            {activeSection === 'general' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div>
                  <h4 className="text-base font-semibold text-gray-900">Ручной перенос счетов в новую сущность</h4>
                  <p className="text-sm text-gray-600 mt-1">Запускает перенос legacy-счетов из платежей в отдельную сущность счетов и связывает платежи со счетами. Безопасно для повторного запуска.</p>
                </div>

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={runInvoiceMigration}
                    disabled={invoiceMigrationLoading}
                    className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {invoiceMigrationLoading ? 'Переносим…' : 'Перенести счета'}
                  </button>
                </div>

                {invoiceMigrationError && (
                  <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{invoiceMigrationError}</div>
                )}

                {invoiceMigrationResult && (
                  <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                    <p className="font-medium mb-2">Сводка переноса счетов</p>
                    <ul className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
                      <li>Проверено платежей: <b>{invoiceMigrationResult.scannedPayments}</b></li>
                      <li>Платежей-счетов: <b>{invoiceMigrationResult.invoicePayments}</b></li>
                      <li>Уже были связаны: <b>{invoiceMigrationResult.alreadyLinked}</b></li>
                      <li>Пропущено без номера: <b>{invoiceMigrationResult.missingNumberSkipped}</b></li>
                      <li>Связано с существующими счетами: <b>{invoiceMigrationResult.linkedToExistingInvoice}</b></li>
                      <li>Создано новых счетов: <b>{invoiceMigrationResult.createdInvoices}</b></li>
                      <li>Всего связанных платежей: <b>{invoiceMigrationResult.linkedPayments}</b></li>
                      <li>Уникальных ключей в legacy: <b>{invoiceMigrationResult.distinctLegacyInvoiceKeys}</b></li>
                      <li>Ключей-дублей уже в Invoices: <b>{invoiceMigrationResult.existingDuplicateInvoiceKeys}</b></li>
                    </ul>
                  </div>
                )}
              </div>
            )}


            {activeSection === 'integrations' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div className="grid gap-4 md:grid-cols-2">
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">DaData API token</span>
                    <input
                      value={dadataToken}
                      onChange={(event) => setDadataToken(event.target.value)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                      placeholder="Введите токен"
                    />
                  </label>
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">DaData Secret key</span>
                    <input
                      value={dadataSecret}
                      onChange={(event) => setDadataSecret(event.target.value)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                      placeholder="Введите секретный ключ"
                    />
                  </label>
                </div>
                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={saveDaDataCredentials}
                    disabled={credentialsLoading}
                    className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60"
                  >
                    {credentialsLoading ? 'Сохраняем…' : 'Сохранить ключи'}
                  </button>
                </div>
                {credentialsMessage && (
                  <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
                    {credentialsMessage}
                  </div>
                )}
              </div>
            )}

            {activeSection === 'form-requirements' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-5">
                {formRequirementSections.length === 0 && !requirementsLoading && (
                  <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
                    Нет доступных полей для настройки.
                  </div>
                )}
                {formRequirementSections.length > 0 && (
                  <div className="space-y-4">
                    <div className="flex flex-wrap gap-2 border-b border-gray-200 pb-3">
                      {formRequirementSections.map((section) => (
                        <button
                          key={section.key}
                          type="button"
                          onClick={() => setSelectedRequirementsSection(section.key)}
                          className={`rounded-lg px-3 py-2 text-sm font-medium transition ${
                            selectedRequirementsSection === section.key
                              ? 'bg-blue-50 text-blue-700 border border-blue-200'
                              : 'text-gray-700 hover:bg-gray-50 border border-gray-200'
                          }`}
                        >
                          {section.title}
                        </button>
                      ))}
                    </div>

                    {formRequirementSections
                      .filter((section) => section.key === selectedRequirementsSection)
                      .map((section) => (
                        <div key={section.key} className="rounded-xl border border-gray-200 p-4">
                          <h4 className="text-sm font-semibold text-gray-900 mb-3">{section.title}</h4>
                          {Object.entries(section.fields.reduce<Record<string, typeof section.fields>>((acc, field) => {
                            const category = field.category || 'Основные';
                            if (!acc[category]) acc[category] = [];
                            acc[category].push(field);
                            return acc;
                          }, {})).map(([category, fields]) => (
                            <div key={category} className="mb-4 last:mb-0">
                              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">{category}</div>
                              <div className="grid gap-2 md:grid-cols-2">
                                {fields.map((field) => {
                                  const id = `${section.key}:${field.key}`;
                                  const checked = requiredFieldKeys.has(id);
                                  return (
                                    <label key={id} className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm">
                                      <input
                                        type="checkbox"
                                        checked={checked}
                                        disabled={requirementsLoading}
                                        onChange={(event) => void toggleRequiredField(section.key, field.key, event.target.checked)}
                                        className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                      />
                                      <span>{field.label}</span>
                                    </label>
                                  );
                                })}
                              </div>
                            </div>
                          ))}
                        </div>
                      ))}
                  </div>
                )}
                {requirementsMessage && <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{requirementsMessage}</div>}
              </div>
            )}

            {activeSection === 'import-addresses' && (
              <>
                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                  <label className="flex items-start gap-3 text-sm text-gray-700">
                    <input
                      type="checkbox"
                      checked={validateWithDadata}
                      onChange={(event) => setValidateWithDadata(event.target.checked)}
                      className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span>
                      Переносить <b>с проверкой через DaData</b> (если выключить — перенос будет без DaData)
                    </span>
                  </label>

                  <div className="flex items-center gap-3">
                    <button
                      onClick={runMigration}
                      disabled={loading}
                      className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60 disabled:cursor-not-allowed"
                    >
                      {loading ? 'Переносим…' : 'Перенести старые адреса'}
                    </button>
                  </div>

                  {error && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                      {error}
                    </div>
                  )}

                  {result && (
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p className="font-medium mb-2">Отчёт по переносу</p>
                      <ul className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
                        <li>
                          Проверено всего карточек: <b>{result.scanned}</b>
                        </li>
                        <li>
                          Из них юр. лиц: <b>{result.scannedLegalEntities ?? 0}</b>
                        </li>
                        <li>
                          Из них физ. лиц: <b>{result.scannedClients ?? 0}</b>
                        </li>
                        <li>
                          Добавлено новых адресов: <b>{result.added}</b>
                        </li>
                        <li>
                          Обновлено существующих записей: <b>{result.updated}</b>
                        </li>
                        <li>
                          Пропущено дубликатов: <b>{result.duplicateSkipped}</b>
                        </li>
                        <li>
                          Пропущено пустых: <b>{result.emptySkipped}</b>
                        </li>
                        <li>
                          Ошибок: <b>{result.failed}</b>
                        </li>
                        <li>
                          Совпадений DaData: <b>{result.dadataMatched}</b>
                        </li>
                        <li>
                          Без совпадения DaData: <b>{result.dadataNotMatched}</b>
                        </li>
                        <li>
                          Режим: <b>{result.validateWithDadata ? 'с DaData' : 'без DaData'}</b>
                        </li>
                      </ul>
                    </div>
                  )}
                </div>

                {result && result.details.length > 0 && (
                  <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6">
                    <h3 className="text-lg font-semibold text-gray-900 mb-3">Детальный отчёт</h3>
                    <div className="overflow-x-auto">
                      <table className="min-w-full text-sm">
                        <thead>
                          <tr className="text-left text-gray-500 border-b border-gray-200">
                            <th className="py-2 pr-4">Карточка</th>
                            <th className="py-2 pr-4">Legacy адрес</th>
                            <th className="py-2 pr-4">Нормализованный адрес</th>
                            <th className="py-2 pr-4">Действие</th>
                            <th className="py-2 pr-4">DaData</th>
                            <th className="py-2">Комментарий</th>
                          </tr>
                        </thead>
                        <tbody>
                          {result.details.map((item, idx) => (
                            <tr
                              key={`${item.ownerType ?? 'legal'}-${item.ownerId ?? item.legalEntityId ?? item.clientId ?? idx}-${idx}`}
                              className="border-b border-gray-100 align-top"
                            >
                              <td className="py-2 pr-4">
                                <div className="font-medium text-gray-900">{item.ownerName ?? item.legalEntityName ?? item.clientName ?? '—'}</div>
                                <div className="text-xs text-gray-500">
                                  {item.ownerType === 'client' ? 'Физ. лицо' : 'Юр. лицо'} · ID:{' '}
                                  {item.ownerId ?? item.legalEntityId ?? item.clientId ?? '—'}
                                </div>
                              </td>
                              <td className="py-2 pr-4 text-gray-700">{item.legacyAddress || '—'}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.normalizedAddress || '—'}</td>
                              <td className="py-2 pr-4">
                                <span className="inline-flex rounded-md bg-slate-100 px-2 py-1 text-xs font-medium text-slate-700">
                                  {item.action}
                                </span>
                              </td>
                              <td className="py-2 pr-4 text-gray-700">
                                {item.dadataMatched === null || item.dadataMatched === undefined
                                  ? '—'
                                  : item.dadataMatched
                                    ? 'Найдено'
                                    : 'Не найдено'}
                              </td>
                              <td className="py-2 text-gray-700">{item.message || '—'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                )}
              </>
            )}

            {activeSection === 'dictionary-merge' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div className="grid gap-4 md:grid-cols-3">
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">Справочник</span>
                    <select
                      value={mergeKind}
                      onChange={(event) => setMergeKind(event.target.value as MergeKind)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                    >
                      <option value="organization-forms">Формы организации</option>
                      <option value="countries">Страны</option>
                      <option value="base-currencies">Базовые валюты</option>
                      <option value="service-languages">Языки сервиса</option>
                      <option value="timezones">Часовые пояса</option>
                      <option value="tax-systems">Системы налогообложения</option>
                      <option value="tax-system-modes">Режимы налогообложения</option>
                    </select>
                  </label>

                  <label className="text-sm text-gray-700 md:col-span-2">
                    <span className="mb-1 block">Группа дублей</span>
                    <select
                      value={selectedGroupKey}
                      onChange={(event) => {
                        const canonical = event.target.value;
                        setSelectedGroupKey(canonical);
                        const group = duplicates.find((item) => item.canonical === canonical);
                        setPreferredId(group?.items?.[0]?.id ?? null);
                      }}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                    >
                      <option value="">Выберите группу</option>
                      {duplicates.map((group) => (
                        <option key={group.canonical} value={group.canonical}>
                          {group.items.map((item) => item.name).join(' / ')}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>

                {selectedGroupKey && (
                  <div className="rounded-xl border border-gray-200 p-4">
                    <p className="mb-2 text-sm font-medium text-gray-800">Выберите базовое значение</p>
                    <div className="space-y-2">
                      {(duplicates.find((item) => item.canonical === selectedGroupKey)?.items ?? []).map((item) => (
                        <label key={item.id} className="flex items-start gap-2 text-sm text-gray-700">
                          <input
                            type="radio"
                            checked={preferredId === item.id}
                            onChange={() => setPreferredId(item.id)}
                            className="mt-1"
                          />
                          <span>
                            <b>{item.name}</b>
                            <span className="text-gray-500"> · {item.description || '—'}</span>
                          </span>
                        </label>
                      ))}
                    </div>
                  </div>
                )}

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={runMerge}
                    disabled={mergeLoading || !selectedGroupKey}
                    className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60"
                  >
                    {mergeLoading ? 'Объединяем…' : 'Объединить выбранные дубли'}
                  </button>
                  <button
                    type="button"
                    onClick={() => void loadDuplicates(mergeKind)}
                    disabled={mergeLoading}
                    className="px-4 py-2 rounded-lg border border-gray-300 text-sm font-medium hover:bg-gray-50 disabled:opacity-60"
                  >
                    Обновить список
                  </button>
                </div>

                {mergeError && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{mergeError}</div>}
                {mergeMessage && <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">{mergeMessage}</div>}

                {!mergeLoading && duplicates.length === 0 && (
                  <div className="rounded-lg border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                    Дубли в выбранном справочнике не найдены.
                  </div>
                )}
              </div>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
