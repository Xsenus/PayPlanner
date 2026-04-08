import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Building2,
  Download,
  GripVertical,
  Loader2,
  Mail,
  Pencil,
  Phone,
  Plus,
  Settings2,
  Trash2,
  Upload,
  Users,
} from 'lucide-react';
import {
  apiService,
  type ContactDirectoryItemDto,
  type ContactScope,
  type YealinkContactImportDecisionAction,
  type YealinkContactImportResultDto,
} from '../../services/api';
import type { ActResponsible, LegalEntitySummary } from '../../types';
import { useAuth } from '../../contexts/AuthContext';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { ContactEditorModal } from './ContactEditorModal';
import { YealinkImportPreviewModal } from './YealinkImportPreviewModal';

type EditorState =
  | { mode: 'create-external'; contact: null }
  | { mode: 'edit-client'; contact: ContactDirectoryItemDto }
  | { mode: 'edit-external'; contact: ContactDirectoryItemDto };

type YealinkImportState = {
  file: File;
  preview: YealinkContactImportResultDto;
  decisions: Record<number, YealinkContactImportDecisionAction>;
};

interface ContactsProps {
  initialScope?: ContactScope;
  onScopeChange?: (scope: ContactScope) => void;
}

const PAGE_SIZE_OPTIONS = [20, 50, 100];

const CONTACT_COLUMNS = [
  { key: 'contact', label: 'Контакт', defaultVisible: true, defaultWidth: 320 },
  { key: 'organization', label: 'Организация', defaultVisible: true, defaultWidth: 240 },
  { key: 'phones', label: 'Телефоны', defaultVisible: true, defaultWidth: 220 },
  { key: 'email', label: 'Email', defaultVisible: true, defaultWidth: 220 },
  { key: 'status', label: 'Статус', defaultVisible: true, defaultWidth: 180 },
] as const;

type ContactColumnKey = (typeof CONTACT_COLUMNS)[number]['key'];

const CONTACT_COLUMN_LABELS: Record<ContactColumnKey, string> = CONTACT_COLUMNS.reduce(
  (acc, column) => ({ ...acc, [column.key]: column.label }),
  {} as Record<ContactColumnKey, string>,
);

function formatPhones(contact: ContactDirectoryItemDto) {
  const list = contact.phones.length > 0 ? contact.phones : contact.phone ? [contact.phone] : [];
  return list.join(', ');
}

function formatEmails(contact: ContactDirectoryItemDto) {
  const list = contact.emails.length > 0 ? contact.emails : contact.email ? [contact.email] : [];
  return list.join(', ');
}

function sourceBadge(sourceType: ContactDirectoryItemDto['sourceType']) {
  return sourceType === 'client'
    ? 'border-blue-200 bg-blue-50 text-blue-700'
    : 'border-violet-200 bg-violet-50 text-violet-700';
}

function sourceLabel(sourceType: ContactDirectoryItemDto['sourceType']) {
  return sourceType === 'client' ? 'Клиент' : 'Сторонний';
}

