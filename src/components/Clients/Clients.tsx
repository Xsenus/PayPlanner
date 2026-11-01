import { SystemSelect } from '../ui/SystemSelect';
import { ReactNode, useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import {
  Users,
  Plus,
  Building,
  Building2,
  Mail,
  Phone,
  Edit,
  FolderKanban,
  PlusCircle,
  Search,
  X,
  LayoutGrid,
  List,
  Ban,
  Loader2,
  SlidersHorizontal,
  GripVertical,
  Filter,
} from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useTranslation } from '../../hooks/useTranslation';
import { ClientModal } from './ClientModal';
import { ClientDetail } from './ClientDetail';
import type { ActResponsible, Client, ClientCase, ClientInput, PaymentKind, RelatedDocumentLink } from '../../types';
import { CaseModal } from './CaseModal';
import { apiService } from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useTableSelection } from '../../hooks/useTableSelection';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { ClientStatusBadge } from './ClientStatusBadge';
import { formatClientDate as formatDate } from './clientFormatters';
import { FormPromptModal } from '../ui/FormPromptModal';
import { TableContextMenu, type TableContextMenuAction } from '../ui/TableContextMenu';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { TableSelectCheckbox } from '../ui/TableSelectCheckbox';

const CLIENT_PROFILE_NOTES_PREFIX = '__PP_CLIENT_PROFILE_V2__:';

function getDisplayNotes(notes?: string | null): string {
  if (!notes) return '';
  return notes.startsWith(CLIENT_PROFILE_NOTES_PREFIX) ? '' : notes;
}

function statusOrder(statusRaw?: string): number {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 0;
  if (s.includes('onhold') || s.includes('hold')) return 1;
  if (s.includes('closed')) return 2;
  if (s.includes('lost')) return 3;
  if (s.includes('проигр')) return 3;
  return 99;
}

function caseStatusLabel(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'Открыто';
  if (s.includes('onhold') || s.includes('hold')) return 'Приостановлено';
  if (s.includes('closed')) return 'Закрыто';
  if (s.includes('lost')) return 'Проиграно';
  if (s.includes('в работе') || s.includes('актив')) return 'Открыто';
  if (s.includes('ожид')) return 'Приостановлено';
  if (s.includes('закры')) return 'Закрыто';
  if (s.includes('проигр')) return 'Проиграно';
  return statusRaw ?? '';
}

function statusClasses(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'bg-green-100 text-green-700 border border-green-200';
  if (s.includes('onhold') || s.includes('hold'))
    return 'bg-amber-100 text-amber-800 border border-amber-200';
  if (s.includes('closed')) return 'bg-red-100 text-red-700 border border-red-200';
  if (s.includes('lost')) return 'bg-rose-100 text-rose-700 border border-rose-200';
  if (s.includes('проигр')) return 'bg-rose-100 text-rose-700 border border-rose-200';
  if (s.includes('overdue') || s.includes('проср'))
    return 'bg-rose-100 text-rose-700 border border-rose-200';
  if (s.includes('pending') || s.includes('ожид'))
    return 'bg-amber-100 text-amber-800 border border-amber-200';
  if (s.includes('completed') || s.includes('выполн'))
    return 'bg-emerald-100 text-emerald-700 border border-emerald-200';
  if (s.includes('inprogress') || s.includes('в работе') || s.includes('актив'))
    return 'bg-blue-100 text-blue-700 border border-blue-200';
  if (s.includes('cancel')) return 'bg-gray-200 text-gray-700 border border-gray-300';
  return 'bg-slate-100 text-slate-700 border border-slate-200';
}

function getClientCaseCount(client: Client) {
  return Math.max(client.casesCount ?? 0, client.cases?.length ?? 0);
}

function getClientContractsCount(client: Client) {
  return Math.max(client.contractsCount ?? 0, client.contracts?.length ?? 0);
}

function getClientInvoicesCount(client: Client) {
  return client.invoicesCount ?? 0;
}

function getClientActsCount(client: Client) {
  return client.actsCount ?? 0;
}

function getClientPaymentsCount(client: Client) {
  return client.paymentsCount ?? 0;
}

function normalizeRelatedPaymentKind(value?: string | null): PaymentKind | undefined {
  return value === 'Income' || value === 'Expense' ? value : undefined;
}

function getClientCaseLinks(client: Client): RelatedDocumentLink[] {
  if (client.relatedCases?.length) return client.relatedCases;
  return (client.cases ?? []).map((item) => ({
    id: item.id,
    label: item.title || `Дело #${item.id}`,
    secondaryLabel: item.status,
    clientId: item.clientId ?? client.id,
    legalEntityId: item.legalEntityId ?? client.legalEntityId ?? null,
    date: item.createdAt,
  }));
}

function getClientContractLinks(client: Client): RelatedDocumentLink[] {
  if (client.relatedContracts?.length) return client.relatedContracts;
  return (client.contracts ?? []).map((item) => ({
    id: item.id,
    label: item.number ? `№${item.number}` : item.title || `Договор #${item.id}`,
    secondaryLabel: item.title,
    clientId: client.id,
    date: item.date,
    amount: item.amount ?? null,
  }));
}

function escapeRegExp(s: string) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function highlight(text: string, q: string): ReactNode {
  const query = q.trim();
  if (!query) return text;
  const re = new RegExp(`(${escapeRegExp(query)})`, 'gi');
  const parts = text.split(re);
  return parts.map((part, i) =>
    i % 2 === 1 ? (
      <mark key={i} className="bg-yellow-200 rounded px-0.5">
        {part}
      </mark>
    ) : (
      part
    ),
  );
}

function highlightPhone(phone: string, q: string): ReactNode {
  const qDigits = q.replace(/\D/g, '');
  if (!qDigits) return highlight(phone, q);

  const digitPositions: number[] = [];
  const digitsOnlyChars: string[] = [];
  for (let i = 0; i < phone.length; i++) {
    const ch = phone[i];
    if (/\d/.test(ch)) {
      digitPositions.push(i);
      digitsOnlyChars.push(ch);
    }
  }
  const digitsOnly = digitsOnlyChars.join('');
  const pos = digitsOnly.indexOf(qDigits);
  if (pos === -1) return highlight(phone, q);

  const toHi = new Set(digitPositions.slice(pos, pos + qDigits.length));
  const out: ReactNode[] = [];
  let buf = '';
  let inMark = false;
  let key = 0;

  for (let i = 0; i < phone.length; i++) {
    const should = toHi.has(i);
    if (should) {
      if (!inMark) {
        if (buf) out.push(buf);
        buf = phone[i];
        inMark = true;
      } else {
        buf += phone[i];
      }
    } else {
      if (inMark) {
        out.push(
          <mark key={`m${key++}`} className="bg-yellow-200 rounded px-0.5">
            {buf}
          </mark>,
        );
        buf = '';
        inMark = false;
      }
      buf += phone[i];
    }
  }
  if (buf) {
    if (inMark) {
      out.push(
        <mark key={`m${key++}`} className="bg-yellow-200 rounded px-0.5">
          {buf}
        </mark>,
      );
    } else {
      out.push(buf);
    }
  }
  return out;
}

type ColumnId =
  | 'id'
  | 'name'
  | 'responsible'
  | 'status'
  | 'company'
  | 'email'
  | 'phone'
  | 'address'
  | 'notes'
  | 'createdAt'
  | 'isActive'
  | 'legalEntityId'
  | 'legalEntityShortName'
  | 'legalEntityFullName'
  | 'legalEntityInn'
  | 'legalEntityOgrn'
  | 'legalEntityKpp'
  | 'casesCount'
  | 'contractsCount'
  | 'invoicesCount'
  | 'actsCount'
  | 'paymentsCount';

type ColumnDefinition = {
  id: ColumnId;
  label: string;
  defaultWidth: number;
  defaultVisible: boolean;
  filterable?: boolean;
};

