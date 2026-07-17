import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Building2,
  GripVertical,
  LayoutGrid,
  List,
  Loader2,
  Mail,
  MapPin,
  Phone,
  Search,
  SlidersHorizontal,
  UserRound,
} from 'lucide-react';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { Client, LegalEntityDetail, LegalEntityInput, LegalEntitySummary, PaymentKind, RelatedDocumentLink } from '../../types';
import { getObjectAuditActorLabel } from '../../utils/objectAudit';
import { formatDateInTimeZone } from '../../utils/timeZoneDate';
import { LegalEntityModal } from '../LegalEntities/LegalEntityModal';
import { TableColumnSettings } from '../ui/TableColumnSettings';

type ContactKind = 'all' | 'client' | 'legal';
type ViewMode = 'cards' | 'table';
type ContactColumnKey =
  | 'contact'
  | 'sourceId'
  | 'phone'
  | 'email'
  | 'inn'
  | 'kpp'
  | 'ogrn'
  | 'address'
  | 'responsible'
  | 'status'
  | 'company'
  | 'notes'
  | 'legalShortName'
  | 'legalFullName'
  | 'legalInn'
  | 'legalKpp'
  | 'legalOgrn'
  | 'director'
  | 'country'
  | 'organizationForm'
  | 'taxSystem'
  | 'taxSystemMode'
  | 'vatWithoutVat'
  | 'okved'
  | 'okpo'
  | 'okato'
  | 'oktmo'
  | 'okogu'
  | 'okfs'
  | 'okopf'
  | 'pfr'
  | 'fss'
  | 'taxOfficeCode'
  | 'taxRegion'
  | 'ifnsName'
  | 'taxOfficePhone'
  | 'treasuryInn'
  | 'treasuryKpp'
  | 'treasuryRecipientName'
  | 'treasuryBankBik'
  | 'treasuryBankName'
  | 'treasuryAccountNumber'
  | 'clientsCount'
  | 'cases'
  | 'contracts'
  | 'invoices'
  | 'acts'
  | 'payments'
  | 'createdAt'
  | 'updatedAt'
  | 'auditCreatedBy'
  | 'auditUpdatedBy';

type UnifiedContact =
  | {
      id: string;
      kind: 'client';
      sourceId: number;
      title: string;
      subtitle: string;
      phone: string;
      email: string;
      inn: string;
      kpp: string;
      address: string;
      statusText: string;
      createdAt?: string | null;
      client: Client;
    }
  | {
      id: string;
      kind: 'legal';
      sourceId: number;
      title: string;
      subtitle: string;
      phone: string;
      email: string;
      inn: string;
      kpp: string;
      address: string;
      statusText: string;
      createdAt?: string | null;
      legalEntity: LegalEntitySummary;
    };

interface UnifiedContactsProps {
  canViewClients?: boolean;
  canViewLegalEntities?: boolean;
  initialKind?: ContactKind;
  lockKind?: boolean;
  onOpenClient?: (id: number) => void;
  onOpenCase?: (caseId: number, clientId?: number | null, legalEntityId?: number | null) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
  onOpenAct?: (actId: number) => void;
  onOpenPayment?: (paymentId: number, type?: PaymentKind) => void;
}

const PAGE_SIZE = 24;
const CONTACTS_SETTINGS_VERSION = 'v1';

type ContactColumnConfig = {
  key: ContactColumnKey;
  label: string;
  defaultWidth: number;
  defaultVisible?: boolean;
};

const CLIENT_LEGAL_ENTITY_COLUMN_KEYS = new Set<ContactColumnKey>([
  'legalShortName',
  'legalFullName',
  'legalInn',
  'legalKpp',
  'legalOgrn',
]);

const COMMON_CONTACT_COLUMNS: ContactColumnConfig[] = [
  { key: 'contact', label: 'Контакт', defaultWidth: 360 },
  { key: 'phone', label: 'Телефон', defaultWidth: 150 },
  { key: 'email', label: 'Email', defaultWidth: 190 },
  { key: 'inn', label: 'ИНН', defaultWidth: 150 },
  { key: 'kpp', label: 'КПП', defaultWidth: 130, defaultVisible: false },
  { key: 'address', label: 'Адрес', defaultWidth: 320 },
  { key: 'cases', label: 'Дела', defaultWidth: 90 },
  { key: 'contracts', label: 'Договоры', defaultWidth: 110 },
  { key: 'invoices', label: 'Счета', defaultWidth: 90 },
  { key: 'acts', label: 'Акты', defaultWidth: 90 },
  { key: 'payments', label: 'Платежи', defaultWidth: 100 },
  { key: 'createdAt', label: 'Создано', defaultWidth: 120 },
];

const CLIENT_CONTACT_COLUMNS: ContactColumnConfig[] = [
  { key: 'contact', label: 'Физическое лицо', defaultWidth: 360 },
  { key: 'sourceId', label: 'ID', defaultWidth: 90, defaultVisible: false },
  { key: 'phone', label: 'Телефон', defaultWidth: 150 },
  { key: 'email', label: 'Email', defaultWidth: 190 },
  { key: 'company', label: 'Компания', defaultWidth: 220, defaultVisible: false },
  { key: 'address', label: 'Адрес', defaultWidth: 320 },
  { key: 'status', label: 'Статус', defaultWidth: 180 },
  { key: 'responsible', label: 'Ответственный', defaultWidth: 190 },
  { key: 'notes', label: 'Комментарий', defaultWidth: 260, defaultVisible: false },
  { key: 'legalShortName', label: 'Юрлицо', defaultWidth: 240, defaultVisible: false },
  { key: 'legalFullName', label: 'Юрлицо полное', defaultWidth: 320, defaultVisible: false },
  { key: 'legalInn', label: 'ИНН юрлица', defaultWidth: 150, defaultVisible: false },
  { key: 'legalKpp', label: 'КПП юрлица', defaultWidth: 130, defaultVisible: false },
  { key: 'legalOgrn', label: 'ОГРН юрлица', defaultWidth: 170, defaultVisible: false },
  { key: 'cases', label: 'Дела', defaultWidth: 90 },
  { key: 'contracts', label: 'Договоры', defaultWidth: 110 },
  { key: 'invoices', label: 'Счета', defaultWidth: 90 },
  { key: 'acts', label: 'Акты', defaultWidth: 90 },
  { key: 'payments', label: 'Платежи', defaultWidth: 100 },
  { key: 'createdAt', label: 'Создано', defaultWidth: 120 },
  { key: 'auditCreatedBy', label: 'Кто создал', defaultWidth: 180, defaultVisible: false },
  { key: 'updatedAt', label: 'Обновлено', defaultWidth: 130, defaultVisible: false },
  { key: 'auditUpdatedBy', label: 'Кто обновил', defaultWidth: 180, defaultVisible: false },
];

