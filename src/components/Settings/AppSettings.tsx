import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { TablePagination } from '../ui/TablePagination';
import type {
  FormRequirementSectionMetadataDto,
  InvoiceCounterpartyCleanupResultDto,
  InvoiceLegacyMigrationSummaryDto,
  InvoiceNumberingSettingsDto,
  LegacyAddressMigrationResult,
} from '../../services/api';

type SettingsSection =
  | 'general'
  | 'integrations'
  | 'db-invoices'
  | 'db-addresses'
  | 'dictionary-merge'
  | 'form-requirements';
type MergeKind = 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes';
const DETAILS_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

export function AppSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
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
  const [invoiceCounterpartyCleanupLoading, setInvoiceCounterpartyCleanupLoading] = useState(false);
  const [invoiceCounterpartyCleanupMessage, setInvoiceCounterpartyCleanupMessage] = useState<string | null>(null);
  const [invoiceCounterpartyCleanupResult, setInvoiceCounterpartyCleanupResult] = useState<InvoiceCounterpartyCleanupResultDto | null>(null);
  const [invoiceMigrationLoading, setInvoiceMigrationLoading] = useState(false);
  const [invoiceMigrationError, setInvoiceMigrationError] = useState<string | null>(null);
  const [invoiceMigrationResult, setInvoiceMigrationResult] = useState<InvoiceLegacyMigrationSummaryDto | null>(null);
  const [detailsPage, setDetailsPage] = useState(1);
  const [detailsPageSize, setDetailsPageSize] = useState(DETAILS_PAGE_SIZE_OPTIONS[1]);

  const activeSectionTitle = useMemo(() => {
    if (activeSection === 'general') return 'Основные';
    if (activeSection === 'integrations') return 'Ключи интеграций';
    if (activeSection === 'dictionary-merge') return 'Слияние дублей справочников';
    if (activeSection === 'form-requirements') return 'Обязательные поля форм';
    if (activeSection === 'db-invoices') return 'Работа с базой · Счета';
    return 'Работа с базой · Адреса';
  }, [activeSection]);

  const migrationDetails = result?.details ?? [];
  const migrationDetailsTotalPages = useMemo(
    () => Math.max(1, Math.ceil(migrationDetails.length / detailsPageSize || 1)),
    [detailsPageSize, migrationDetails.length],
  );
  const pagedMigrationDetails = useMemo(() => {
    const offset = (detailsPage - 1) * detailsPageSize;
    return migrationDetails.slice(offset, offset + detailsPageSize);
  }, [detailsPage, detailsPageSize, migrationDetails]);

  const getErrorMessage = (error: unknown, fallback: string) =>
    extractDisplayErrorMessage(error, fallback);

  const handleReloadDuplicates = async () => {
    const approved = await confirm({
      title: '????????????? ??????????',
      description: '???????? ?????? ?????? ??? ?????????? ????????????',
      confirmLabel: '????????',
      cancelLabel: '??????',
    });
    if (!approved) return;

    await loadDuplicates(mergeKind, { showSuccessToast: true, showErrorToast: true });
  };

  useEffect(() => {
    setDetailsPage(1);
  }, [detailsPageSize, result]);

  useEffect(() => {
    setDetailsPage((prev) => Math.min(prev, migrationDetailsTotalPages));
  }, [migrationDetailsTotalPages]);

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
      const message = '????????? ???????????? ????? ?????????';
      setRequirementsMessage(message);
      toast.success(message, '?????????');
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ?????????');
      setRequirementsMessage(message);
      toast.error(message, '?????????');
      setRequirementsLoading(false);
    }
  };

  const loadDuplicates = async (
    kind: MergeKind,
    options?: { showSuccessToast?: boolean; showErrorToast?: boolean },
  ) => {
    setMergeLoading(true);
    setMergeError(null);
    setMergeMessage(null);
    try {
      const data = await apiService.getSimpleDictionaryDuplicates(kind);
      setDuplicates(data ?? []);
      const first = (data ?? [])[0];
      setSelectedGroupKey(first?.canonical ?? '');
      setPreferredId(first?.items?.[0]?.id ?? null);
      if (options?.showSuccessToast) {
        toast.success(
          (data?.length ?? 0) > 0
            ? `??????? ????? ??????: ${data?.length ?? 0}`
            : '????? ? ????????? ??????????? ?? ???????',
          '???????????',
        );
      }
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ?????');
      setMergeError(message);
      setDuplicates([]);
      setSelectedGroupKey('');
      setPreferredId(null);
      if (options?.showErrorToast) {
        toast.error(message, '???????????');
      }
    } finally {
      setMergeLoading(false);
    }
  };

  const runMerge = async () => {
    const group = duplicates.find((item) => item.canonical === selectedGroupKey);
    if (!group || group.items.length < 2 || !preferredId) {
      const message = '???????? ?????? ?????? ? ??????? ????????';
      setMergeError(message);
      toast.error(message, '???????????');
      return;
    }

    const approved = await confirm({
      title: '????????????? ???????????',
      description: '?????????? ????????? ????? ? ???? ????????? ????????? ???????? ????????? ????????.',
      confirmLabel: '??????????',
      cancelLabel: '??????',
      variant: 'danger',
    });
    if (!approved) return;

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
      const message = `??????????? ?????????: ??????? "${result.name}", ????????? ?????: ${result.replacedInLegalEntities}`;
      setMergeMessage(message);
      toast.success(message, '???????????');
      await loadDuplicates(mergeKind);
      window.dispatchEvent(new CustomEvent('dicts:changed'));
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ???????????');
      setMergeError(message);
      toast.error(message, '???????????');
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
    const approved = await confirm({
      title: '????????????? ??????????',
      description: '????????? ????? DaData ? ?????????? ???????????',
      confirmLabel: '?????????',
      cancelLabel: '??????',
    });
    if (!approved) return;

    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      await Promise.all([
        apiService.upsertUserSetting('integrations.dadata.apiKey', dadataToken.trim()),
        apiService.upsertUserSetting('integrations.dadata.secret', dadataSecret.trim()),
      ]);
      const message = '????? DaData ?????????';
      setCredentialsMessage(message);
      toast.success(message, '??????????');
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ????? DaData');
      setCredentialsMessage(message);
      toast.error(message, '??????????');
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
    const approved = await confirm({
      title: '????????????? ??????????',
      description: '????????? ????????? ????????? ???????',
      confirmLabel: '?????????',
      cancelLabel: '??????',
    });
    if (!approved) return;

    setInvoiceNumberingLoading(true);
    setInvoiceNumberingMessage(null);
    try {
      const response = await apiService.updateInvoiceNumberingSettings(invoiceNumbering);
      setInvoiceNumbering(response);
      const message = '????????? ????????? ?????? ?????????';
      setInvoiceNumberingMessage(message);
      toast.success(message, '?????????');
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ????????? ????????? ??????');
      setInvoiceNumberingMessage(message);
      toast.error(message, '?????????');
    } finally {
      setInvoiceNumberingLoading(false);
    }
  };

  const runInvoiceCounterpartyCleanup = async () => {
    const approved = await confirm({
      title: '????????????? ???????',
      description: '???????? ?????????? ???? "?? ???? ????" ?? ???? ??????? ???????? ??????????.',
      confirmLabel: '????????',
      cancelLabel: '??????',
      variant: 'danger',
    });
    if (!approved) return;

    setInvoiceCounterpartyCleanupLoading(true);
    setInvoiceCounterpartyCleanupMessage(null);
    try {
      const response = await apiService.cleanupInvoiceCounterpartyNames();
      setInvoiceCounterpartyCleanupResult(response);
      const message = '??????? ???? "?? ???? ????" ?????????';
      setInvoiceCounterpartyCleanupMessage(message);
      toast.success(message, '?????');
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ???????? ???? "?? ???? ????"');
      setInvoiceCounterpartyCleanupResult(null);
      setInvoiceCounterpartyCleanupMessage(message);
      toast.error(message, '?????');
    } finally {
      setInvoiceCounterpartyCleanupLoading(false);
    }
  };

  const runMigration = async () => {
    const approved = await confirm({
      title: '????????????? ????????',
      description: '????????? ??????? ?????? ???????? ???????? ????? ????????? ??????? ?????????? ????????.',
      confirmLabel: '?????????',
      cancelLabel: '??????',
      variant: 'danger',
    });
    if (!approved) return;

    setLoading(true);
    setError(null);
    try {
      const response = await apiService.migrateLegacyLegalEntityAddresses(validateWithDadata);
      setResult(response);
      toast.success(
        `??????? ??????? ????????. ?????????: ${response.added}, ?????????: ${response.updated}, ??????: ${response.failed}`,
        '???? ??????',
      );
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ??????? ???????');
      setError(message);
      toast.error(message, '???? ??????');
    } finally {
      setLoading(false);
    }
  };

  const runInvoiceMigration = async () => {
    const approved = await confirm({
      title: '????????????? ??????????',
      description: '???????? ????? ? ????? ?? legacy-?????????? ???????? ??????? ???????????? ????????.',
      confirmLabel: '?????????',
      cancelLabel: '??????',
      variant: 'danger',
    });
    if (!approved) return;

    setInvoiceMigrationLoading(true);
    setInvoiceMigrationError(null);
    try {
      const response = await apiService.migrateLegacyInvoiceLinks();
      setInvoiceMigrationResult(response);
      toast.success(
        `?????????? ?????? ?????????. ??????? ??????: ${response.createdInvoices}, ????? ????????: ${response.linkedPayments}`,
        '?????',
      );
    } catch (e) {
      const message = getErrorMessage(e, '?? ??????? ????????? ??????? ??????');
      setInvoiceMigrationError(message);
      toast.error(message, '?????');
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
                <div className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">Работа с базой</div>
                <button
                  type="button"
                  onClick={() => setActiveSection('db-invoices')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'db-invoices'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Счета
                </button>
                <button
                  type="button"
                  onClick={() => setActiveSection('db-addresses')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'db-addresses'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Адреса
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
                    : activeSection === 'db-invoices'
                      ? 'Операции по формированию канонических счетов и перепривязке платежей к актуальным сущностям счета.'
                      : 'Операции по переносу и выравниванию адресов. Позже здесь можно будет добавить другие сервисные процедуры.'}
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

                <div className="border-t border-gray-200 pt-4">
                  <h4 className="text-base font-semibold text-gray-900">Очистка устаревшего поля счета</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Удаляет сохраненные значения поля "От кого счёт" и снимает старое требование заполнения.
                  </p>
                  <div className="mt-3 flex items-center gap-3">
                    <button
                      type="button"
                      onClick={runInvoiceCounterpartyCleanup}
                      disabled={invoiceCounterpartyCleanupLoading}
                      className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {invoiceCounterpartyCleanupLoading ? 'Очищаем…' : 'Очистить поле'}
                    </button>
                    {invoiceCounterpartyCleanupMessage && (
                      <span className="text-sm text-gray-600">{invoiceCounterpartyCleanupMessage}</span>
                    )}
                  </div>
                  {invoiceCounterpartyCleanupResult && (
                    <div className="mt-3 rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p>Очищено счетов: {invoiceCounterpartyCleanupResult.cleared}</p>
                      <p>Удалено legacy-требований: {invoiceCounterpartyCleanupResult.removedRequirements}</p>
                    </div>
                  )}
                </div>
              </div>
            )}

            {activeSection === 'db-invoices' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div>
                  <h4 className="text-base font-semibold text-gray-900">Формирование канонических счетов из операций</h4>
                  <p className="text-sm text-gray-600 mt-1">
                    Операция анализирует платежи, у которых заполнен legacy-счет, и формирует канонические счета с привязкой по клиенту, номеру счета, году и делу.
                    Платежи с одинаковым счетом и одним делом объединяются в один счет; одинаковый номер счета, но разные дела, разделяются на разные счета.
                    После этого платежи перепривязываются к актуальному счету через `invoiceId`.
                  </p>
                </div>

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={runInvoiceMigration}
                    disabled={invoiceMigrationLoading}
                    className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {invoiceMigrationLoading ? 'Обрабатываем…' : 'Обновить счета и связи'}
                  </button>
                </div>

                {invoiceMigrationError && (
                  <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{invoiceMigrationError}</div>
                )}

                {invoiceMigrationResult && (
                  <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                    <p className="font-medium mb-2">Сводка обновления счетов</p>
                    <ul className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
                      <li>Проверено платежей: <b>{invoiceMigrationResult.scannedPayments}</b></li>
                      <li>Из них legacy invoice-строк: <b>{invoiceMigrationResult.invoicePayments}</b></li>
                      <li>Уже были связаны правильно: <b>{invoiceMigrationResult.alreadyLinked}</b></li>
                      <li>Пропущено без номера: <b>{invoiceMigrationResult.missingNumberSkipped}</b></li>
                      <li>Пропущено без клиента: <b>{invoiceMigrationResult.missingClientSkipped}</b></li>
                      <li>Групп к обработке: <b>{invoiceMigrationResult.distinctLegacyGroups}</b></li>
                      <li>Групп с делом: <b>{invoiceMigrationResult.groupsWithCase}</b></li>
                      <li>Групп без дела: <b>{invoiceMigrationResult.groupsWithoutCase}</b></li>
                      <li>Связано с существующими счетами: <b>{invoiceMigrationResult.linkedToExistingInvoice}</b></li>
                      <li>Создано новых счетов: <b>{invoiceMigrationResult.createdInvoices}</b></li>
                      <li>Новых привязок платежей: <b>{invoiceMigrationResult.linkedPayments}</b></li>
                      <li>Перепривязано к новым счетам: <b>{invoiceMigrationResult.relinkedPayments}</b></li>
                      <li>Обновлено карточек счета/дела: <b>{invoiceMigrationResult.representativePaymentsUpdated}</b></li>
                      <li>Legacy-групп по ключу: <b>{invoiceMigrationResult.distinctLegacyInvoiceKeys}</b></li>
                      <li>Дубликатов ключей в счетах: <b>{invoiceMigrationResult.existingDuplicateInvoiceKeys}</b></li>
                      <li>Общих счетов на несколько ключей: <b>{invoiceMigrationResult.sharedInvoiceEntities}</b></li>
                      <li>Групп, которые пришлось разделить: <b>{invoiceMigrationResult.splitSharedInvoiceGroups}</b></li>
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

            {activeSection === 'db-addresses' && (
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
                      <table className="min-w-full table-fixed text-sm">
                        <colgroup>
                          <col style={{ width: '18%' }} />
                          <col style={{ width: '22%' }} />
                          <col style={{ width: '22%' }} />
                          <col style={{ width: '12%' }} />
                          <col style={{ width: '10%' }} />
                          <col style={{ width: '16%' }} />
                        </colgroup>
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
                          {pagedMigrationDetails.map((item, idx) => (
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
                    <TablePagination
                      page={detailsPage}
                      totalPages={migrationDetailsTotalPages}
                      totalItems={migrationDetails.length}
                      pageSize={detailsPageSize}
                      pageSizeOptions={DETAILS_PAGE_SIZE_OPTIONS}
                      onPageChange={setDetailsPage}
                      onPageSizeChange={setDetailsPageSize}
                    />
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
                    onClick={handleReloadDuplicates}
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