const COLUMN_DEFINITIONS: ColumnDefinition[] = [
  { id: 'id', label: 'ID', defaultWidth: 80, defaultVisible: false },
  { id: 'name', label: 'Клиент', defaultWidth: 220, defaultVisible: true, filterable: true },
  { id: 'responsible', label: 'Ответственный', defaultWidth: 180, defaultVisible: true, filterable: true },
  { id: 'status', label: 'Статус', defaultWidth: 160, defaultVisible: true, filterable: true },
  { id: 'company', label: 'Компания', defaultWidth: 180, defaultVisible: true, filterable: true },
  { id: 'email', label: 'Email', defaultWidth: 200, defaultVisible: true, filterable: true },
  { id: 'phone', label: 'Телефон', defaultWidth: 160, defaultVisible: true, filterable: true },
  { id: 'address', label: 'Адрес', defaultWidth: 220, defaultVisible: false, filterable: true },
  { id: 'notes', label: 'Заметки', defaultWidth: 260, defaultVisible: false, filterable: true },
  { id: 'createdAt', label: 'Создан', defaultWidth: 140, defaultVisible: false },
  { id: 'isActive', label: 'Активность', defaultWidth: 130, defaultVisible: true },
  { id: 'legalEntityId', label: 'ID юр. лица', defaultWidth: 140, defaultVisible: false, filterable: true },
  { id: 'legalEntityShortName', label: 'Юр. лицо', defaultWidth: 200, defaultVisible: true, filterable: true },
  { id: 'legalEntityFullName', label: 'Юр. лицо (полное)', defaultWidth: 240, defaultVisible: false, filterable: true },
  { id: 'legalEntityInn', label: 'ИНН юр. лица', defaultWidth: 160, defaultVisible: false, filterable: true },
  { id: 'legalEntityOgrn', label: 'ОГРН юр. лица', defaultWidth: 160, defaultVisible: false, filterable: true },
  { id: 'legalEntityKpp', label: 'КПП юр. лица', defaultWidth: 160, defaultVisible: false, filterable: true },
  { id: 'casesCount', label: 'Дела', defaultWidth: 120, defaultVisible: true },
  { id: 'contractsCount', label: 'Договоры', defaultWidth: 130, defaultVisible: true },
  { id: 'invoicesCount', label: 'Счета', defaultWidth: 110, defaultVisible: true },
  { id: 'actsCount', label: 'Акты', defaultWidth: 100, defaultVisible: true },
  { id: 'paymentsCount', label: 'Платежи', defaultWidth: 120, defaultVisible: true },
];

const COLUMN_META = COLUMN_DEFINITIONS.reduce(
  (acc, col) => {
    acc[col.id] = col;
    return acc;
  },
  {} as Record<ColumnId, ColumnDefinition>,
);

const TABLE_SETTINGS_STORAGE_PREFIX = 'pp.clients_table_settings';
const TABLE_SETTINGS_DB_KEY = 'clients_table_settings';
const FILTER_SETTINGS_STORAGE_PREFIX = 'pp.clients_table_filters';
const FILTER_SETTINGS_DB_KEY = 'clients_table_filters';
const PAGE_SIZE_OPTIONS = [5, 10, 25, 50, 100];

interface ClientsProps {
  onOpenContract?: (contractId: number) => void;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
  onOpenAct?: (actId: number) => void;
  onOpenPayment?: (paymentId: number, type?: PaymentKind) => void;
  onOpenTask?: (taskId: number) => void;
}

const TABLE_LAYOUT_COLUMNS = COLUMN_DEFINITIONS.map((column) => ({
  key: column.id,
  defaultVisible: column.defaultVisible,
  defaultWidth: column.defaultWidth,
})) satisfies ReadonlyArray<{
  key: ColumnId;
  defaultVisible?: boolean;
  defaultWidth?: number;
}>;

type ClientTableFilterSettings = {
  filters: Record<ColumnId, string>;
  filterColumns: ColumnId[];
};

type LegacyClientTableSettings = {
  filters?: Partial<Record<ColumnId, string>>;
  filterColumns?: ColumnId[];
};

const DEFAULT_FILTER_SETTINGS: ClientTableFilterSettings = {
  filters: COLUMN_DEFINITIONS.reduce(
    (acc, col) => ({ ...acc, [col.id]: '' }),
    {} as Record<ColumnId, string>,
  ),
  filterColumns: [],
};

function normalizeFilterSettings(
  parsed?: Partial<ClientTableFilterSettings> | LegacyClientTableSettings | null,
): ClientTableFilterSettings {
  const filters = { ...DEFAULT_FILTER_SETTINGS.filters, ...(parsed?.filters ?? {}) };
  const filterColumns = Array.isArray(parsed?.filterColumns)
    ? parsed?.filterColumns.filter((id): id is ColumnId =>
        COLUMN_DEFINITIONS.some((col) => col.id === id),
      )
    : DEFAULT_FILTER_SETTINGS.filterColumns;
  return { filters, filterColumns };
}

function loadFilterSettings(storageKey: string, legacyStorageKey: string): ClientTableFilterSettings {
  try {
    const raw = localStorage.getItem(storageKey);
    if (raw) {
      return normalizeFilterSettings(JSON.parse(raw) as Partial<ClientTableFilterSettings>);
    }

    const legacyRaw = localStorage.getItem(legacyStorageKey);
    if (legacyRaw) {
      return normalizeFilterSettings(JSON.parse(legacyRaw) as LegacyClientTableSettings);
    }

    return DEFAULT_FILTER_SETTINGS;
  } catch {
    return DEFAULT_FILTER_SETTINGS;
  }
}

function saveFilterSettings(storageKey: string, settings: ClientTableFilterSettings) {
  try {
    localStorage.setItem(storageKey, JSON.stringify(settings));
  } catch {
    /** */
  }
}