const LEGAL_CONTACT_COLUMNS: ContactColumnConfig[] = [
  { key: 'contact', label: 'Юридическое лицо', defaultWidth: 360 },
  { key: 'sourceId', label: 'ID', defaultWidth: 90, defaultVisible: false },
  { key: 'inn', label: 'ИНН', defaultWidth: 150 },
  { key: 'kpp', label: 'КПП', defaultWidth: 130 },
  { key: 'ogrn', label: 'ОГРН', defaultWidth: 170 },
  { key: 'phone', label: 'Телефон', defaultWidth: 150 },
  { key: 'email', label: 'Email', defaultWidth: 190 },
  { key: 'address', label: 'Адрес', defaultWidth: 320 },
  { key: 'director', label: 'Руководитель', defaultWidth: 220 },
  { key: 'responsible', label: 'Ответственный', defaultWidth: 190, defaultVisible: false },
  { key: 'clientsCount', label: 'Клиенты', defaultWidth: 100 },
  { key: 'cases', label: 'Дела', defaultWidth: 90 },
  { key: 'contracts', label: 'Договоры', defaultWidth: 110 },
  { key: 'invoices', label: 'Счета', defaultWidth: 90 },
  { key: 'acts', label: 'Акты', defaultWidth: 90 },
  { key: 'payments', label: 'Платежи', defaultWidth: 100 },
  { key: 'country', label: 'Страна', defaultWidth: 150, defaultVisible: false },
  { key: 'organizationForm', label: 'Форма организации', defaultWidth: 190, defaultVisible: false },
  { key: 'taxSystem', label: 'Налогообложение', defaultWidth: 180, defaultVisible: false },
  { key: 'taxSystemMode', label: 'Режим налогообложения', defaultWidth: 210, defaultVisible: false },
  { key: 'vatWithoutVat', label: 'Без НДС', defaultWidth: 120, defaultVisible: false },
  { key: 'okved', label: 'ОКВЭД', defaultWidth: 150, defaultVisible: false },
  { key: 'okpo', label: 'ОКПО', defaultWidth: 140, defaultVisible: false },
  { key: 'okato', label: 'ОКАТО', defaultWidth: 150, defaultVisible: false },
  { key: 'oktmo', label: 'ОКТМО', defaultWidth: 150, defaultVisible: false },
  { key: 'okogu', label: 'ОКОГУ', defaultWidth: 150, defaultVisible: false },
  { key: 'okfs', label: 'ОКФС', defaultWidth: 120, defaultVisible: false },
  { key: 'okopf', label: 'ОКОПФ', defaultWidth: 150, defaultVisible: false },
  { key: 'pfr', label: 'ПФР', defaultWidth: 150, defaultVisible: false },
  { key: 'fss', label: 'ФСС', defaultWidth: 150, defaultVisible: false },
  { key: 'taxOfficeCode', label: 'Код ИФНС', defaultWidth: 140, defaultVisible: false },
  { key: 'taxRegion', label: 'Регион ФНС', defaultWidth: 160, defaultVisible: false },
  { key: 'ifnsName', label: 'ИФНС', defaultWidth: 260, defaultVisible: false },
  { key: 'taxOfficePhone', label: 'Телефон ИФНС', defaultWidth: 170, defaultVisible: false },
  { key: 'treasuryInn', label: 'ИНН казначейства', defaultWidth: 170, defaultVisible: false },
  { key: 'treasuryKpp', label: 'КПП казначейства', defaultWidth: 170, defaultVisible: false },
  { key: 'treasuryRecipientName', label: 'Получатель казначейства', defaultWidth: 260, defaultVisible: false },
  { key: 'treasuryBankBik', label: 'БИК казначейства', defaultWidth: 170, defaultVisible: false },
  { key: 'treasuryBankName', label: 'Банк казначейства', defaultWidth: 240, defaultVisible: false },
  { key: 'treasuryAccountNumber', label: 'Счет казначейства', defaultWidth: 190, defaultVisible: false },
  { key: 'notes', label: 'Комментарий', defaultWidth: 260, defaultVisible: false },
  { key: 'createdAt', label: 'Создано', defaultWidth: 120 },
  { key: 'auditCreatedBy', label: 'Кто создал', defaultWidth: 180, defaultVisible: false },
  { key: 'updatedAt', label: 'Обновлено', defaultWidth: 130, defaultVisible: false },
  { key: 'auditUpdatedBy', label: 'Кто обновил', defaultWidth: 180, defaultVisible: false },
];

function columnsForScope(scope: ContactKind): ContactColumnConfig[] {
  if (scope === 'client') return CLIENT_CONTACT_COLUMNS;
  if (scope === 'legal') return LEGAL_CONTACT_COLUMNS;
  return COMMON_CONTACT_COLUMNS;
}

export function formatDate(value?: string | null, timeZoneId?: string | null) {
  if (!value) return '';
  return formatDateInTimeZone(value, timeZoneId) || value;
}

function normalizeRelatedPaymentKind(value?: string | null): PaymentKind | undefined {
  return value === 'Income' || value === 'Expense' ? value : undefined;
}

function formatAuditUserName(value?: string | null) {
  return value?.trim() ? getObjectAuditActorLabel(value) : '—';
}