export function Contacts({ initialScope = 'all', onScopeChange }: ContactsProps) {
  const { isAdmin } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const [scope, setScope] = useState<ContactScope>(initialScope);
  const [searchInput, setSearchInput] = useState('');
  const debouncedSearch = useDebouncedValue(searchInput, 450);
  const [legalEntityId, setLegalEntityId] = useState('');
  const [syncOnly, setSyncOnly] = useState<'all' | 'only-sync' | 'excluded'>('all');
  const [includeInactive, setIncludeInactive] = useState(false);
  const [contacts, setContacts] = useState<ContactDirectoryItemDto[]>([]);
  const [summaryContacts, setSummaryContacts] = useState<ContactDirectoryItemDto[]>([]);
  const [legalEntities, setLegalEntities] = useState<LegalEntitySummary[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [downloadingXml, setDownloadingXml] = useState(false);
  const [importingXml, setImportingXml] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [editor, setEditor] = useState<EditorState | null>(null);
  const [yealinkImport, setYealinkImport] = useState<YealinkImportState | null>(null);
  const [page, setPage] = useState(1);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const requestIdRef = useRef(0);
  const hasLoadedRef = useRef(false);
  const importInputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    setScope(initialScope);
    setPage(1);
  }, [initialScope]);

  const {
    columnOrder,
    orderedVisibleColumns,
    visibleColumns,
    pageSize,
    setPageSize,
    draggingColumn,
    toggleColumn,
    moveColumn,
    handleColumnDragStart,
    handleColumnDragOver,
    handleColumnDrop,
    handleColumnDragEnd,
    handleResizeStart,
    getColumnWidth,
    resetTableSettings,
  } = usePersistedTableSettings<ContactColumnKey>({
    storageKey: 'pp.contacts_table_settings.v1',
    settingsKey: 'contacts_table_settings',
    columns: CONTACT_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: PAGE_SIZE_OPTIONS[0],
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 140,
  });

  const loadDirectory = useCallback(async () => {
    const requestId = ++requestIdRef.current;
    const nextMode = hasLoadedRef.current ? 'refresh' : 'initial';

    try {
      if (nextMode === 'initial') {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);

      const baseParams = {
        search: debouncedSearch.trim() || undefined,
        legalEntityId: legalEntityId ? Number(legalEntityId) : undefined,
        includeInactive,
        syncOnly: syncOnly === 'all' ? undefined : syncOnly === 'only-sync',
      };

      const dataRequest = apiService.getContacts({
        ...baseParams,
        scope,
      });
      const summaryRequest =
        scope === 'all'
          ? dataRequest
          : apiService.getContacts({
              ...baseParams,
              scope: 'all',
            });
      const [data, summaryData] = await Promise.all([dataRequest, summaryRequest]);

      if (requestId !== requestIdRef.current) {
        return;
      }

      setContacts(data);
      setSummaryContacts(summaryData);
    } catch (nextError) {
      if (requestId !== requestIdRef.current) {
        return;
      }
      setError(extractDisplayErrorMessage(nextError, 'Не удалось загрузить контакты'));
    } finally {
      if (requestId === requestIdRef.current) {
        hasLoadedRef.current = true;
        setLoading(false);
        setRefreshing(false);
      }
    }
  }, [debouncedSearch, includeInactive, legalEntityId, scope, syncOnly]);

  const handleScopeChange = (nextScope: ContactScope) => {
    setScope(nextScope);
    setPage(1);
    onScopeChange?.(nextScope);
  };

  useEffect(() => {
    void loadDirectory();
  }, [loadDirectory]);

  useEffect(() => {
    setPage(1);
  }, [scope, debouncedSearch, legalEntityId, syncOnly, includeInactive]);

  useEffect(() => {
    const loadLookups = async () => {
      try {
        const [entities, nextResponsibles] = await Promise.all([
          apiService.getLegalEntities(),
          isAdmin() ? apiService.getActResponsibles() : Promise.resolve([]),
        ]);

        setLegalEntities(entities);
        setResponsibles(nextResponsibles);
      } catch {
        // lookup issues should not block the directory itself
      }
    };

    void loadLookups();
  }, [isAdmin]);

  const counts = useMemo(
    () => ({
      all: summaryContacts.length,
      clients: summaryContacts.filter((item) => item.sourceType === 'client').length,
      external: summaryContacts.filter((item) => item.sourceType === 'external').length,
    }),
    [summaryContacts],
  );

  const totalPages = Math.max(1, Math.ceil(contacts.length / pageSize));
  const pagedContacts = useMemo(() => {
    const start = (page - 1) * pageSize;
    return contacts.slice(start, start + pageSize);
  }, [contacts, page, pageSize]);

  useEffect(() => {
    setPage((current) => Math.min(current, totalPages));
  }, [totalPages]);

  const handleSaved = () => {
    void loadDirectory();
  };

  const handleDelete = async (contact: ContactDirectoryItemDto) => {
    const approved = await confirm({
      title: 'Удалить контакт',
      description: `Удалить контакт "${contact.fullName}" из справочника?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    try {
      await apiService.deleteExternalContact(contact.sourceId);
      setContacts((prev) =>
        prev.filter((item) => !(item.sourceType === contact.sourceType && item.sourceId === contact.sourceId)),
      );
      setSummaryContacts((prev) =>
        prev.filter((item) => !(item.sourceType === contact.sourceType && item.sourceId === contact.sourceId)),
      );
      toast.success('Контакт удалён', 'Контакты');
    } catch (nextError) {
      toast.error(extractDisplayErrorMessage(nextError, 'Не удалось удалить контакт'), 'Контакты');
    }
  };

  const handleDownloadXml = async () => {
    setDownloadingXml(true);
    try {
      const response = await apiService.downloadYealinkContactsXml();
      const href = window.URL.createObjectURL(response.blob);
      const link = document.createElement('a');
      link.href = href;
      link.download = response.fileName || 'RemotePhonebook.xml';
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.URL.revokeObjectURL(href);
      toast.success('Yealink XML подготовлен', 'Контакты');
    } catch (nextError) {
      toast.error(extractDisplayErrorMessage(nextError, 'Не удалось скачать XML'), 'Контакты');
    } finally {
      setDownloadingXml(false);
    }
  };

  const handleImportXmlFile = async (file: File | null | undefined) => {
    if (!file) {
      return;
    }

    setImportingXml(true);
    try {
      toast.info('Читаю XML и проверяю совпадения по телефонам...', 'Контакты');
      const preview = await apiService.importYealinkContactsXml(file, { dryRun: true });
      const decisions = preview.details.reduce<Record<number, YealinkContactImportDecisionAction>>((acc, detail) => {
        if (detail.action === 'Existing' || detail.action === 'SkippedExisting') {
          acc[detail.rowId] = 'skip';
        } else if (detail.action === 'WouldCreateExternal' || detail.action === 'WouldCreate') {
          acc[detail.rowId] = 'createExternal';
        }
        return acc;
      }, {});

      setYealinkImport({ file, preview, decisions });
      toast.success(
        `Файл прочитан: ${preview.scanned} записей, новых к созданию ${preview.created}, найдено в CRM ${preview.skippedExisting}.`,
        'Контакты',
      );
    } catch (nextError) {
      toast.error(extractDisplayErrorMessage(nextError, 'Не удалось импортировать Yealink XML'), 'Контакты');
    } finally {
      setImportingXml(false);
      if (importInputRef.current) {
        importInputRef.current.value = '';
      }
    }
  };

  const handleChangeImportDecision = (rowId: number, action: YealinkContactImportDecisionAction) => {
    setYealinkImport((current) =>
      current
        ? {
            ...current,
            decisions: {
              ...current.decisions,
              [rowId]: action,
            },
          }
        : current,
    );
  };

  const handleCloseImportPreview = () => {
    if (importingXml) {
      return;
    }

    setYealinkImport(null);
    toast.info('Импорт Yealink XML отменён.', 'Контакты');
  };

  const handleConfirmImportPreview = async () => {
    if (!yealinkImport) {
      return;
    }

    const decisions = Object.entries(yealinkImport.decisions).map(([rowId, action]) => ({
      rowId: Number(rowId),
      action,
    }));
    const createCount = decisions.filter((item) => item.action === 'createExternal' || item.action === 'createClient').length;
    const updateCount = decisions.filter((item) => item.action === 'updateExisting').length;
    const skipCount = decisions.filter((item) => item.action === 'skip').length;

    const approved = await confirm({
      title: 'Импортировать выбранные контакты?',
      description: `Будет создано: ${createCount}. Обновлено: ${updateCount}. Оставлено без изменений или пропущено: ${skipCount}.`,
      confirmLabel: 'Импортировать',
      cancelLabel: 'Отмена',
      variant: 'default',
    });

    if (!approved) {
      toast.info('Импорт Yealink XML отменён.', 'Контакты');
      return;
    }

    setImportingXml(true);
    try {
      const result = await apiService.importYealinkContactsXml(yealinkImport.file, { decisions });
      toast.success(
        `Импорт завершён: создано ${result.created}, обновлено ${result.updated}, оставлено ${result.skippedExisting}, пропущено ${result.skippedByUser}.`,
        'Контакты',
      );
      setYealinkImport(null);
      void loadDirectory();
    } catch (nextError) {
      toast.error(extractDisplayErrorMessage(nextError, 'Не удалось импортировать выбранные контакты'), 'Контакты');
    } finally {
      setImportingXml(false);
    }
  };

  const renderContactCell = (contact: ContactDirectoryItemDto, column: ContactColumnKey) => {
    switch (column) {
      case 'contact':
        return (
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <div className="truncate text-base font-semibold text-slate-900">{contact.fullName}</div>
              <span className={`rounded-full border px-2.5 py-1 text-xs font-semibold ${sourceBadge(contact.sourceType)}`}>
                {sourceLabel(contact.sourceType)}
              </span>
            </div>
            {contact.position ? <div className="truncate text-sm text-slate-600">{contact.position}</div> : null}
            {contact.powerOfAttorney ? (
              <div className="truncate text-sm text-slate-500">Доверенность: {contact.powerOfAttorney}</div>
            ) : null}
            {contact.responsibleUserName ? (
              <div className="truncate text-sm text-slate-500">Ответственный: {contact.responsibleUserName}</div>
            ) : null}
          </div>
        );
      case 'organization':
        return (
          <div className="flex items-start gap-2 text-sm text-slate-700">
            <Building2 className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" />
            <span className="truncate">{contact.organizationName || 'Без привязки'}</span>
          </div>
        );
      case 'phones':
        return (
          <div className="flex items-start gap-2 text-sm text-slate-700">
            <Phone className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" />
            <span className="truncate">{formatPhones(contact) || '—'}</span>
          </div>
        );
      case 'email':
        return (
          <div className="flex items-start gap-2 text-sm text-slate-700">
            <Mail className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" />
            <span className="truncate">{formatEmails(contact) || '—'}</span>
          </div>
        );
      case 'status':
        return (
          <div className="space-y-2">
            <span
              className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${
                contact.isActive ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'
              }`}
            >
              {contact.isActive ? 'Активен' : 'Неактивен'}
            </span>
            <div>
              <span
                className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${
                  contact.syncToPhonebook ? 'bg-blue-50 text-blue-700' : 'bg-amber-50 text-amber-700'
                }`}
              >
                {contact.syncToPhonebook ? 'В телефонии' : 'Без синхронизации'}
              </span>
            </div>
          </div>
        );
      default:
        return null;
    }
  };

  return (
    <div className="space-y-6 p-8">
      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-6 xl:flex-row xl:items-start xl:justify-between">
          <div>
            <div className="flex items-center gap-3">
              <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-blue-50 text-blue-600">
                <Users className="h-6 w-6" />
              </div>
              <div>
                <h1 className="text-4xl font-bold tracking-tight text-slate-900">Контакты</h1>
                <p className="mt-1 text-base text-slate-600">
                  Единый справочник физических лиц из клиентов и сторонних контактов для CRM и телефонии.
                </p>
              </div>
            </div>

            <div className="mt-5 flex flex-wrap gap-2">
              {([
                { key: 'all', label: 'Все', count: counts.all },
                { key: 'clients', label: 'Клиенты', count: counts.clients },
                { key: 'external', label: 'Сторонние', count: counts.external },
              ] as const).map((item) => (
                <button
                  key={item.key}
                  type="button"
                  onClick={() => handleScopeChange(item.key)}
                  className={`inline-flex items-center gap-2 rounded-2xl border px-4 py-2 text-sm font-semibold transition ${
                    scope === item.key
                      ? 'border-blue-200 bg-blue-50 text-blue-700'
                      : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                  }`}
                >
                  <span>{item.label}</span>
                  <span className="rounded-full bg-white/80 px-2 py-0.5 text-xs text-slate-500">{item.count}</span>
                </button>
              ))}
            </div>
          </div>

          <div className="flex flex-wrap gap-3">
            {isAdmin() ? (
              <>
                <input
                  ref={importInputRef}
                  type="file"
                  accept=".xml,text/xml,application/xml"
                  className="hidden"
                  onChange={(event) => void handleImportXmlFile(event.target.files?.[0])}
                />
                <button
                  type="button"
                  onClick={() => importInputRef.current?.click()}
                  disabled={importingXml}
                  className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {importingXml ? <Loader2 className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
                  Импорт XML
                </button>
                <button
                  type="button"
                  onClick={handleDownloadXml}
                  disabled={downloadingXml}
                  className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {downloadingXml ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                  Скачать Yealink XML
                </button>
              </>
            ) : null}

            <button
              type="button"
              onClick={() => setEditor({ mode: 'create-external', contact: null })}
              className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-3 text-sm font-semibold text-white transition hover:bg-blue-700"
            >
              <Plus className="h-4 w-4" />
              Добавить сторонний контакт
            </button>
          </div>
        </div>
      </div>

      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="grid gap-3 xl:grid-cols-[minmax(220px,1fr)_260px_220px_auto]">
          <label className="block">
            <span className="text-sm font-medium text-slate-700">Поиск</span>
            <input
              type="search"
              value={searchInput}
              onChange={(event) => setSearchInput(event.target.value)}
              placeholder="ФИО, телефон, email, организация"
              className="mt-1 w-full rounded-2xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
            />
          </label>

          <label className="block">
            <span className="text-sm font-medium text-slate-700">Организация</span>
            <select
              value={legalEntityId}
              onChange={(event) => setLegalEntityId(event.target.value)}
              className="mt-1 w-full rounded-2xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
            >
              <option value="">Все организации</option>
              {legalEntities.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.shortName}
                </option>
              ))}
            </select>
          </label>

          <label className="block">
            <span className="text-sm font-medium text-slate-700">Телефония</span>
            <select
              value={syncOnly}
              onChange={(event) => setSyncOnly(event.target.value as typeof syncOnly)}
              className="mt-1 w-full rounded-2xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
            >
              <option value="all">Все контакты</option>
              <option value="only-sync">Только в синхронизации</option>
              <option value="excluded">Исключённые из синхронизации</option>
            </select>
          </label>

          <label className="flex items-end">
            <span className="flex w-full items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
              <input
                type="checkbox"
                checked={includeInactive}
                onChange={(event) => setIncludeInactive(event.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
              />
              Показывать неактивные
            </span>
          </label>
        </div>
      </div>

      <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm">
        <div className="border-b border-slate-200 px-6 py-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <h2 className="text-lg font-semibold text-slate-900">Справочник контактов</h2>
              <p className="text-sm text-slate-500">
                Клиентские контакты редактируются поверх карточек клиентов, сторонние живут как отдельные записи.
              </p>
            </div>
            <div className="flex flex-wrap items-center justify-end gap-3">
              {refreshing ? (
                <div className="inline-flex items-center gap-2 text-sm text-slate-500">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Обновляем список…
                </div>
              ) : null}
              <button
                type="button"
                onClick={() => setSettingsOpen((value) => !value)}
                className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                <Settings2 className="h-4 w-4" />
                {settingsOpen ? 'Скрыть настройки' : 'Настроить таблицу'}
              </button>
            </div>
          </div>
        </div>

        {settingsOpen ? (
          <div className="border-b border-slate-200 px-6 py-4">
            <TableColumnSettings
              columns={CONTACT_COLUMNS.map((column) => ({
                key: column.key,
                label: column.label,
              }))}
              columnOrder={columnOrder}
              visibleColumns={visibleColumns}
              draggingColumn={draggingColumn}
              onToggleColumn={toggleColumn}
              onMoveColumn={moveColumn}
              onDragStart={handleColumnDragStart}
              onDragOver={handleColumnDragOver}
              onDrop={handleColumnDrop}
              onDragEnd={handleColumnDragEnd}
              onReset={resetTableSettings}
              footer={
                <div className="flex items-center gap-2 text-sm text-slate-600">
                  <span>Строк на странице</span>
                  <select
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                  >
                    {PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </select>
                </div>
              }
            />
          </div>
        ) : null}

        {loading ? (
          <div className="flex items-center justify-center px-6 py-20 text-slate-500">
            <Loader2 className="mr-3 h-5 w-5 animate-spin" />
            Загружаем контакты…
          </div>
        ) : error ? (
          <div className="px-6 py-10">
            <div className="rounded-2xl border border-rose-200 bg-rose-50 px-4 py-4 text-sm text-rose-700">{error}</div>
          </div>
        ) : contacts.length === 0 ? (
          <div className="px-6 py-14 text-center text-slate-500">По текущим фильтрам контакты не найдены.</div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full min-w-[1120px] table-fixed divide-y divide-slate-200">
                <colgroup>
                  {orderedVisibleColumns.map((columnKey) => (
                    <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                  ))}
                  <col style={{ width: 140 }} />
                </colgroup>
                <thead className="bg-slate-50">
                  <tr className="text-left text-xs font-semibold uppercase tracking-[0.2em] text-slate-500">
                    {orderedVisibleColumns.map((columnKey) => (
                      <th
                        key={columnKey}
                        style={{ width: getColumnWidth(columnKey) }}
                        className={`relative px-6 py-4 ${
                          draggingColumn === columnKey ? 'bg-blue-50 text-blue-700' : 'text-slate-500'
                        }`}
                        onDragOver={handleColumnDragOver}
                        onDrop={(event) => handleColumnDrop(columnKey, event)}
                      >
                        <div
                          className="inline-flex min-w-0 cursor-grab select-none items-center gap-2 active:cursor-grabbing"
                          draggable
                          onDragStart={(event) => handleColumnDragStart(columnKey, event)}
                          onDragEnd={handleColumnDragEnd}
                          title="Перетащите, чтобы изменить порядок"
                        >
                          <GripVertical className="h-4 w-4 text-slate-400" />
                          <span className="truncate">{CONTACT_COLUMN_LABELS[columnKey]}</span>
                        </div>
                        <div
                          role="separator"
                          aria-label={`Изменить ширину колонки ${CONTACT_COLUMN_LABELS[columnKey]}`}
                          className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                          onMouseDown={(event) => handleResizeStart(columnKey, event)}
                          onDragStart={(event) => event.preventDefault()}
                        />
                      </th>
                    ))}
                    <th className="px-6 py-4 text-right text-slate-500">Действия</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {pagedContacts.map((contact) => (
                    <tr key={`${contact.sourceType}-${contact.sourceId}`} className="align-top hover:bg-slate-50">
                      {orderedVisibleColumns.map((columnKey) => (
                        <td
                          key={columnKey}
                          style={{ width: getColumnWidth(columnKey) }}
                          className="max-w-0 overflow-hidden px-6 py-4 align-top"
                        >
                          {renderContactCell(contact, columnKey)}
                        </td>
                      ))}
                      <td className="px-6 py-4">
                        <div className="flex items-center justify-end gap-2">
                          <button
                            type="button"
                            onClick={() =>
                              setEditor({
                                mode: contact.sourceType === 'client' ? 'edit-client' : 'edit-external',
                                contact,
                              })
                            }
                            className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-600 transition hover:bg-slate-50 hover:text-slate-900"
                            aria-label="Редактировать"
                          >
                            <Pencil className="h-4 w-4" />
                          </button>
                          {contact.sourceType === 'external' ? (
                            <button
                              type="button"
                              onClick={() => void handleDelete(contact)}
                              className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-rose-200 text-rose-600 transition hover:bg-rose-50"
                              aria-label="Удалить"
                            >
                            <Trash2 className="h-4 w-4" />
                          </button>
                        ) : null}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <TablePagination
              page={page}
              totalPages={totalPages}
              totalItems={contacts.length}
              pageSize={pageSize}
              pageSizeOptions={PAGE_SIZE_OPTIONS}
              onPageChange={setPage}
              onPageSizeChange={setPageSize}
            />
          </>
        )}
      </div>

      <ContactEditorModal
        open={Boolean(editor)}
        mode={editor?.mode ?? 'create-external'}
        contact={editor?.contact ?? null}
        legalEntities={legalEntities}
        responsibles={responsibles}
        canManageResponsible={isAdmin()}
        onClose={() => setEditor(null)}
        onSaved={handleSaved}
      />

      {yealinkImport ? (
        <YealinkImportPreviewModal
          fileName={yealinkImport.file.name}
          preview={yealinkImport.preview}
          decisions={yealinkImport.decisions}
          importing={importingXml}
          onChangeDecision={handleChangeImportDecision}
          onClose={handleCloseImportPreview}
          onConfirm={() => void handleConfirmImportPreview()}
        />
      ) : null}
    </div>
  );
}