export function Clients({ onOpenContract, onOpenInvoice, onOpenAct, onOpenPayment, onOpenTask }: ClientsProps = {}) {
  const { statuses: clientStatuses } = useClientStatuses();
  const { t } = useTranslation();
  const { user } = useAuth();
  const profileTimeZoneId = useUserTimeZone();
  const { confirm } = useDialog();
  const tableLayoutStorageKey = `${TABLE_SETTINGS_STORAGE_PREFIX}.${user?.id ?? 'guest'}.v1`;
  const filterSettingsStorageKey = `${FILTER_SETTINGS_STORAGE_PREFIX}.${user?.id ?? 'guest'}.v1`;
  const permissions = useRolePermissions(user?.role?.id);
  const toast = useToast();
  const clientPermissions = permissions.clients;
  const canViewClients = clientPermissions.canView;
  const canCreateClients = clientPermissions.canCreate;
  const canEditClients = clientPermissions.canEdit;
  const canDeleteClients = clientPermissions.canDelete;
  const isColumnAllowed = useCallback(
    (columnId: ColumnId) => {
      if (columnId === 'invoicesCount') return permissions.accounts.canView;
      if (columnId === 'actsCount') return permissions.acts.canView;
      if (columnId === 'paymentsCount') return permissions.payments.canView;
      if (columnId === 'contractsCount') return permissions.contracts.canView;
      return true;
    },
    [
      permissions.accounts.canView,
      permissions.acts.canView,
      permissions.contracts.canView,
      permissions.payments.canView,
    ],
  );
  const availableColumnDefinitions = useMemo(
    () => COLUMN_DEFINITIONS.filter((column) => isColumnAllowed(column.id)),
    [isColumnAllowed],
  );
  const availableTableLayoutColumns = useMemo(
    () => TABLE_LAYOUT_COLUMNS.filter((column) => isColumnAllowed(column.key)),
    [isColumnAllowed],
  );

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [editingClient, setEditingClient] = useState<Client | null>(null);
  const [tableMenuPosition, setTableMenuPosition] = useState<{ x: number; y: number } | null>(null);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [responsiblesLoading, setResponsiblesLoading] = useState(false);
  const [changeResponsibleOpen, setChangeResponsibleOpen] = useState(false);
  const [changeResponsibleSubmitting, setChangeResponsibleSubmitting] = useState(false);
  const [changeResponsibleError, setChangeResponsibleError] = useState<string | null>(null);
  const [nextResponsibleId, setNextResponsibleId] = useState('');

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [caseClientId, setCaseClientId] = useState<number | null>(null);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('edit');

  const [expandedCards, setExpandedCards] = useState<Set<number>>(new Set());

  const [query, setQuery] = useState('');
  const debouncedQuery = useDebouncedValue(query.trim(), 400);
  const [viewMode, setViewMode] = useState<'cards' | 'table'>(() => {
    try {
      return (localStorage.getItem('pp.clients_view') as 'cards' | 'table') || 'cards';
    } catch {
      return 'cards';
    }
  });
  const [statusFilter, setStatusFilter] = useState<string>('all');
  const [activityFilter, setActivityFilter] = useState<'all' | 'active' | 'inactive'>('all');
  const {
    columnOrder,
    orderedVisibleColumns,
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
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<ColumnId>({
    storageKey: tableLayoutStorageKey,
    settingsKey: TABLE_SETTINGS_DB_KEY,
    columns: availableTableLayoutColumns,
    defaultPageSize: 25,
    minPageSize: 5,
    maxPageSize: 100,
    minColumnWidth: 80,
  });
  const [filterSettings, setFilterSettings] = useState<ClientTableFilterSettings>(() =>
    loadFilterSettings(filterSettingsStorageKey, tableLayoutStorageKey),
  );
  const [page, setPage] = useState(1);

  const filterableColumns = useMemo(
    () => availableColumnDefinitions.filter((col) => col.filterable),
    [availableColumnDefinitions],
  );

  const activeFilterColumns = useMemo(
    () => filterSettings.filterColumns.filter(isColumnAllowed),
    [filterSettings.filterColumns, isColumnAllowed],
  );

  const filterParamMap = useMemo(
    () =>
      ({
        id: 'id',
        name: 'name',
        responsible: 'responsibleUserName',
        status: 'clientStatus',
        company: 'company',
        email: 'email',
        phone: 'phone',
        address: 'address',
        notes: 'notes',
        createdAt: 'createdAt',
        isActive: 'isActive',
        legalEntityId: 'legalEntityId',
        legalEntityShortName: 'legalEntityShortName',
        legalEntityFullName: 'legalEntityFullName',
        legalEntityInn: 'legalEntityInn',
        legalEntityOgrn: 'legalEntityOgrn',
        legalEntityKpp: 'legalEntityKpp',
        casesCount: 'casesCount',
        contractsCount: 'contractsCount',
        invoicesCount: 'invoicesCount',
        actsCount: 'actsCount',
        paymentsCount: 'paymentsCount',
      }) satisfies Record<ColumnId, string>,
    [],
  );

  const activeFilters = useMemo(() => {
    const filters: Record<string, string> = {};
    activeFilterColumns.forEach((columnId) => {
      const raw = filterSettings.filters[columnId]?.trim();
      if (raw) {
        const paramKey = filterParamMap[columnId] ?? columnId;
        filters[paramKey] = raw;
      }
    });
    return filters;
  }, [activeFilterColumns, filterSettings.filters, filterParamMap]);

  const clientOwnerScope = JSON.stringify([user?.id, user?.role?.id, clientPermissions, permissions.cases,
    permissions.contracts, permissions.accounts, permissions.acts, permissions.payments]);
  const clientsQueryOptions = useMemo(
    () => ({
      mode: 'paged' as const,
      enabled: canViewClients,
      ownerScope: clientOwnerScope,
      search: debouncedQuery || undefined,
      isActive: activityFilter === 'all' ? undefined : activityFilter === 'active',
      sortBy: 'name' as const,
      sortDir: 'asc' as const,
      page,
      pageSize,
      filters: {
        ...(statusFilter !== 'all'
          ? statusFilter === 'none'
            ? { clientStatus: '__none__' }
            : { clientStatusId: statusFilter }
          : {}),
        ...activeFilters,
      },
    }),
    [debouncedQuery, activityFilter, page, pageSize, statusFilter, activeFilters, canViewClients, clientOwnerScope],
  );

  const { clients, loading, refreshing, total, createClient, updateClient, setClients, refresh } =
    useClients(clientsQueryOptions);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [filterSettingsLoaded, setFilterSettingsLoaded] = useState(false);
  const filterSaveTimerRef = useRef<number | null>(null);
  const sortedClients = clients;
  const visibleTableClientIds = useMemo(() => sortedClients.map((client) => client.id), [sortedClients]);
  const {
    selectedIds: selectedTableClientIds,
    selectedIdSet: selectedTableClientIdSet,
    selectedCount: selectedTableClientCount,
    allVisibleSelected,
    someVisibleSelected,
    isSelected: isTableClientSelected,
    clearSelection: clearTableClientSelection,
    selectOnly: selectOnlyTableClient,
    toggleSelection: toggleTableClientSelection,
    toggleAllVisible: toggleAllTableClients,
  } = useTableSelection(visibleTableClientIds);

  const selectedTableClients = useMemo(
    () => sortedClients.filter((client) => selectedTableClientIdSet.has(client.id)),
    [selectedTableClientIdSet, sortedClients],
  );
  const singleSelectedTableClient =
    selectedTableClients.length === 1 ? selectedTableClients[0] : null;

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  useEffect(() => {
    const handler = (e: Event) => {
      const { clientId, caseId } =
        (e as CustomEvent<{ clientId: number; caseId?: number }>).detail || {};
      if (typeof clientId === 'number') {
        setSelectedClientId(clientId);
        if (caseId !== undefined) {
          setInitialCaseId(caseId);
        }
      }
    };

    window.addEventListener('open-client-detail', handler as EventListener);
    return () => window.removeEventListener('open-client-detail', handler as EventListener);
  }, []);

  const toggleCases = (clientId: number) =>
    setExpandedCards((prev) => {
      const next = new Set(prev);
      if (next.has(clientId)) {
        next.delete(clientId);
      } else {
        next.add(clientId);
      }
      return next;
    });

  const showPermissionWarning = (message: string) => {
    toast.error(message || 'Недостаточно прав для выполнения действия.', 'Недостаточно прав');
  };

  const handleOpenClient = (clientId: number, caseId: number | 'all' = 'all') => {
    clearTableClientSelection();
    setSelectedClientId(clientId);
    setInitialCaseId(caseId);
  };

  const ensureResponsiblesLoaded = async () => {
    if (responsibles.length > 0) {
      return responsibles;
    }

    try {
      setResponsiblesLoading(true);
      const response = await apiService.getActResponsibles();
      setResponsibles(response ?? []);
      return response ?? [];
    } catch (error) {
      const message = extractDisplayErrorMessage(
        error,
        'Не удалось загрузить список ответственных.',
      );
      toast.error(message, 'Ошибка');
      throw error;
    } finally {
      setResponsiblesLoading(false);
    }
  };

  const handleRefreshClients = async () => {
    await refresh();
    toast.success('Список клиентов обновлен.', 'Обновление выполнено');
  };

  const handleDeleteSelectedClients = async (ids: number[]) => {
    if (!canDeleteClients) {
      showPermissionWarning('Недостаточно прав для удаления клиентов.');
      return;
    }

    const normalizedIds = Array.from(new Set(ids.filter((id) => Number.isFinite(id) && id > 0)));
    if (!normalizedIds.length) return;

    const targetClients = sortedClients.filter((client) => normalizedIds.includes(client.id));
    const description =
      normalizedIds.length === 1
        ? `Удалить клиента "${targetClients[0]?.name ?? `#${normalizedIds[0]}`}"?`
        : `Удалить выбранные записи (${normalizedIds.length})?`;

    const approved = await confirm({
      title: normalizedIds.length === 1 ? 'Удаление клиента' : 'Массовое удаление клиентов',
      description,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) return;

    try {
      await apiService.deleteClients(normalizedIds);
      clearTableClientSelection();
      await refresh();
      toast.success(
        normalizedIds.length === 1
          ? 'Клиент удален.'
          : `Удалено записей: ${normalizedIds.length}.`,
        'Удаление выполнено',
      );
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось удалить клиентов.');
      toast.error(message, 'Ошибка');
      await refresh();
    }
  };

  const openChangeResponsiblePrompt = async () => {
    if (!canEditClients) {
      showPermissionWarning('Недостаточно прав для смены ответственного.');
      return;
    }

    if (!selectedTableClientCount) return;

    try {
      const availableResponsibles = await ensureResponsiblesLoaded();
      if (!availableResponsibles.length) {
        setChangeResponsibleError('Список ответственных пуст.');
        return;
      }

      const defaultResponsibleId =
        selectedTableClients.length === 1 && selectedTableClients[0].responsibleUserId
          ? String(selectedTableClients[0].responsibleUserId)
          : String(availableResponsibles[0].id);

      setNextResponsibleId(defaultResponsibleId);
      setChangeResponsibleError(null);
      setChangeResponsibleOpen(true);
    } catch {
      /** */
    }
  };

  const submitResponsibleChange = async () => {
    if (!canEditClients) {
      showPermissionWarning('Недостаточно прав для смены ответственного.');
      return;
    }

    const responsibleUserId = Number(nextResponsibleId);
    if (!Number.isFinite(responsibleUserId) || responsibleUserId <= 0) {
      setChangeResponsibleError('Выберите ответственного.');
      return;
    }

    const selectedResponsibleLabel =
      responsibles.find((responsible) => String(responsible.id) === nextResponsibleId)?.fullName
      ?? (
        singleSelectedTableClient?.responsibleUserId
        && String(singleSelectedTableClient.responsibleUserId) === nextResponsibleId
          ? singleSelectedTableClient.responsibleUserName
          : null
      )
      ?? 'выбранного ответственного';
    const approved = await confirm({
      title: 'Подтверждение сохранения',
      description:
        selectedTableClientIds.length === 1
          ? `Сохранить нового ответственного (${selectedResponsibleLabel}) для выбранного клиента?`
          : `Сохранить нового ответственного (${selectedResponsibleLabel}) для выбранных клиентов: ${selectedTableClientIds.length}?`,
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });

    if (!approved) {
      return;
    }

    try {
      setChangeResponsibleSubmitting(true);
      setChangeResponsibleError(null);
      await apiService.changeClientsResponsible(selectedTableClientIds, responsibleUserId);
      setChangeResponsibleOpen(false);
      await refresh();
      toast.success('Ответственный обновлен.', 'Изменения сохранены');
    } catch (error) {
      setChangeResponsibleError(
        extractDisplayErrorMessage(error, 'Не удалось сменить ответственного.'),
      );
    } finally {
      setChangeResponsibleSubmitting(false);
    }
  };

  const closeTableMenu = () => {
    setTableMenuPosition(null);
  };

  const openTableMenu = (
    event: ReactMouseEvent<HTMLTableRowElement>,
    client: Client,
  ) => {
    event.preventDefault();
    event.stopPropagation();

    if (!isTableClientSelected(client.id)) {
      selectOnlyTableClient(client.id);
    }

    setTableMenuPosition({
      x: event.clientX,
      y: event.clientY,
    });
  };

  const handleAddClient = () => {
    if (!canCreateClients) {
      showPermissionWarning('Недостаточно прав для добавления клиентов.');
      return;
    }
    setEditingClient(null);
    setIsModalOpen(true);
  };

  const handleEditClient = (client: Client) => {
    if (!canEditClients) {
      showPermissionWarning('Недостаточно прав для редактирования клиентов.');
      return;
    }
    handleOpenClient(client.id);
  };

  const handleCloseModal = () => {
    setIsModalOpen(false);
    setEditingClient(null);
  };

  const handleSubmitClient = async (clientData: ClientInput) => {
    if (editingClient) {
      if (!canEditClients) {
        showPermissionWarning('Недостаточно прав для редактирования клиентов.');
        return;
      }
      await updateClient(editingClient.id, clientData);
    } else {
      if (!canCreateClients) {
        showPermissionWarning('Недостаточно прав для добавления клиентов.');
        return;
      }
      await createClient(clientData);
    }
  };

  const handleDeleteClient = async (id: number) => {
    if (!canDeleteClients) {
      showPermissionWarning('Недостаточно прав для удаления клиентов.');
      return;
    }
    setEditingClient(null);
    setIsModalOpen(false);

    try {
      await apiService.deleteClients([id]);
      clearTableClientSelection();
      await refresh();
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось удалить клиента.'),
        'Ошибка',
      );
      await refresh();
    }
  };

  const openAddCase = (clientId: number) => {
    if (!canCreateClients) {
      showPermissionWarning('Недостаточно прав для добавления дел.');
      return;
    }
    setCaseClientId(clientId);
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
  };

  const openEditCase = (clientId: number, clientCase: ClientCase) => {
    if (!canEditClients) {
      showPermissionWarning('Недостаточно прав для редактирования дел.');
      return;
    }
    setCaseClientId(clientId);
    setEditingCase(clientCase);
    setCaseMode('edit');
    setCaseModalOpen(true);
  };

  const saveCase = async (patch: Partial<ClientCase>): Promise<void> => {
    if (!caseClientId) return;

    type Status = ClientCase['status'];
    const DEFAULT_STATUS: Status = 'Open';

    const base: Omit<ClientCase, 'id' | 'createdAt' | 'payments'> = {
      clientId: caseClientId,
      title: (patch.title ?? editingCase?.title ?? '').trim(),
      description: (patch.description ?? editingCase?.description ?? '').trim(),
      status: (patch.status ?? editingCase?.status ?? DEFAULT_STATUS) as Status,
      caseNumber: patch.caseNumber ?? editingCase?.caseNumber ?? '',
      caseType: patch.caseType ?? editingCase?.caseType ?? '',
      stage: patch.stage ?? editingCase?.stage ?? '',
      responsibleUserId: patch.responsibleUserId !== undefined ? patch.responsibleUserId : editingCase?.responsibleUserId ?? null,
      startDate: patch.startDate !== undefined ? patch.startDate : editingCase?.startDate ?? null,
      dueDate: patch.dueDate !== undefined ? patch.dueDate : editingCase?.dueDate ?? null,
      feeAmount: patch.feeAmount ?? editingCase?.feeAmount ?? 0,
      priority: patch.priority ?? editingCase?.priority ?? 'normal',
      courtName: patch.courtName ?? editingCase?.courtName ?? '',
      ourCourtRole: patch.ourCourtRole ?? editingCase?.ourCourtRole ?? '',
      courtClaimAmount: patch.courtClaimAmount !== undefined ? patch.courtClaimAmount : editingCase?.courtClaimAmount ?? null,
      courtAwardedAmount: patch.courtAwardedAmount !== undefined ? patch.courtAwardedAmount : editingCase?.courtAwardedAmount ?? null,
      courtStateFeeAmount: patch.courtStateFeeAmount !== undefined ? patch.courtStateFeeAmount : editingCase?.courtStateFeeAmount ?? null,
      clearCourtClaimAmount: patch.clearCourtClaimAmount ?? false,
      clearCourtAwardedAmount: patch.clearCourtAwardedAmount ?? false,
      clearCourtStateFeeAmount: patch.clearCourtStateFeeAmount ?? false,
      courtCaseNumber: patch.courtCaseNumber ?? editingCase?.courtCaseNumber ?? '',
      courtUrl: patch.courtUrl ?? editingCase?.courtUrl ?? '',
      nextHearingAt: patch.nextHearingAt !== undefined ? patch.nextHearingAt : editingCase?.nextHearingAt ?? null,
      claimant: patch.claimant ?? editingCase?.claimant ?? '',
      claimantInn: patch.claimantInn ?? editingCase?.claimantInn ?? '',
      claimantOgrn: patch.claimantOgrn ?? editingCase?.claimantOgrn ?? '',
      defendant: patch.defendant ?? editingCase?.defendant ?? '',
      defendantInn: patch.defendantInn ?? editingCase?.defendantInn ?? '',
      defendantOgrn: patch.defendantOgrn ?? editingCase?.defendantOgrn ?? '',
      thirdParties: patch.thirdParties ?? editingCase?.thirdParties ?? '',
      representatives: patch.representatives ?? editingCase?.representatives ?? '',
      judgeName: patch.judgeName ?? editingCase?.judgeName ?? '',
      courtroom: patch.courtroom ?? editingCase?.courtroom ?? '',
      courtSecretaryContacts: patch.courtSecretaryContacts ?? editingCase?.courtSecretaryContacts ?? '',
      claimFiledAt: patch.claimFiledAt !== undefined ? patch.claimFiledAt : editingCase?.claimFiledAt ?? null,
      courtDecisionAt: patch.courtDecisionAt !== undefined ? patch.courtDecisionAt : editingCase?.courtDecisionAt ?? null,
      courtDecisionEffectiveAt: patch.courtDecisionEffectiveAt !== undefined ? patch.courtDecisionEffectiveAt : editingCase?.courtDecisionEffectiveAt ?? null,
      clearCourtDecisionEffectiveAt: patch.clearCourtDecisionEffectiveAt ?? false,
      executiveWritReceivedAt: patch.executiveWritReceivedAt !== undefined ? patch.executiveWritReceivedAt : editingCase?.executiveWritReceivedAt ?? null,
      clearExecutiveWritReceivedAt: patch.clearExecutiveWritReceivedAt ?? false,
      executiveWritSubmittedAt: patch.executiveWritSubmittedAt !== undefined ? patch.executiveWritSubmittedAt : editingCase?.executiveWritSubmittedAt ?? null,
      executiveWritSubmittedTo: patch.executiveWritSubmittedTo !== undefined ? patch.executiveWritSubmittedTo : editingCase?.executiveWritSubmittedTo ?? '',
      clearExecutiveWritSubmission: patch.clearExecutiveWritSubmission ?? false,
      appealDeadlineAt: patch.appealDeadlineAt !== undefined ? patch.appealDeadlineAt : editingCase?.appealDeadlineAt ?? null,
      cassationDeadlineAt: patch.cassationDeadlineAt !== undefined ? patch.cassationDeadlineAt : editingCase?.cassationDeadlineAt ?? null,
      motionDeadlineAt: patch.motionDeadlineAt !== undefined ? patch.motionDeadlineAt : editingCase?.motionDeadlineAt ?? null,
      objectionDeadlineAt: patch.objectionDeadlineAt !== undefined ? patch.objectionDeadlineAt : editingCase?.objectionDeadlineAt ?? null,
      documentsSummary: patch.documentsSummary ?? editingCase?.documentsSummary ?? '',
      eventHistory: patch.eventHistory ?? editingCase?.eventHistory ?? '',
      strategy: patch.strategy ?? editingCase?.strategy ?? '',
    };

    if (caseMode === 'edit' && editingCase) {
      if (!canEditClients) {
        showPermissionWarning('Недостаточно прав для редактирования дел.');
        return;
      }
      const updated = await apiService.updateCase(editingCase.id, base);
      setClients((prev) =>
        prev.map((c) =>
          c.id === caseClientId
            ? { ...c, cases: (c.cases ?? []).map((k) => (k.id === updated.id ? updated : k)) }
            : c,
        ),
      );
    } else {
      if (!canCreateClients) {
        showPermissionWarning('Недостаточно прав для добавления дел.');
        return;
      }
      const created = await apiService.createCase(base);
      setClients((prev) =>
        prev.map((c) =>
          c.id === caseClientId ? { ...c, cases: [created, ...(c.cases ?? [])] } : c,
        ),
      );
    }

    setCaseModalOpen(false);
    setEditingCase(null);
  };

  const deleteCase = async (): Promise<void> => {
    if (!editingCase || !caseClientId) return;
    if (!canDeleteClients) {
      showPermissionWarning('Недостаточно прав для удаления дел.');
      return;
    }
    await apiService.deleteCase(editingCase.id);

    setClients((prev) =>
      prev.map((c) =>
        c.id === caseClientId
          ? { ...c, cases: (c.cases ?? []).filter((k) => k.id !== editingCase.id) }
          : c,
      ),
    );

    setCaseModalOpen(false);
    setEditingCase(null);
  };

  const hasAnyClients = total > 0;
  const isFiltered =
    !!query.trim() ||
    statusFilter !== 'all' ||
    activityFilter !== 'all' ||
    Object.keys(activeFilters).length > 0;

  const handleViewModeChange = (mode: 'cards' | 'table') => {
    setViewMode(mode);
    try {
      localStorage.setItem('pp.clients_view', mode);
    } catch {
      /** */
    }
  };

  useEffect(() => {
    let active = true;
    const loadFilterSettingsFromServer = async () => {
      try {
        const response = await apiService.getUserSetting(FILTER_SETTINGS_DB_KEY);
        if (active && response?.value) {
          const parsed = JSON.parse(response.value) as Partial<ClientTableFilterSettings>;
          const normalized = normalizeFilterSettings(parsed);
          setFilterSettings(normalized);
          saveFilterSettings(filterSettingsStorageKey, normalized);
          return;
        }

        const legacyResponse = await apiService.getUserSetting(TABLE_SETTINGS_DB_KEY);
        if (active && legacyResponse?.value) {
          const parsed = JSON.parse(legacyResponse.value) as LegacyClientTableSettings;
          const normalized = normalizeFilterSettings(parsed);
          setFilterSettings(normalized);
          saveFilterSettings(filterSettingsStorageKey, normalized);
        }
      } catch {
        /** */
      } finally {
        if (active) setFilterSettingsLoaded(true);
      }
    };

    void loadFilterSettingsFromServer();
    return () => {
      active = false;
    };
  }, [filterSettingsStorageKey]);

  useEffect(() => {
    saveFilterSettings(filterSettingsStorageKey, filterSettings);
    if (!filterSettingsLoaded) return;
    if (filterSaveTimerRef.current) {
      window.clearTimeout(filterSaveTimerRef.current);
    }

    filterSaveTimerRef.current = window.setTimeout(() => {
      void apiService.upsertUserSetting(FILTER_SETTINGS_DB_KEY, JSON.stringify(filterSettings));
    }, 600);

    return () => {
      if (filterSaveTimerRef.current) {
        window.clearTimeout(filterSaveTimerRef.current);
      }
    };
  }, [filterSettings, filterSettingsLoaded, filterSettingsStorageKey]);

  useEffect(() => {
    if (viewMode !== 'table') {
      setSettingsOpen(false);
      setFiltersOpen(false);
    }
  }, [viewMode]);

  useEffect(() => {
    setPage(1);
  }, [debouncedQuery, statusFilter, activityFilter, activeFilters, pageSize]);

  useEffect(() => {
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, totalPages]);

  const visibleColumnIds = useMemo(
    () => orderedVisibleColumns.filter(isColumnAllowed),
    [orderedVisibleColumns, isColumnAllowed],
  );

  const handleFilterColumnsChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    const selected = Array.from(event.target.selectedOptions).map((option) => option.value as ColumnId);
    setFilterSettings((prev) => ({ ...prev, filterColumns: selected }));
  };

  const handleFilterValueChange = (columnId: ColumnId, value: string) => {
    setFilterSettings((prev) => ({
      ...prev,
      filters: { ...prev.filters, [columnId]: value },
    }));
  };

  const resetFilters = () => {
    setFilterSettings(DEFAULT_FILTER_SETTINGS);
    setPage(1);
  };

  const setPageSizeValue = (next: number) => {
    setPageSize(Number.isFinite(next) && next > 0 ? next : pageSize);
    setPage(1);
  };

  if (!canViewClients) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center p-6">
        <div className="w-full max-w-xl rounded-2xl border border-red-200 bg-white px-6 py-10 text-center shadow-sm">
          <Ban className="mx-auto h-12 w-12 text-red-500" />
          <h1 className="mt-4 text-2xl font-semibold text-red-700">Доступ к клиентам ограничен</h1>
          <p className="mt-2 text-sm text-red-600">
            Обратитесь к администратору, чтобы получить права на просмотр и управление базой клиентов.
          </p>
        </div>
      </div>
    );
  }

  if (selectedClientId) {
    return (
      <ClientDetail
        clientId={selectedClientId}
        onClientUpdated={(updatedClient) => {
          setClients((prev) =>
            prev.map((client) =>
              client.id === updatedClient.id
                ? {
                    ...client,
                    ...updatedClient,
                    legalEntity: updatedClient.legalEntity ?? client.legalEntity ?? null,
                    clientStatus: updatedClient.clientStatus ?? client.clientStatus ?? null,
                    cases: Array.isArray(updatedClient.cases) ? updatedClient.cases : client.cases,
                    contracts: Array.isArray(updatedClient.contracts) ? updatedClient.contracts : client.contracts,
                  }
                : client,
            ),
          );
        }}
        onBack={() => setSelectedClientId(null)}
        initialCaseId={initialCaseId}
        onOpenTask={onOpenTask}
      />
    );
  }

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">{t('loading')}</div>
      </div>
    );
  }

  const renderRelatedLinks = (
    links: RelatedDocumentLink[],
    totalCount: number,
    onOpen?: (link: RelatedDocumentLink) => void,
  ) => {
    if (links.length === 0) {
      return <span className="text-sm text-gray-700 block truncate">{totalCount}</span>;
    }

    return (
      <div className="flex min-w-0 flex-col gap-1">
        {links.map((link) => {
          const label = link.label || `#${link.id}`;
          const title = [label, link.secondaryLabel].filter(Boolean).join(' · ');
          return onOpen ? (
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
            <span key={link.id} className="block max-w-full truncate text-sm text-gray-700" title={title}>
              {label}
            </span>
          );
        })}
        {totalCount > links.length ? (
          <span className="text-xs text-gray-400">+{totalCount - links.length}</span>
        ) : null}
      </div>
    );
  };

  const renderCell = (client: Client, columnId: ColumnId) => {
    switch (columnId) {
      case 'id':
        return <span className="text-sm text-gray-600 block truncate">#{client.id}</span>;
      case 'name':
        return (
          <div className="flex items-center gap-3 min-w-0">
            <button
              type="button"
              onClick={() => handleOpenClient(client.id)}
              className="w-9 h-9 bg-blue-100 rounded-lg flex items-center justify-center shrink-0 hover:bg-blue-200 transition-colors"
              title="Открыть карточку клиента"
            >
              <Users size={18} className="text-blue-600" />
            </button>
            <div className="min-w-0">
              <button
                type="button"
                onClick={() => handleOpenClient(client.id)}
                className="font-medium text-gray-900 text-left truncate hover:underline"
                title="Открыть карточку клиента"
              >
                {highlight(client.name ?? '', debouncedQuery)}
              </button>
            </div>
          </div>
        );
      case 'responsible':
        return client.responsibleUserName ? (
          <span className="text-sm text-gray-700 block truncate">{client.responsibleUserName}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'status':
        return <ClientStatusBadge status={client.clientStatus} />;
      case 'company':
        return (
          <span className="text-sm text-gray-700 block truncate">
            {highlight(client.company ?? '', debouncedQuery)}
          </span>
        );
      case 'email':
        return client.email ? (
          <span className="text-sm text-gray-700 block truncate">
            {highlight(client.email ?? '', debouncedQuery)}
          </span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'phone':
        return client.phone ? (
          <span className="text-sm text-gray-700 block truncate">
            {highlightPhone(client.phone ?? '', debouncedQuery)}
          </span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'address':
        return client.address ? (
          <span className="text-sm text-gray-700 block truncate">{client.address}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'notes':
        return getDisplayNotes(client.notes) ? (
          <span className="text-sm text-gray-700 block truncate">{getDisplayNotes(client.notes)}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'createdAt':
        return <span className="text-sm text-gray-600 block truncate">{formatDate(client.createdAt, profileTimeZoneId)}</span>;
      case 'isActive':
        return client.isActive ? (
          <span className="inline-flex items-center px-2 py-1 text-xs rounded-full bg-emerald-100 text-emerald-700 border border-emerald-200">
            Активен
          </span>
        ) : (
          <span className="inline-flex items-center px-2 py-1 text-xs rounded-full bg-slate-100 text-slate-600 border border-slate-200">
            Неактивен
          </span>
        );
      case 'legalEntityId':
        return client.legalEntityId ? (
          <span className="text-sm text-gray-700 block truncate">#{client.legalEntityId}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'legalEntityShortName':
        return client.legalEntity ? (
          <span className="text-sm text-gray-700 block truncate">
            {highlight(client.legalEntity.shortName ?? '', debouncedQuery)}
          </span>
        ) : (
          <span className="text-xs text-gray-400">{t('legalEntityNotAssigned') || 'Не указано'}</span>
        );
      case 'legalEntityFullName':
        return client.legalEntity?.fullName ? (
          <span className="text-sm text-gray-700 block truncate">{client.legalEntity.fullName}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'legalEntityInn':
        return client.legalEntity?.inn ? (
          <span className="text-sm text-gray-700 block truncate">{client.legalEntity.inn}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'legalEntityOgrn':
        return client.legalEntity?.ogrn ? (
          <span className="text-sm text-gray-700 block truncate">{client.legalEntity.ogrn}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'legalEntityKpp':
        return client.legalEntity?.kpp ? (
          <span className="text-sm text-gray-700 block truncate">{client.legalEntity.kpp}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'casesCount':
        return renderRelatedLinks(getClientCaseLinks(client), getClientCaseCount(client), (link) => {
          handleOpenClient(client.id, link.id);
        });
      case 'contractsCount':
        return renderRelatedLinks(
          getClientContractLinks(client),
          getClientContractsCount(client),
          onOpenContract ? (link) => onOpenContract(link.id) : undefined,
        );
      case 'invoicesCount':
        return renderRelatedLinks(
          client.relatedInvoices ?? [],
          getClientInvoicesCount(client),
          onOpenInvoice ? (link) => onOpenInvoice(link.id, normalizeRelatedPaymentKind(link.type)) : undefined,
        );
      case 'actsCount':
        return renderRelatedLinks(
          client.relatedActs ?? [],
          getClientActsCount(client),
          onOpenAct ? (link) => onOpenAct(link.id) : undefined,
        );
      case 'paymentsCount':
        return renderRelatedLinks(
          client.relatedPayments ?? [],
          getClientPaymentsCount(client),
          onOpenPayment ? (link) => onOpenPayment(link.id, normalizeRelatedPaymentKind(link.type)) : undefined,
        );
      default:
        return null;
    }
  };

  const dictionaryResponsibles = [...responsibles];
  const knownResponsibleIds = new Set(
    dictionaryResponsibles.map((responsible) => responsible.id),
  );

  selectedTableClients.forEach((client) => {
    if (
      client.responsibleUserId
      && client.responsibleUserName
      && !knownResponsibleIds.has(client.responsibleUserId)
    ) {
      dictionaryResponsibles.push({
        id: client.responsibleUserId,
        fullName: client.responsibleUserName,
      });
      knownResponsibleIds.add(client.responsibleUserId);
    }
  });

  const responsibleOptions = [
    { value: '', label: 'Выберите ответственного' },
    ...dictionaryResponsibles.map((responsible) => ({
      value: String(responsible.id),
      label: responsible.fullName,
    })),
  ];

  const tableMenuTitle =
    selectedTableClientCount === 1
      ? singleSelectedTableClient?.name ?? 'Клиент'
      : `Выбрано: ${selectedTableClientCount}`;

  const tableMenuActions: TableContextMenuAction[] = [
    {
      key: 'add',
      label: 'Добавить',
      onSelect: handleAddClient,
      disabled: !canCreateClients,
    },
    {
      key: 'edit',
      label: 'Редактировать',
      onSelect: () => {
        if (singleSelectedTableClient) {
          handleEditClient(singleSelectedTableClient);
        }
      },
      disabled: !canEditClients || selectedTableClientCount !== 1 || !singleSelectedTableClient,
    },
    {
      key: 'refresh',
      label: 'Обновить',
      onSelect: async () => {
        await handleRefreshClients();
      },
    },
    {
      key: 'changeResponsible',
      label: 'Смена ответственного',
      onSelect: openChangeResponsiblePrompt,
      disabled: !canEditClients || selectedTableClientCount === 0,
      separatorBefore: true,
    },
    {
      key: 'delete',
      label: 'Удалить',
      onSelect: () => void handleDeleteSelectedClients(selectedTableClientIds),
      disabled: !canDeleteClients || selectedTableClientCount === 0,
      variant: 'danger',
      separatorBefore: true,
    },
  ];

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
        <div className="mb-6 sm:mb-8">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
            <div className="flex flex-col items-center sm:items-start text-center sm:text-left gap-2">
              <div className="flex items-center gap-3">
                <Users size={32} className="text-blue-600" />
                <h1 className="text-2xl sm:text-3xl font-bold text-gray-900">{t('clients')}</h1>
              </div>
              <p className="text-gray-600">{t('manageClients')}</p>
            </div>
            <div className="w-full lg:w-auto flex flex-col gap-2 md:grid md:grid-cols-2 lg:flex lg:flex-row lg:items-center">
              <div className="relative w-full lg:w-64">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2" size={16} />
                <input
                  type="text"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder={t('search') || 'Поиск по имени, телефону, компании или email'}
                  className="pl-9 pr-14 py-2 w-full rounded-lg border border-gray-200 bg-white text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
                {refreshing && (
                  <Loader2
                    className="absolute right-8 top-1/2 -translate-y-1/2 animate-spin text-gray-400"
                    size={14}
                  />
                )}
                {query && (
                  <button
                    type="button"
                    aria-label={t('clear') || 'Очистить'}
                    className="absolute right-1 top-1/2 -translate-y-1/2 p-1 rounded hover:bg-gray-100"
                    onClick={() => setQuery('')}>
                    <X size={14} />
                  </button>
                )}
              </div>
              <div className="w-full lg:w-56">
                <SystemSelect
                  value={statusFilter}
                  onChange={(e) => setStatusFilter(e.target.value)}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="all">Все статусы</option>
                  <option value="none">Без статуса</option>
                  {clientStatuses.map((status) => (
                    <option key={status.id} value={String(status.id)}>
                      {status.name}
                    </option>
                  ))}
                </SystemSelect>
              </div>
              <div className="w-full lg:w-40">
                <SystemSelect
                  value={activityFilter}
                  onChange={(e) => setActivityFilter(e.target.value as 'all' | 'active' | 'inactive')}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="all">Все</option>
                  <option value="active">Активные</option>
                  <option value="inactive">Неактивные</option>
                </SystemSelect>
              </div>
              <div className="flex w-full justify-center gap-1 rounded-lg bg-gray-100 p-1 md:w-auto md:justify-start">
                <button
                  type="button"
                  onClick={() => handleViewModeChange('cards')}
                  className={`p-2 rounded transition-colors ${
                    viewMode === 'cards'
                      ? 'bg-white text-blue-600 shadow-sm'
                      : 'text-gray-600 hover:text-gray-900'
                  }`}
                  title="Карточки">
                  <LayoutGrid size={18} />
                </button>
                <button
                  type="button"
                  onClick={() => handleViewModeChange('table')}
                  className={`p-2 rounded transition-colors ${
                    viewMode === 'table'
                      ? 'bg-white text-blue-600 shadow-sm'
                      : 'text-gray-600 hover:text-gray-900'
                  }`}
                  title="Таблица">
                  <List size={18} />
                </button>
              </div>
              {viewMode === 'table' && (
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => setSettingsOpen((prev) => !prev)}
                    className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition-colors ${
                      settingsOpen
                        ? 'border-blue-200 bg-blue-50 text-blue-700'
                        : 'border-gray-200 bg-white text-gray-600 hover:bg-gray-50'
                    }`}
                  >
                    <SlidersHorizontal size={16} />
                    Колонки
                  </button>
                  <button
                    type="button"
                    onClick={() => setFiltersOpen((prev) => !prev)}
                    className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition-colors ${
                      filtersOpen
                        ? 'border-blue-200 bg-blue-50 text-blue-700'
                        : 'border-gray-200 bg-white text-gray-600 hover:bg-gray-50'
                    }`}
                  >
                    <Filter size={16} />
                    Фильтры
                  </button>
                </div>
              )}
              {canCreateClients && (
                <button
                  type="button"
                  onClick={handleAddClient}
                  className="w-full lg:w-auto flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors font-medium">
                  <Plus size={20} />
                  {t('addClient')}
                </button>
              )}
            </div>
          </div>
        </div>

        {viewMode === 'table' && settingsOpen && (
          <div className="mb-6">
            <TableColumnSettings
              columns={availableColumnDefinitions.map((column) => ({
                key: column.id,
                label: column.label,
              }))}
              columnOrder={columnOrder}
              visibleColumns={visibleColumnIds}
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
                  <SystemSelect
                    value={pageSize}
                    onChange={(event) => setPageSizeValue(Number(event.target.value))}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                  >
                    {PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </SystemSelect>
                </div>
              }
            />
          </div>
        )}

        {viewMode === 'table' && filtersOpen && (
          <div className="mb-6 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
              <div>
                <h2 className="text-sm font-semibold text-gray-900">Фильтры по колонкам</h2>
                <p className="text-xs text-gray-500">
                  Выберите колонки (удерживайте CTRL для нескольких) и задайте значения фильтров.
                </p>
              </div>
              <button
                type="button"
                onClick={resetFilters}
                className="text-sm text-blue-600 hover:underline"
              >
                Сбросить фильтры
              </button>
            </div>
            <div className="grid gap-4 lg:grid-cols-[280px,1fr]">
              <div>
                <label className="block text-xs font-medium text-gray-600 mb-2">
                  Колонки для фильтра
                </label>
                <SystemSelect
                  multiple
                  value={activeFilterColumns}
                  onChange={handleFilterColumnsChange}
                  className="w-full min-h-[160px] rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  {filterableColumns.map((column) => (
                    <option key={column.id} value={column.id}>
                      {column.label}
                    </option>
                  ))}
                </SystemSelect>
              </div>
              <div className="grid gap-3 sm:grid-cols-2">
                {activeFilterColumns.length === 0 ? (
                  <div className="text-sm text-gray-500">
                    Выберите хотя бы одну колонку, чтобы задать фильтр.
                  </div>
                ) : (
                  activeFilterColumns.map((columnId) => (
                    <div key={columnId}>
                      <label className="block text-xs font-medium text-gray-600 mb-2">
                        {COLUMN_META[columnId].label}
                      </label>
                      <input
                        type="text"
                        value={filterSettings.filters[columnId] ?? ''}
                        onChange={(event) => handleFilterValueChange(columnId, event.target.value)}
                        className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                        placeholder="Введите значение"
                      />
                    </div>
                  ))
                )}
              </div>
            </div>
          </div>
        )}

        {!hasAnyClients && !isFiltered ? (
          <div className="text-center py-12">
            <Users size={48} className="mx-auto mb-4 text-gray-300" />
            <h3 className="text-lg font-medium text-gray-900 mb-2">{t('noClientsYet')}</h3>
            <p className="text-gray-500 mb-4">{t('getStartedByAdding')}</p>
            {canCreateClients ? (
              <button
                type="button"
                onClick={handleAddClient}
                className="bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors">
                {t('addClient')}
              </button>
            ) : (
              <p className="text-sm text-gray-500">
                У вас нет прав для добавления клиентов.
              </p>
            )}
          </div>
        ) : sortedClients.length === 0 ? (
          <div className="text-center py-12">
            <Users size={48} className="mx-auto mb-4 text-gray-300" />
            <h3 className="text-lg font-medium text-gray-900 mb-2">
              {t('nothingFound') || 'Ничего не найдено'}
            </h3>
            <p className="text-gray-500 mb-4">
              {t('tryAnotherQuery') || 'Попробуйте изменить поисковый запрос.'}
            </p>
            {query && (
              <button
                type="button"
                onClick={() => setQuery('')}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-gray-200 hover:bg-gray-50">
                <X size={16} />
                {t('clear') || 'Очистить поиск'}
              </button>
            )}
          </div>
        ) : viewMode === 'table' ? (
          <div className="bg-white rounded-xl shadow-sm border border-gray-100 overflow-hidden">
            <div className="overflow-x-auto">
              <table
                className="min-w-full table-fixed"
                style={{ width: getTableWidth(52 + 140), minWidth: '100%' }}
              >
                <colgroup>
                  <col style={{ width: 52 }} />
                  {visibleColumnIds.map((columnId) => (
                    <col key={columnId} style={{ width: getColumnWidth(columnId) }} />
                  ))}
                  <col style={{ width: 140 }} />
                </colgroup>
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    <th className="px-4 py-3 text-center">
                      <TableSelectCheckbox
                        checked={allVisibleSelected}
                        indeterminate={someVisibleSelected}
                        onChange={(event) => toggleAllTableClients(event.target.checked)}
                        aria-label="Выбрать все строки"
                      />
                    </th>
                    {visibleColumnIds.map((columnId) => {
                      const meta = COLUMN_META[columnId];
                      const width = getColumnWidth(columnId);
                      return (
                        <th
                          key={columnId}
                          style={{ width }}
                          className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wider ${
                            draggingColumn === columnId ? 'bg-blue-50 text-blue-700' : 'text-gray-700'
                          }`}
                          onDragOver={handleColumnDragOver}
                          onDrop={(event) => handleColumnDrop(columnId, event)}
                        >
                          <div
                            className="inline-flex items-center gap-2 cursor-move select-none"
                            draggable
                            onDragStart={(event) => handleColumnDragStart(columnId, event)}
                            onDragEnd={handleColumnDragEnd}
                            title="Перетащите, чтобы изменить порядок"
                          >
                            <GripVertical size={14} className="text-gray-400" />
                            {meta.label}
                          </div>
                          <div
                            role="separator"
                            aria-label={`Изменить ширину колонки ${meta.label}`}
                            className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                            onMouseDown={(e) => handleResizeStart(columnId, e)}
                            onDragStart={(event) => event.preventDefault()}
                          />
                        </th>
                      );
                    })}
                    <th className="px-4 py-3 text-right text-xs font-semibold text-gray-700 uppercase tracking-wider">
                      Действия
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {sortedClients.map((client) => (
                    <tr
                      key={client.id}
                      onContextMenu={(event) => openTableMenu(event, client)}
                      className={`transition-colors ${
                        isTableClientSelected(client.id)
                          ? 'bg-blue-50/70 hover:bg-blue-50'
                          : 'hover:bg-gray-50'
                      } ${client.isActive ? '' : 'bg-gray-50 opacity-70'}`}
                      title="ПКМ для действий"
                    >
                      <td className="px-4 py-3 text-center align-top">
                        <TableSelectCheckbox
                          checked={isTableClientSelected(client.id)}
                          onChange={(event) => toggleTableClientSelection(client.id, event.target.checked)}
                          onClick={(event) => event.stopPropagation()}
                          aria-label={`Выбрать клиента ${client.name}`}
                        />
                      </td>
                      {visibleColumnIds.map((columnId) => (
                        <td
                          key={columnId}
                          style={{ width: getColumnWidth(columnId) }}
                          className="px-4 py-3 align-top max-w-0 overflow-hidden"
                        >
                          {renderCell(client, columnId)}
                        </td>
                      ))}
                      <td className="px-4 py-3">
                        <div className="flex items-center justify-end gap-1">
                          {canCreateClients && (
                            <button
                              type="button"
                              onClick={() => openAddCase(client.id)}
                              className="p-2 text-gray-400 hover:text-emerald-600 hover:bg-emerald-50 rounded-lg transition-colors"
                              title="Добавить дело"
                            >
                              <PlusCircle size={16} />
                            </button>
                          )}
                          {canEditClients && (
                            <button
                              type="button"
                              onClick={() => handleEditClient(client)}
                              className="p-2 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded-lg transition-colors"
                              title="Открыть карточку клиента"
                            >
                              <Edit size={16} />
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 sm:gap-6 items-stretch min-h-0">
            {sortedClients.map((client) => {
              const cases = (client.cases ?? []) as ClientCase[];
              const totalCases = getClientCaseCount(client);
              const hasCasePreviews = cases.length > 0;

              const counts = cases.reduce<Record<string, number>>((acc, c) => {
                const key = (c.status ?? 'unknown').toLowerCase();
                acc[key] = (acc[key] ?? 0) + 1;
                return acc;
              }, {});

              const sortedByStatusThenDate = [...cases].sort((a, b) => {
                const ra = statusOrder(a.status);
                const rb = statusOrder(b.status);
                if (ra !== rb) return ra - rb;
                return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
              });

              const isExpanded = expandedCards.has(client.id);

              const nonClosed = sortedByStatusThenDate.filter((k) => statusOrder(k.status) < 2);
              const visibleCases = isExpanded
                ? sortedByStatusThenDate
                : nonClosed.length
                ? nonClosed.slice(0, 3)
                : sortedByStatusThenDate.slice(0, 3);

              const hiddenCount = Math.max(0, sortedByStatusThenDate.length - visibleCases.length);

              return (
                <div
                  key={client.id}
                  className={`h-full flex flex-col overflow-hidden rounded-xl p-5 sm:p-6 shadow-sm border border-gray-100 hover:shadow-md transition-shadow
                  ${client.isActive ? 'bg-white' : 'bg-gray-100 opacity-70'}`}>
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between mb-4">
                    <div className="flex items-center gap-3 min-w-0 flex-1">
                      <button
                        type="button"
                        onClick={() => handleOpenClient(client.id)}
                        className="w-12 h-12 bg-blue-100 rounded-lg flex items-center justify-center shrink-0 hover:bg-blue-200 transition-colors"
                        title="Открыть карточку клиента"
                      >
                        <Users size={24} className="text-blue-600" />
                      </button>
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <button
                            type="button"
                            onClick={() => handleOpenClient(client.id)}
                            className="font-semibold text-gray-900 block max-w-full truncate hover:underline text-left"
                            title="Открыть карточку клиента"
                          >
                            {highlight(client.name ?? '', debouncedQuery)}
                          </button>
                          {client.responsibleUserName && (
                            <span className="inline-flex items-center rounded-full border border-slate-200 bg-slate-100 px-2.5 py-1 text-[11px] font-medium text-slate-700">
                              Ответственный: {client.responsibleUserName}
                            </span>
                          )}
                          <ClientStatusBadge status={client.clientStatus} />
                        </div>
                        {client.company && (
                          <p className="text-sm text-gray-500 block max-w-full truncate">
                            {highlight(client.company ?? '', debouncedQuery)}
                          </p>
                        )}
                      </div>
                    </div>
                    <div className="flex gap-1 self-end sm:self-auto shrink-0">
                      {canCreateClients && (
                        <button
                          type="button"
                          onClick={() => openAddCase(client.id)}
                          className="p-2 text-gray-400 hover:text-emerald-600 hover:bg-emerald-50 rounded-lg transition-colors"
                          title="Добавить дело">
                          <PlusCircle size={16} />{' '}
                        </button>
                      )}
                      {canEditClients && (
                        <button
                          type="button"
                          onClick={() => handleEditClient(client)}
                          className="p-2 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded-lg transition-colors"
                          title="Открыть карточку клиента">
                          <Edit size={16} />
                        </button>
                      )}
                    </div>
                  </div>
                    <div className="flex-1 min-h-0 flex flex-col gap-1">
                      {client.email && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Mail size={14} />
                          <span className="truncate">{highlight(client.email ?? '', debouncedQuery)}</span>
                        </div>
                      )}
                      {client.phone && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Phone size={14} />
                          <span className="truncate">
                            {highlightPhone(client.phone ?? '', debouncedQuery)}
                          </span>
                        </div>
                      )}
                      {client.legalEntity && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Building2 size={14} className="text-blue-500" />
                          <span className="truncate">
                            {highlight(client.legalEntity.shortName ?? '', debouncedQuery)}
                          </span>
                        </div>
                      )}
                      {client.address && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Building size={14} />
                        <span className="truncate">{client.address}</span>
                      </div>
                    )}
                  </div>
                  <div className="mt-2">
                    <div className="flex items-center gap-2 mb-2">
                      <FolderKanban size={18} className="text-slate-600" />
                      <span className="font-medium text-slate-800">{t('cases') || 'Дела'}</span>
                      <span className="ml-auto text-xs text-slate-500">
                        {t('total') || 'Всего'}: {totalCases}
                      </span>
                    </div>
                    {hasCasePreviews ? (
                      <div className="flex flex-wrap gap-1.5 sm:gap-2 mb-3">
                        {Object.entries(counts)
                          .sort((a, b) => b[1] - a[1])
                          .slice(0, 4)
                          .map(([status, count]) => {
                            const label = caseStatusLabel(status);
                            return (
                              <span
                                key={status}
                                className={`text-[11px] sm:text-xs px-2 py-1 rounded-full ${statusClasses(
                                  status,
                                )}`}
                                title={label}>
                                {label} · {count}
                              </span>
                            );
                          })}
                      </div>
                    ) : totalCases > 0 ? (
                      <div className="text-xs text-slate-500 mb-3">
                        Дела есть в карточке клиента: {totalCases}
                      </div>
                    ) : (
                      <div className="text-xs text-slate-500 mb-3">
                        {t('noCases') || 'Дел пока нет'}
                      </div>
                    )}
                    {visibleCases.length > 0 && (
                      <ul className="space-y-2">
                        {visibleCases.map((c) => (
                          <li
                            key={c.id}
                            className="flex items-start justify-between rounded-lg border border-slate-100 bg-slate-50 px-3 py-2">
                            <div className="min-w-0">
                              <div className="text-sm font-medium text-slate-800 truncate">
                                {c.title}
                              </div>
                              {c.description && (
                                <div className="text-xs text-slate-500 truncate">
                                  {c.description}
                                </div>
                              )}
                              <div className="text-[11px] text-slate-400 mt-0.5">
                                {t('created') || 'Создано'}: {formatDate(c.createdAt, profileTimeZoneId)}
                              </div>
                            </div>
                            <div className="flex items-center gap-1 shrink-0">
                              <span
                                className={`text-[11px] px-2 py-1 rounded-full ${statusClasses(
                                  c.status,
                                )}`}>
                                {caseStatusLabel(c.status)}
                              </span>
                              {canEditClients && (
                                <button
                                  type="button"
                                  onClick={() => openEditCase(client.id, c)}
                                  className="ml-1 p-1.5 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded transition-colors"
                                  title="Редактировать дело">
                                  <Edit size={14} />
                                </button>
                              )}
                            </div>
                          </li>
                        ))}
                      </ul>
                    )}
                    {hiddenCount > 0 && !isExpanded && (
                      <button
                        type="button"
                        onClick={() => toggleCases(client.id)}
                        className="mt-2 text-xs text-blue-600 hover:text-blue-700">
                        Показать ещё {hiddenCount}
                      </button>
                    )}
                    {isExpanded && sortedByStatusThenDate.length > 3 && (
                      <button
                        type="button"
                        onClick={() => toggleCases(client.id)}
                        className="mt-2 text-xs text-blue-600 hover:text-blue-700">
                        Свернуть
                      </button>
                    )}
                  </div>
                  {getDisplayNotes(client.notes) && (
                    <p className="mt-4 text-sm text-gray-500 line-clamp-2">{getDisplayNotes(client.notes)}</p>
                  )}
                </div>
              );
            })}
          </div>
        )}

        {total > 0 && (
          <div className="mt-6">
            <TablePagination
              page={page}
              totalPages={totalPages}
              totalItems={total}
              pageSize={pageSize}
              pageSizeOptions={PAGE_SIZE_OPTIONS}
              onPageChange={setPage}
              onPageSizeChange={setPageSizeValue}
              recordsLabel="Найдено записей"
              pageSizeLabel="Строк на странице"
              previousLabel="Назад"
              nextLabel="Вперед"
            />
          </div>
        )}

        <TableContextMenu
          open={tableMenuPosition !== null}
          position={tableMenuPosition}
          title={tableMenuTitle}
          actions={tableMenuActions}
          onClose={closeTableMenu}
        />

        <FormPromptModal
          open={changeResponsibleOpen}
          title="Смена ответственного"
          description={
            selectedTableClientCount === 1
              ? 'Назначьте нового ответственного для выбранного клиента.'
              : `Назначьте нового ответственного для записей: ${selectedTableClientCount}.`
          }
          fields={[
            {
              key: 'responsibleUserId',
              label: 'Ответственный',
              value: nextResponsibleId,
              onChange: (value) => {
                setNextResponsibleId(value);
                if (changeResponsibleError) {
                  setChangeResponsibleError(null);
                }
              },
              type: 'lookup',
              options: responsibleOptions,
              required: true,
              lookupTitle: 'Выбор ответственного',
              searchPlaceholder: 'Найти ответственного',
              placeholder: 'Не выбрано',
              dictionaryLabel: 'Справочник',
              noOptionsText: responsiblesLoading ? 'Загрузка списка...' : 'Ответственные не найдены',
              disabled: responsiblesLoading,
            },
          ]}
          onCancel={() => {
            setChangeResponsibleOpen(false);
            setChangeResponsibleError(null);
          }}
          onConfirm={submitResponsibleChange}
          confirmLabel="Сохранить"
          cancelLabel="Отмена"
          error={changeResponsibleError ?? undefined}
          loading={changeResponsibleSubmitting || responsiblesLoading}
        />

        <ClientModal
          isOpen={isModalOpen}
          onClose={handleCloseModal}
          onSubmit={handleSubmitClient}
          onDelete={handleDeleteClient}
          client={editingClient ?? undefined}
        />

        {caseModalOpen && (
          <CaseModal
            isOpen={caseModalOpen}
            onClose={() => setCaseModalOpen(false)}
            caseData={
              caseMode === 'edit' && editingCase
                ? editingCase
                : ({
                    id: 0,
                    clientId: caseClientId ?? 0,
                    title: '',
                    description: '',
                    status: 'Open',
                    createdAt: new Date().toISOString(),
                  } as ClientCase)
            }
            onSave={saveCase}
            onNextHearingConfirmed={(confirmation) => {
              setEditingCase((current) => current ? { ...current, ...confirmation } : current);
              setClients((current) => current.map((client) => ({
                ...client,
                cases: client.cases?.map((item) => item.id === editingCase?.id ? { ...item, ...confirmation } : item),
              })));
            }}
            onDelete={caseMode === 'edit' ? deleteCase : undefined}
            canViewFinance={permissions.payments.canView}
            onOpenTask={permissions.tasks.canView ? onOpenTask : undefined}
            canCreateTasks={permissions.tasks.canView && permissions.tasks.canCreate}
          />
        )}
      </div>
    </div>
  );
}