function normalize(value?: string | null) {
  return value?.trim() ?? '';
}

function isViewMode(value: unknown): value is ViewMode {
  return value === 'cards' || value === 'table';
}

function readStoredViewMode(key: string): ViewMode | null {
  try {
    const raw = localStorage.getItem(key);
    return isViewMode(raw) ? raw : null;
  } catch {
    return null;
  }
}

function writeStoredViewMode(key: string, value: ViewMode) {
  try {
    localStorage.setItem(key, value);
  } catch {
    /** */
  }
}

function yesNo(value?: boolean | null) {
  if (value === true) return 'Да';
  if (value === false) return 'Нет';
  return '';
}

function contactSettingsKey(scope: ContactKind, suffix: string) {
  return scope === 'all' ? `contacts_${suffix}` : `contacts_${scope}_${suffix}`;
}

function buildClientContact(client: Client): UnifiedContact {
  const legal = client.legalEntity;
  return {
    id: `client-${client.id}`,
    kind: 'client',
    sourceId: client.id,
    title: normalize(client.name) || `Клиент #${client.id}`,
    subtitle: normalize(legal?.shortName) || normalize(client.company) || 'Физическое лицо',
    phone: normalize(client.phone),
    email: normalize(client.email),
    inn: normalize(legal?.inn),
    kpp: normalize(legal?.kpp),
    address: normalize(client.address) || normalize(legal?.address),
    statusText: `${client.isActive ? 'Активен' : 'Неактивен'}${client.clientStatus?.name ? ` · ${client.clientStatus.name}` : ''}`,
    createdAt: client.createdAt,
    client,
  };
}

function buildLegalContact(entity: LegalEntitySummary): UnifiedContact {
  return {
    id: `legal-${entity.id}`,
    kind: 'legal',
    sourceId: entity.id,
    title: normalize(entity.shortName) || `Юрлицо #${entity.id}`,
    subtitle: normalize(entity.fullName) || normalize(entity.director) || 'Юридическое лицо',
    phone: normalize(entity.phone),
    email: normalize(entity.email),
    inn: normalize(entity.inn),
    kpp: normalize(entity.kpp),
    address: normalize(entity.address),
    statusText: `${entity.clientsCount ?? 0} клиент(ов)`,
    createdAt: entity.createdAt,
    legalEntity: entity,
  };
}

function kindBadge(kind: UnifiedContact['kind']) {
  return kind === 'client'
    ? 'border-blue-200 bg-blue-50 text-blue-700'
    : 'border-emerald-200 bg-emerald-50 text-emerald-700';
}

function kindLabel(kind: UnifiedContact['kind']) {
  return kind === 'client' ? 'Физическое лицо' : 'Юридическое лицо';
}

function FieldCell({
  icon,
  label,
  value,
}: {
  icon: ReactNode;
  label: string;
  value: string;
}) {
  return (
    <div className="flex min-w-0 items-start gap-2 overflow-hidden">
      <div className="mt-0.5 shrink-0 text-slate-400">{icon}</div>
      <div className="min-w-0 flex-1 overflow-hidden">
        <div className="text-[11px] font-medium uppercase text-slate-400">{label}</div>
        <div
          className={`w-full truncate text-sm ${value ? 'text-slate-700' : 'text-slate-400'}`}
          title={value || undefined}
        >
          {value || 'Не заполнено'}
        </div>
      </div>
    </div>
  );
}

function MetricTile({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="flex min-h-[58px] flex-col items-center justify-center rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-center">
      <div className="text-[11px] font-medium uppercase text-slate-400">{label}</div>
      <div className="mt-1 max-w-full truncate text-sm font-semibold text-slate-800">{value}</div>
    </div>
  );
}

export function UnifiedContacts({
  canViewClients = true,
  canViewLegalEntities = true,
  initialKind = 'all',
  lockKind = false,
  onOpenClient,
  onOpenCase,
  onOpenContract,
  onOpenInvoice,
  onOpenAct,
  onOpenPayment,
}: UnifiedContactsProps) {
  const { user } = useAuth();
  const profileTimeZoneId = useUserTimeZone();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const canEditLegalEntities = permissions.legalEntities.canEdit;
  const isContactColumnAllowed = useCallback(
    (columnKey: ContactColumnKey) => {
      if (CLIENT_LEGAL_ENTITY_COLUMN_KEYS.has(columnKey)) return canViewLegalEntities;
      if (columnKey === 'clientsCount') return canViewClients;
      if (columnKey === 'cases') return permissions.cases.canView;
      if (columnKey === 'invoices') return permissions.accounts.canView;
      if (columnKey === 'acts') return permissions.acts.canView;
      if (columnKey === 'payments') return permissions.payments.canView;
      if (columnKey === 'contracts') return permissions.contracts.canView;
      return true;
    },
    [
      permissions.accounts.canView,
      permissions.acts.canView,
      permissions.cases.canView,
      permissions.contracts.canView,
      permissions.payments.canView,
      canViewClients,
      canViewLegalEntities,
    ],
  );
  const settingsScope = lockKind ? initialKind : 'all';
  const userStorageScope = user?.id ?? 'guest';
  const viewModeStorageKey = `pp.contacts.${settingsScope}.viewMode.${userStorageScope}.${CONTACTS_SETTINGS_VERSION}`;
  const viewModeSettingsKey = contactSettingsKey(settingsScope, 'view_mode');
  const tableLayoutStorageKey = `pp.contacts.${settingsScope}.tableSettings.${userStorageScope}.${CONTACTS_SETTINGS_VERSION}`;
  const tableLayoutSettingsKey = contactSettingsKey(settingsScope, 'table_settings');
  const contactColumns = useMemo(
    () => columnsForScope(settingsScope).filter((column) => isContactColumnAllowed(column.key)),
    [isContactColumnAllowed, settingsScope],
  );

  const [items, setItems] = useState<UnifiedContact[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [kind, setKind] = useState<ContactKind>(initialKind);
  const [viewMode, setViewMode] = useState<ViewMode>(() => readStoredViewMode(viewModeStorageKey) ?? 'cards');
  const [loadedViewModeSettingsKey, setLoadedViewModeSettingsKey] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [clientsTotal, setClientsTotal] = useState(0);
  const [legalTotal, setLegalTotal] = useState(0);
  const [legalDetail, setLegalDetail] = useState<LegalEntityDetail | null>(null);
  const [detailPageOpen, setDetailPageOpen] = useState(false);
  const [detailLoadingId, setDetailLoadingId] = useState<string | null>(null);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const viewModeSaveTimerRef = useRef<number | null>(null);
  const requestIdRef = useRef(0);
  const activeRequestControllerRef = useRef<AbortController | null>(null);
  const {
    columnOrder,
    orderedVisibleColumns,
    visibleColumns,
    draggingColumn,
    toggleColumn,
    moveColumn,
    handleColumnDragStart,
    handleColumnDragOver,
    handleColumnDrop,
    handleColumnDragEnd,
    handleResizeStart,
    getColumnWidth,
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<ContactColumnKey>({
    storageKey: tableLayoutStorageKey,
    settingsKey: tableLayoutSettingsKey,
    columns: contactColumns,
    defaultPageSize: PAGE_SIZE,
    minColumnWidth: 80,
  });

  const columnsByKey = useMemo(
    () => Object.fromEntries(contactColumns.map((column) => [column.key, column])) as Record<ContactColumnKey, ContactColumnConfig>,
    [contactColumns],
  );
  const orderedTableColumns = useMemo(
    () => orderedVisibleColumns.map((key) => columnsByKey[key]).filter(Boolean),
    [columnsByKey, orderedVisibleColumns],
  );
  useEffect(() => {
    setKind(initialKind);
  }, [initialKind]);

  useEffect(() => {
    let active = true;
    setLoadedViewModeSettingsKey(null);

    const cachedMode = readStoredViewMode(viewModeStorageKey);
    setViewMode(cachedMode ?? 'cards');

    const loadViewMode = async () => {
      try {
        const response = await apiService.getUserSetting(viewModeSettingsKey);
        if (!active || !response?.value) return;
        if (!isViewMode(response.value)) return;
        setViewMode(response.value);
        writeStoredViewMode(viewModeStorageKey, response.value);
      } catch {
        /** */
      } finally {
        if (active) {
          setLoadedViewModeSettingsKey(viewModeSettingsKey);
        }
      }
    };

    void loadViewMode();

    return () => {
      active = false;
    };
  }, [viewModeSettingsKey, viewModeStorageKey]);

  useEffect(() => {
    writeStoredViewMode(viewModeStorageKey, viewMode);
    if (loadedViewModeSettingsKey !== viewModeSettingsKey) return;

    if (viewModeSaveTimerRef.current) {
      window.clearTimeout(viewModeSaveTimerRef.current);
    }

    viewModeSaveTimerRef.current = window.setTimeout(() => {
      void apiService.upsertUserSetting(viewModeSettingsKey, viewMode);
    }, 400);

    return () => {
      if (viewModeSaveTimerRef.current) {
        window.clearTimeout(viewModeSaveTimerRef.current);
      }
    };
  }, [loadedViewModeSettingsKey, viewMode, viewModeSettingsKey, viewModeStorageKey]);

  const load = useCallback(async (initial = false) => {
    const requestId = ++requestIdRef.current;
    activeRequestControllerRef.current?.abort();
    const controller = new AbortController();
    activeRequestControllerRef.current = controller;

    try {
      if (initial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);

      const query = debouncedSearch.trim() || undefined;

      if (kind === 'client') {
        if (!canViewClients) {
          setItems([]);
          setTotal(0);
          setClientsTotal(0);
          setLegalTotal(0);
          return;
        }

        const [result, legalCounter] = await Promise.all([
          apiService.getClientsV2({
            search: query,
            page,
            pageSize: PAGE_SIZE,
            sortBy: 'name',
            sortDir: 'asc',
          }, { signal: controller.signal }),
          !lockKind && canViewLegalEntities
            ? apiService.getLegalEntitiesPaged(
                { search: query, page: 1, pageSize: 1 },
                { signal: controller.signal },
              )
            : Promise.resolve(null),
        ]);
        if (requestId !== requestIdRef.current || controller.signal.aborted) {
          return;
        }
        setItems(result.items.map(buildClientContact));
        setTotal(result.total);
        setClientsTotal(result.total);
        if (legalCounter) {
          setLegalTotal(legalCounter.total);
        }
        return;
      }

      if (kind === 'legal') {
        if (!canViewLegalEntities) {
          setItems([]);
          setTotal(0);
          setClientsTotal(0);
          setLegalTotal(0);
          return;
        }

        const [result, clientsCounter] = await Promise.all([
          apiService.getLegalEntitiesPaged({
            search: query,
            page,
            pageSize: PAGE_SIZE,
          }, { signal: controller.signal }),
          !lockKind && canViewClients
            ? apiService.getClientsV2({
                search: query,
                page: 1,
                pageSize: 1,
                sortBy: 'name',
                sortDir: 'asc',
              }, { signal: controller.signal })
            : Promise.resolve(null),
        ]);
        if (requestId !== requestIdRef.current || controller.signal.aborted) {
          return;
        }
        setItems(result.items.map(buildLegalContact));
        setTotal(result.total);
        if (clientsCounter) {
          setClientsTotal(clientsCounter.total);
        }
        setLegalTotal(result.total);
        return;
      }

      const windowSize = page * PAGE_SIZE;
      const [clientsResult, legalResult] = await Promise.allSettled([
        canViewClients
          ? apiService.getClientsV2({
              search: query,
              page: 1,
              pageSize: windowSize,
              sortBy: 'name',
              sortDir: 'asc',
            }, { signal: controller.signal })
          : Promise.resolve({ items: [] as Client[], total: 0, page: 1, pageSize: windowSize }),
        canViewLegalEntities
          ? apiService.getLegalEntitiesPaged(
              { search: query, page: 1, pageSize: windowSize },
              { signal: controller.signal },
            )
          : Promise.resolve({ items: [] as LegalEntitySummary[], total: 0, page: 1, pageSize: windowSize }),
      ]);

      if (requestId !== requestIdRef.current || controller.signal.aborted) {
        return;
      }

      if (clientsResult.status === 'rejected' && legalResult.status === 'rejected') {
        throw clientsResult.reason;
      }

      const clientsPage = clientsResult.status === 'fulfilled'
        ? clientsResult.value
        : { items: [] as Client[], total: 0 };
      const legalPage = legalResult.status === 'fulfilled'
        ? legalResult.value
        : { items: [] as LegalEntitySummary[], total: 0 };
      const merged = [
        ...clientsPage.items.map(buildClientContact),
        ...legalPage.items.map(buildLegalContact),
      ].sort((a, b) => a.title.localeCompare(b.title, 'ru'));

      setItems(merged.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE));
      setClientsTotal(clientsPage.total);
      setLegalTotal(legalPage.total);
      setTotal(clientsPage.total + legalPage.total);
    } catch (err) {
      if (requestId !== requestIdRef.current || controller.signal.aborted) {
        return;
      }
      setError(err instanceof Error ? err.message : 'Не удалось загрузить контакты');
      setItems([]);
      setTotal(0);
    } finally {
      if (activeRequestControllerRef.current === controller) {
        activeRequestControllerRef.current = null;
      }
      if (requestId === requestIdRef.current) {
        setLoading(false);
        setRefreshing(false);
      }
    }
  }, [canViewClients, canViewLegalEntities, debouncedSearch, kind, lockKind, page]);

  useEffect(() => {
    void load(true);
    return () => {
      activeRequestControllerRef.current?.abort();
      activeRequestControllerRef.current = null;
      requestIdRef.current += 1;
    };
  }, [load]);

  useEffect(() => {
    setPage(1);
  }, [kind, search]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setDebouncedSearch(search);
    }, 300);

    return () => window.clearTimeout(timer);
  }, [search]);

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const pageItems = items;
  const clientsCount = kind === 'client' ? total : clientsTotal;
  const legalCount = kind === 'legal' ? total : legalTotal;

  const getCaseCount = useCallback((item: UnifiedContact) => {
    if (item.kind === 'client') {
      return item.client.casesCount ?? item.client.cases?.length ?? 0;
    }
    return item.legalEntity.casesCount ?? 0;
  }, []);

  const getInvoiceCount = useCallback((item: UnifiedContact) => {
    if (item.kind === 'client') {
      return item.client.invoicesCount ?? 0;
    }
    return item.legalEntity.invoicesCount ?? 0;
  }, []);

  const getCaseLinks = useCallback((item: UnifiedContact): RelatedDocumentLink[] => {
    if (item.kind === 'client') {
      if (item.client.relatedCases?.length) return item.client.relatedCases;
      return (item.client.cases ?? []).map((clientCase) => ({
        id: clientCase.id,
        label: clientCase.title || `Дело #${clientCase.id}`,
        secondaryLabel: clientCase.status,
        clientId: clientCase.clientId ?? item.sourceId,
        legalEntityId: clientCase.legalEntityId ?? item.client.legalEntityId ?? null,
        date: clientCase.createdAt,
      }));
    }

    return item.legalEntity.relatedCases ?? [];
  }, []);

  const getContractLinks = useCallback((item: UnifiedContact): RelatedDocumentLink[] => {
    if (item.kind === 'client') {
      if (item.client.relatedContracts?.length) return item.client.relatedContracts;
      return (item.client.contracts ?? []).map((contract) => ({
        id: contract.id,
        label: contract.number ? `№${contract.number}` : contract.title || `Договор #${contract.id}`,
        secondaryLabel: contract.title,
        clientId: item.sourceId,
        date: contract.date,
        amount: contract.amount ?? null,
      }));
    }

    return item.legalEntity.relatedContracts ?? [];
  }, []);

  const getContractCount = useCallback((item: UnifiedContact) => {
    if (item.kind === 'client') {
      return item.client.contractsCount ?? item.client.contracts?.length ?? 0;
    }
    return item.legalEntity.contractsCount ?? 0;
  }, []);

  const getActCount = useCallback((item: UnifiedContact) => {
    if (item.kind === 'client') {
      return item.client.actsCount ?? 0;
    }
    return item.legalEntity.actsCount ?? 0;
  }, []);

  const getPaymentCount = useCallback((item: UnifiedContact) => {
    if (item.kind === 'client') {
      return item.client.paymentsCount ?? 0;
    }
    return item.legalEntity.paymentsCount ?? 0;
  }, []);

  const renderRelatedLinks = (
    links: RelatedDocumentLink[],
    totalCount: number,
    onOpen?: (link: RelatedDocumentLink) => void,
    canOpen?: (link: RelatedDocumentLink) => boolean,
  ) => {
    if (links.length === 0) {
      return totalCount;
    }

    return (
      <div className="flex min-w-0 flex-col gap-1">
        {links.map((link) => {
          const label = link.label || `#${link.id}`;
          const title = [label, link.secondaryLabel].filter(Boolean).join(' · ');
          const clickable = onOpen && (canOpen?.(link) ?? true);
          return clickable ? (
            <button
              key={link.id}
              type="button"
              onClick={(event) => {
                event.stopPropagation();
                onOpen(link);
              }}
              className="block max-w-full truncate text-left text-sm text-blue-600 hover:underline"
              title={title}
            >
              {label}
            </button>
          ) : (
            <span key={link.id} className="block max-w-full truncate text-sm text-slate-700" title={title}>
              {label}
            </span>
          );
        })}
        {totalCount > links.length ? (
          <span className="text-xs text-slate-400">+{totalCount - links.length}</span>
        ) : null}
      </div>
    );
  };

  const openItem = async (item: UnifiedContact) => {
    if (item.kind === 'client' && onOpenClient) {
      onOpenClient(item.sourceId);
      return;
    }

    if (item.kind === 'legal') {
      try {
        setDetailLoadingId(item.id);
        const detail = await apiService.getLegalEntity(item.sourceId);
        setLegalDetail(detail);
        setDetailPageOpen(true);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Не удалось открыть карточку юрлица');
      } finally {
        setDetailLoadingId(null);
      }
    }
  };

  const handleLegalEntitySubmit = async (payload: LegalEntityInput) => {
    if (!legalDetail) {
      throw new Error('Не выбрано юридическое лицо для обновления');
    }

    const saved = await apiService.updateLegalEntity(legalDetail.id, payload);
    setLegalDetail(saved);
    toast.success('Юр. лицо обновлено');
    await load(false);
    return saved;
  };

  const closeLegalDetailPage = () => {
    setDetailPageOpen(false);
    setLegalDetail(null);
  };

  const handleViewModeChange = (mode: ViewMode) => {
    setViewMode(mode);
    writeStoredViewMode(viewModeStorageKey, mode);
  };

  const pageTitle = lockKind && initialKind === 'client'
    ? 'Физические лица'
    : lockKind && initialKind === 'legal'
      ? 'Юридические лица'
      : 'Контакты';

  const pageSubtitle = lockKind && initialKind === 'client'
    ? 'Клиенты-физические лица'
    : lockKind && initialKind === 'legal'
      ? 'Организации и ИП'
      : 'Клиенты и юридические лица в одном реестре';

  if (detailPageOpen) {
    return (
      <div className="p-6">
        <LegalEntityModal
          isOpen={Boolean(legalDetail)}
          initial={legalDetail}
          onClose={closeLegalDetailPage}
          onSubmit={handleLegalEntitySubmit}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit={canEditLegalEntities}
          allowDelete={false}
          canViewCases={permissions.cases.canView}
          canViewAccounts={permissions.accounts.canView}
          canCreateAccounts={permissions.accounts.canView && permissions.accounts.canCreate}
          canViewActs={permissions.acts.canView}
          canViewPayments={permissions.payments.canView}
          mode="page"
          onBack={closeLegalDetailPage}
        />
      </div>
    );
  }

  const contactCell = (item: UnifiedContact) => (
    <div className="min-w-0">
      <div className="flex items-center gap-2">
        <span className={`inline-flex shrink-0 items-center rounded border px-2 py-0.5 text-xs font-medium ${kindBadge(item.kind)}`}>
          {kindLabel(item.kind)}
        </span>
        <span className="truncate font-semibold text-slate-900">{item.title}</span>
      </div>
      <div className="mt-1 truncate text-sm text-slate-500">{getContactSubtitle(item)}</div>
    </div>
  );

  const getContactSubtitle = (item: UnifiedContact) => {
    if (item.kind === 'client' && !canViewLegalEntities) {
      return normalize(item.client.company) || kindLabel(item.kind);
    }

    return item.subtitle;
  };

  const getContactInn = (item: UnifiedContact) => {
    if (item.kind === 'client' && !canViewLegalEntities) {
      return '';
    }

    return item.inn;
  };

  const getContactKpp = (item: UnifiedContact) => {
    if (item.kind === 'client' && !canViewLegalEntities) {
      return '';
    }

    return item.kpp;
  };

  const getContactAddress = (item: UnifiedContact) => {
    if (item.kind === 'client' && !canViewLegalEntities) {
      return normalize(item.client.address);
    }

    return item.address;
  };

  const getLegalClientsMetric = (item: UnifiedContact) => (
    canViewClients && item.kind === 'legal' ? item.legalEntity.clientsCount ?? 0 : '—'
  );

  const getLegalFooterText = (item: UnifiedContact) => (
    item.kind === 'legal' && !canViewClients ? kindLabel(item.kind) : item.statusText
  );

  const renderTableCell = (item: UnifiedContact, columnKey: ContactColumnKey) => {
    const client = item.kind === 'client' ? item.client : null;
    const legal = item.kind === 'legal' ? item.legalEntity : client?.legalEntity ?? null;

    switch (columnKey) {
      case 'contact':
        return contactCell(item);
      case 'sourceId':
        return item.sourceId;
      case 'phone':
        return item.phone || '—';
      case 'email':
        return item.email || '—';
      case 'inn':
        return getContactInn(item) || '—';
      case 'kpp':
        return getContactKpp(item) || '—';
      case 'ogrn':
        return legal?.ogrn || '—';
      case 'address':
        return getContactAddress(item) || '—';
      case 'responsible':
        return client?.responsibleUserName || legal?.responsibleUserName || '—';
      case 'status':
        return item.statusText || '—';
      case 'company':
        return client?.company || '—';
      case 'notes':
        return client?.notes || legal?.notes || '—';
      case 'legalShortName':
        return client?.legalEntity?.shortName || '—';
      case 'legalFullName':
        return client?.legalEntity?.fullName || '—';
      case 'legalInn':
        return client?.legalEntity?.inn || '—';
      case 'legalKpp':
        return client?.legalEntity?.kpp || '—';
      case 'legalOgrn':
        return client?.legalEntity?.ogrn || '—';
      case 'director':
        return legal?.director || '—';
      case 'country':
        return legal?.country || '—';
      case 'organizationForm':
        return legal?.organizationForm || '—';
      case 'taxSystem':
        return legal?.taxSystem || '—';
      case 'taxSystemMode':
        return legal?.taxSystemMode || '—';
      case 'vatWithoutVat':
        return yesNo(legal?.vatWithoutVat) || '—';
      case 'okved':
        return legal?.okved || '—';
      case 'okpo':
        return legal?.okpo || '—';
      case 'okato':
        return legal?.okato || '—';
      case 'oktmo':
        return legal?.oktmo || '—';
      case 'okogu':
        return legal?.okogu || '—';
      case 'okfs':
        return legal?.okfs || '—';
      case 'okopf':
        return legal?.okopf || '—';
      case 'pfr':
        return legal?.pfr || '—';
      case 'fss':
        return legal?.fss || '—';
      case 'taxOfficeCode':
        return legal?.taxOfficeCode || '—';
      case 'taxRegion':
        return legal?.taxRegion || '—';
      case 'ifnsName':
        return legal?.ifnsName || '—';
      case 'taxOfficePhone':
        return legal?.taxOfficePhone || '—';
      case 'treasuryInn':
        return legal?.treasuryInn || '—';
      case 'treasuryKpp':
        return legal?.treasuryKpp || '—';
      case 'treasuryRecipientName':
        return legal?.treasuryRecipientName || '—';
      case 'treasuryBankBik':
        return legal?.treasuryBankBik || '—';
      case 'treasuryBankName':
        return legal?.treasuryBankName || '—';
      case 'treasuryAccountNumber':
        return legal?.treasuryAccountNumber || '—';
      case 'clientsCount':
        return item.kind === 'legal' ? getLegalClientsMetric(item) : '—';
      case 'cases':
        return renderRelatedLinks(
          getCaseLinks(item),
          getCaseCount(item),
          onOpenCase
            ? (link) => onOpenCase(link.id, link.clientId ?? (item.kind === 'client' ? item.sourceId : null), link.legalEntityId)
            : undefined,
          (link) => Boolean(link.clientId ?? (item.kind === 'client' ? item.sourceId : null)),
        );
      case 'contracts':
        return renderRelatedLinks(
          getContractLinks(item),
          getContractCount(item),
          onOpenContract ? (link) => onOpenContract(link.id) : undefined,
        );
      case 'invoices':
        return renderRelatedLinks(
          item.kind === 'client' ? item.client.relatedInvoices ?? [] : item.legalEntity.relatedInvoices ?? [],
          getInvoiceCount(item),
          onOpenInvoice ? (link) => onOpenInvoice(link.id, normalizeRelatedPaymentKind(link.type)) : undefined,
        );
      case 'acts':
        return renderRelatedLinks(
          item.kind === 'client' ? item.client.relatedActs ?? [] : item.legalEntity.relatedActs ?? [],
          getActCount(item),
          onOpenAct ? (link) => onOpenAct(link.id) : undefined,
        );
      case 'payments':
        return renderRelatedLinks(
          item.kind === 'client' ? item.client.relatedPayments ?? [] : item.legalEntity.relatedPayments ?? [],
          getPaymentCount(item),
          onOpenPayment ? (link) => onOpenPayment(link.id, normalizeRelatedPaymentKind(link.type)) : undefined,
        );
      case 'createdAt':
        return formatDate(item.createdAt, profileTimeZoneId) || '—';
      case 'updatedAt':
        return formatDate(client?.auditUpdatedAt || legal?.updatedAt || legal?.auditUpdatedAt, profileTimeZoneId) || '—';
      case 'auditCreatedBy':
        return formatAuditUserName(client?.auditCreatedByUserName || legal?.auditCreatedByUserName);
      case 'auditUpdatedBy':
        return formatAuditUserName(client?.auditUpdatedByUserName || legal?.auditUpdatedByUserName);
      default:
        return '—';
    }
  };

  return (
    <div className="space-y-6 p-6">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <h1 className="text-3xl font-bold text-slate-900">{pageTitle}</h1>
          <p className="mt-1 text-slate-500">{pageSubtitle}</p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => handleViewModeChange('cards')}
            aria-label="Показать карточки"
            className={`inline-flex h-10 w-10 items-center justify-center rounded-lg border ${
              viewMode === 'cards' ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-slate-200 bg-white text-slate-600'
            }`}
            title="Карточки"
          >
            <LayoutGrid className="h-5 w-5" />
          </button>
          <button
            type="button"
            onClick={() => handleViewModeChange('table')}
            aria-label="Показать таблицу"
            className={`inline-flex h-10 w-10 items-center justify-center rounded-lg border ${
              viewMode === 'table' ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-slate-200 bg-white text-slate-600'
            }`}
            title="Таблица"
          >
            <List className="h-5 w-5" />
          </button>
          {viewMode === 'table' ? (
            <button
              type="button"
              onClick={() => setColumnsOpen((value) => !value)}
              aria-label="Настроить колонки"
              className={`inline-flex h-10 w-10 items-center justify-center rounded-lg border ${
                columnsOpen ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-slate-200 bg-white text-slate-600'
              }`}
              title="Настройка колонок"
            >
              <SlidersHorizontal className="h-5 w-5" />
            </button>
          ) : null}
          <button
            type="button"
            onClick={() => void load(false)}
            disabled={refreshing}
            className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 disabled:opacity-60"
          >
            {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
            Обновить
          </button>
        </div>
      </div>

      <div className="flex flex-col gap-3 border-y border-slate-200 py-4 lg:flex-row lg:items-center">
        <div className="relative min-w-0 flex-1">
          <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-slate-400" />
          <input
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            className="h-10 w-full rounded-lg border border-slate-200 bg-white pl-9 pr-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            placeholder="Поиск по имени, телефону, email, ИНН или адресу"
          />
        </div>

        {!lockKind ? (
          <div className="grid grid-cols-3 gap-2 lg:w-auto">
            {[
              ['all', `Все ${clientsTotal + legalTotal}`],
              ['client', `Физлица ${clientsCount}`],
              ['legal', `Юрлица ${legalCount}`],
            ].map(([value, label]) => (
              <button
                key={value}
                type="button"
                onClick={() => setKind(value as ContactKind)}
                className={`rounded-lg border px-3 py-2 text-sm font-medium ${
                  kind === value ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-slate-200 bg-white text-slate-700'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
        ) : null}
      </div>

      {error ? (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
      ) : null}

      {viewMode === 'table' && columnsOpen ? (
        <TableColumnSettings
          columns={contactColumns.map((column) => ({
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
        />
      ) : null}

      {loading ? (
        <div className="flex min-h-[360px] items-center justify-center text-slate-500">
          <Loader2 className="mr-2 h-5 w-5 animate-spin" />
          Загрузка контактов...
        </div>
      ) : viewMode === 'cards' ? (
        <div className="grid items-stretch gap-3 md:grid-cols-2 xl:grid-cols-3">
          {pageItems.map((item) => (
            <button
              key={item.id}
              type="button"
              onClick={() => void openItem(item)}
              className="flex min-h-[300px] min-w-0 flex-col overflow-hidden rounded-lg border border-slate-200 bg-white p-4 text-left shadow-sm transition hover:border-blue-300 hover:shadow-md"
            >
              <div className="flex items-start gap-3">
                <div className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-lg ${
                  item.kind === 'client' ? 'bg-blue-50 text-blue-700' : 'bg-emerald-50 text-emerald-700'
                }`}>
                  {detailLoadingId === item.id ? (
                    <Loader2 className="h-5 w-5 animate-spin" />
                  ) : item.kind === 'client' ? (
                    <UserRound className="h-5 w-5" />
                  ) : (
                    <Building2 className="h-5 w-5" />
                  )}
                </div>
                <div className="min-w-0 flex-1">{contactCell(item)}</div>
              </div>

              <div className="mt-4 grid min-w-0 gap-3">
                <div className="grid min-w-0 grid-cols-2 gap-4">
                  <FieldCell icon={<Phone className="h-4 w-4" />} label="Телефон" value={item.phone} />
                  <FieldCell icon={<Mail className="h-4 w-4" />} label="Email" value={item.email} />
                </div>
                <div className="grid min-w-0 grid-cols-2 gap-4">
                  <FieldCell icon={<Building2 className="h-4 w-4" />} label="ИНН" value={getContactInn(item)} />
                  <FieldCell icon={<Building2 className="h-4 w-4" />} label="КПП" value={getContactKpp(item)} />
                </div>
                <FieldCell icon={<MapPin className="h-4 w-4" />} label="Адрес" value={getContactAddress(item)} />
              </div>

              <div className="mt-4 grid grid-cols-3 gap-2">
                <MetricTile label={item.kind === 'legal' ? 'Клиенты' : 'Статус'} value={item.kind === 'legal' ? getLegalClientsMetric(item) : item.statusText} />
                <MetricTile label="Дела" value={permissions.cases.canView ? getCaseCount(item) : '—'} />
                <MetricTile label="Счета" value={permissions.accounts.canView ? getInvoiceCount(item) : '—'} />
              </div>

              <div className="mt-auto flex items-center justify-between border-t border-slate-100 pt-3 text-xs text-slate-500">
                <span className="truncate pr-3">{item.kind === 'legal' ? getLegalFooterText(item) : kindLabel(item.kind)}</span>
                <span>{formatDate(item.createdAt, profileTimeZoneId)}</span>
              </div>
            </button>
          ))}
        </div>
      ) : (
        <div className="overflow-hidden rounded-lg border border-slate-200 bg-white">
          <div className="overflow-x-auto">
            <table
              className="table-fixed divide-y divide-slate-200 text-sm"
              style={{ width: getTableWidth(), minWidth: '100%' }}
            >
              <colgroup>
                {orderedTableColumns.map((column) => (
                  <col key={column.key} style={{ width: getColumnWidth(column.key) }} />
                ))}
              </colgroup>
              <thead className="bg-slate-50 text-left text-xs uppercase text-slate-500">
                <tr>
                  {orderedTableColumns.map((column) => (
                    <th
                      key={column.key}
                      scope="col"
                      style={{ width: getColumnWidth(column.key) }}
                      className={`relative px-4 py-3 align-middle ${draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                      onDragOver={handleColumnDragOver}
                      onDrop={(event) => handleColumnDrop(column.key, event)}
                    >
                      <div
                        className="inline-flex min-w-0 max-w-full cursor-grab select-none items-center gap-2 active:cursor-grabbing"
                        draggable
                        onDragStart={(event) => handleColumnDragStart(column.key, event)}
                        onDragEnd={handleColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок"
                      >
                        <GripVertical className="h-4 w-4 shrink-0 text-slate-400" />
                        <span className="truncate">{column.label}</span>
                      </div>
                      <div
                        role="separator"
                        aria-orientation="vertical"
                        aria-label={`Изменить ширину колонки ${column.label}`}
                        className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                        onMouseDown={(event) => handleResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {pageItems.map((item) => (
                  <tr
                    key={item.id}
                    onClick={() => void openItem(item)}
                    className="cursor-pointer hover:bg-blue-50/50"
                  >
                    {orderedTableColumns.map((column) => {
                      const value = renderTableCell(item, column.key);
                      return (
                        <td
                          key={column.key}
                          style={{ width: getColumnWidth(column.key) }}
                          className="max-w-0 overflow-hidden px-4 py-3 text-slate-600"
                        >
                          <div className="truncate" title={String(value)}>
                            {value}
                          </div>
                        </td>
                      );
                    })}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {!loading && total === 0 ? (
        <div className="rounded-lg border border-slate-200 bg-white px-4 py-10 text-center text-slate-500">
          Ничего не найдено
        </div>
      ) : null}

      {!loading && total > 0 ? (
        <div className="flex flex-col gap-3 border-t border-slate-200 pt-4 text-sm text-slate-600 sm:flex-row sm:items-center sm:justify-between">
          <div>
            Показано {Math.min(total, (page - 1) * PAGE_SIZE + 1)}-{Math.min(total, (page - 1) * PAGE_SIZE + pageItems.length)} из {total}
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={page <= 1}
              onClick={() => setPage((prev) => Math.max(1, prev - 1))}
              className="rounded-lg border border-slate-200 bg-white px-3 py-2 disabled:opacity-50"
            >
              Назад
            </button>
            <span>{page} / {totalPages}</span>
            <button
              type="button"
              disabled={page >= totalPages}
              onClick={() => setPage((prev) => Math.min(totalPages, prev + 1))}
              className="rounded-lg border border-slate-200 bg-white px-3 py-2 disabled:opacity-50"
            >
              Далее
            </button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
