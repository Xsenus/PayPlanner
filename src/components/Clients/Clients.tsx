import { ReactNode, useEffect, useMemo, useRef, useState } from 'react';
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
  SlidersHorizontal,
  GripVertical,
  Filter,
} from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useTranslation } from '../../hooks/useTranslation';
import { ClientModal } from './ClientModal';
import { ClientDetail } from './ClientDetail';
import type { Client, ClientCase, ClientInput } from '../../types';
import { CaseModal } from './CaseModal';
import { apiService } from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useDialog } from '../../hooks/useDialog';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { ClientStatusBadge } from './ClientStatusBadge';

function statusOrder(statusRaw?: string): number {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 0;
  if (s.includes('onhold') || s.includes('hold')) return 1;
  if (s.includes('closed')) return 2;
  return 99;
}

function caseStatusLabel(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'Открыто';
  if (s.includes('onhold') || s.includes('hold')) return 'Приостановлено';
  if (s.includes('closed')) return 'Закрыто';
  if (s.includes('в работе') || s.includes('актив')) return 'Открыто';
  if (s.includes('ожид')) return 'Приостановлено';
  if (s.includes('закры')) return 'Закрыто';
  return statusRaw ?? '';
}

function statusClasses(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'bg-green-100 text-green-700 border border-green-200';
  if (s.includes('onhold') || s.includes('hold'))
    return 'bg-amber-100 text-amber-800 border border-amber-200';
  if (s.includes('closed')) return 'bg-red-100 text-red-700 border border-red-200';
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

function formatDate(d?: string) {
  if (!d) return '';
  try {
    return new Date(d).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
    });
  } catch {
    return d;
  }
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
  | 'contractsCount';

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
  { id: 'contractsCount', label: 'Договоры', defaultWidth: 130, defaultVisible: false },
];

const COLUMN_META = COLUMN_DEFINITIONS.reduce(
  (acc, col) => {
    acc[col.id] = col;
    return acc;
  },
  {} as Record<ColumnId, ColumnDefinition>,
);

const TABLE_SETTINGS_KEY = 'pp.clients_table_settings.v1';
const TABLE_SETTINGS_DB_KEY = 'clients_table_settings';

type TableSettings = {
  order: ColumnId[];
  visible: Record<ColumnId, boolean>;
  widths: Record<ColumnId, number>;
  filters: Record<ColumnId, string>;
  filterColumns: ColumnId[];
  pageSize: number;
};

const DEFAULT_TABLE_SETTINGS: TableSettings = {
  order: COLUMN_DEFINITIONS.map((col) => col.id),
  visible: COLUMN_DEFINITIONS.reduce(
    (acc, col) => ({ ...acc, [col.id]: col.defaultVisible }),
    {} as Record<ColumnId, boolean>,
  ),
  widths: COLUMN_DEFINITIONS.reduce(
    (acc, col) => ({ ...acc, [col.id]: col.defaultWidth }),
    {} as Record<ColumnId, number>,
  ),
  filters: COLUMN_DEFINITIONS.reduce(
    (acc, col) => ({ ...acc, [col.id]: '' }),
    {} as Record<ColumnId, string>,
  ),
  filterColumns: [],
  pageSize: 25,
};

function normalizeTableSettings(parsed?: Partial<TableSettings> | null): TableSettings {
  const order = Array.isArray(parsed?.order)
    ? parsed?.order.filter((id): id is ColumnId =>
        COLUMN_DEFINITIONS.some((col) => col.id === id),
      )
    : [];
  const fallbackOrder = DEFAULT_TABLE_SETTINGS.order;
  const finalOrder = order.length
    ? [...order, ...fallbackOrder.filter((id) => !order.includes(id))]
    : fallbackOrder;
  const visible = { ...DEFAULT_TABLE_SETTINGS.visible, ...(parsed?.visible ?? {}) };
  const widths = { ...DEFAULT_TABLE_SETTINGS.widths, ...(parsed?.widths ?? {}) };
  const filters = { ...DEFAULT_TABLE_SETTINGS.filters, ...(parsed?.filters ?? {}) };
  const filterColumns = Array.isArray(parsed?.filterColumns)
    ? parsed?.filterColumns.filter((id): id is ColumnId =>
        COLUMN_DEFINITIONS.some((col) => col.id === id),
      )
    : DEFAULT_TABLE_SETTINGS.filterColumns;
  const pageSize =
    typeof parsed?.pageSize === 'number'
      ? Math.max(1, parsed.pageSize)
      : DEFAULT_TABLE_SETTINGS.pageSize;
  return { order: finalOrder, visible, widths, filters, filterColumns, pageSize };
}

function loadTableSettings(): TableSettings {
  try {
    const raw = localStorage.getItem(TABLE_SETTINGS_KEY);
    if (!raw) return DEFAULT_TABLE_SETTINGS;
    const parsed = JSON.parse(raw) as Partial<TableSettings>;
    return normalizeTableSettings(parsed);
  } catch {
    return DEFAULT_TABLE_SETTINGS;
  }
}

function saveTableSettings(settings: TableSettings) {
  try {
    localStorage.setItem(TABLE_SETTINGS_KEY, JSON.stringify(settings));
  } catch {
    /** */
  }
}

export function Clients() {
  const { statuses: clientStatuses } = useClientStatuses();
  const { t } = useTranslation();
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const { alert } = useDialog();
  const clientPermissions = permissions.clients;
  const canViewClients = clientPermissions.canView;
  const canCreateClients = clientPermissions.canCreate;
  const canEditClients = clientPermissions.canEdit;
  const canDeleteClients = clientPermissions.canDelete;

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [editingClient, setEditingClient] = useState<Client | null>(null);

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [caseClientId, setCaseClientId] = useState<number | null>(null);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('edit');

  const [expandedCards, setExpandedCards] = useState<Set<number>>(new Set());

  const [query, setQuery] = useState('');
  const [viewMode, setViewMode] = useState<'cards' | 'table'>(() => {
    try {
      return (localStorage.getItem('pp.clients_view') as 'cards' | 'table') || 'cards';
    } catch {
      return 'cards';
    }
  });
  const [statusFilter, setStatusFilter] = useState<string>('all');
  const [activityFilter, setActivityFilter] = useState<'all' | 'active' | 'inactive'>('all');
  const [tableSettings, setTableSettings] = useState<TableSettings>(() => {
    try {
      return loadTableSettings();
    } catch {
      return DEFAULT_TABLE_SETTINGS;
    }
  });
  const [page, setPage] = useState(1);
  const pageSize = tableSettings.pageSize;

  const filterableColumns = useMemo(
    () => COLUMN_DEFINITIONS.filter((col) => col.filterable),
    [],
  );

  const activeFilterColumns = tableSettings.filterColumns;

  const filterParamMap = useMemo(
    () =>
      ({
        id: 'id',
        name: 'name',
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
      }) satisfies Record<ColumnId, string>,
    [],
  );

  const activeFilters = useMemo(() => {
    const filters: Record<string, string> = {};
    activeFilterColumns.forEach((columnId) => {
      const raw = tableSettings.filters[columnId]?.trim();
      if (raw) {
        const paramKey = filterParamMap[columnId] ?? columnId;
        filters[paramKey] = raw;
      }
    });
    return filters;
  }, [activeFilterColumns, tableSettings.filters, filterParamMap]);

  const clientsQueryOptions = useMemo(
    () => ({
      mode: 'paged' as const,
      search: query.trim() || undefined,
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
    [query, activityFilter, page, pageSize, statusFilter, activeFilters],
  );

  const { clients, loading, total, createClient, updateClient, deleteClient, setClients, refresh } =
    useClients(clientsQueryOptions);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [settingsLoaded, setSettingsLoaded] = useState(false);
  const [draggingColumnId, setDraggingColumnId] = useState<ColumnId | null>(null);
  const resizingRef = useRef<{
    columnId: ColumnId;
    startX: number;
    startWidth: number;
  } | null>(null);
  const saveTimerRef = useRef<number | null>(null);
  const sortedClients = clients;

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
    void alert({ title: 'Недостаточно прав', description: message || 'Недостаточно прав для выполнения действия.', variant: 'error' });
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

    setClients((prev: Client[]) => prev.filter((c: Client) => c.id !== id));

    try {
      await deleteClient(id);
    } catch (err) {
      console.error(err);
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
    const loadFromServer = async () => {
      try {
        const response = await apiService.getUserSetting(TABLE_SETTINGS_DB_KEY);
        if (!active || !response?.value) return;
        const parsed = JSON.parse(response.value) as Partial<TableSettings>;
        const normalized = normalizeTableSettings(parsed);
        setTableSettings(normalized);
        saveTableSettings(normalized);
      } catch {
        /** */
      } finally {
        if (active) setSettingsLoaded(true);
      }
    };
    void loadFromServer();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    saveTableSettings(tableSettings);
    if (!settingsLoaded) return;
    if (saveTimerRef.current) {
      window.clearTimeout(saveTimerRef.current);
    }
    saveTimerRef.current = window.setTimeout(() => {
      void apiService.upsertUserSetting(TABLE_SETTINGS_DB_KEY, JSON.stringify(tableSettings));
    }, 600);
    return () => {
      if (saveTimerRef.current) {
        window.clearTimeout(saveTimerRef.current);
      }
    };
  }, [settingsLoaded, tableSettings]);

  useEffect(() => {
    if (viewMode !== 'table') {
      setSettingsOpen(false);
      setFiltersOpen(false);
    }
  }, [viewMode]);

  useEffect(() => {
    setPage(1);
  }, [query, statusFilter, activityFilter, activeFilters, pageSize]);

  useEffect(() => {
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, totalPages]);

  const visibleColumnIds = useMemo(
    () => tableSettings.order.filter((id) => tableSettings.visible[id]),
    [tableSettings],
  );

  const resetTableSettings = () => {
    setTableSettings(DEFAULT_TABLE_SETTINGS);
    setPage(1);
  };

  const handleFilterColumnsChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    const selected = Array.from(event.target.selectedOptions).map((option) => option.value as ColumnId);
    setTableSettings((prev) => ({ ...prev, filterColumns: selected }));
  };

  const handleFilterValueChange = (columnId: ColumnId, value: string) => {
    setTableSettings((prev) => ({
      ...prev,
      filters: { ...prev.filters, [columnId]: value },
    }));
  };

  const resetFilters = () => {
    setTableSettings((prev) => ({
      ...prev,
      filters: { ...DEFAULT_TABLE_SETTINGS.filters },
      filterColumns: [],
    }));
    setPage(1);
  };

  const handlePageSizeChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    const next = Number(event.target.value);
    setTableSettings((prev) => ({
      ...prev,
      pageSize: Number.isFinite(next) && next > 0 ? next : prev.pageSize,
    }));
    setPage(1);
  };

  const toggleColumn = (columnId: ColumnId) => {
    setTableSettings((prev) => ({
      ...prev,
      visible: { ...prev.visible, [columnId]: !prev.visible[columnId] },
    }));
  };

  const moveColumn = (columnId: ColumnId, direction: 'up' | 'down') => {
    setTableSettings((prev) => {
      const order = [...prev.order];
      const idx = order.indexOf(columnId);
      if (idx === -1) return prev;
      const target = direction === 'up' ? idx - 1 : idx + 1;
      if (target < 0 || target >= order.length) return prev;
      [order[idx], order[target]] = [order[target], order[idx]];
      return { ...prev, order };
    });
  };

  const reorderColumns = (sourceId: ColumnId, targetId: ColumnId) => {
    if (sourceId === targetId) return;
    setTableSettings((prev) => {
      const order = [...prev.order];
      const fromIndex = order.indexOf(sourceId);
      const toIndex = order.indexOf(targetId);
      if (fromIndex === -1 || toIndex === -1) return prev;
      order.splice(fromIndex, 1);
      order.splice(toIndex, 0, sourceId);
      return { ...prev, order };
    });
  };

  const handleColumnDragStart = (columnId: ColumnId, event: React.DragEvent<HTMLElement>) => {
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', columnId);
    setDraggingColumnId(columnId);
  };

  const handleColumnDragOver = (event: React.DragEvent<HTMLElement>) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  };

  const handleColumnDrop = (targetId: ColumnId, event: React.DragEvent<HTMLElement>) => {
    event.preventDefault();
    const sourceId = event.dataTransfer.getData('text/plain') as ColumnId;
    if (!sourceId) return;
    reorderColumns(sourceId, targetId);
    setDraggingColumnId(null);
  };

  const handleColumnDragEnd = () => {
    setDraggingColumnId(null);
  };

  const handleResizeStart = (columnId: ColumnId, e: React.MouseEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    const startWidth = tableSettings.widths[columnId] ?? 120;
    const previousCursor = document.body.style.cursor;
    const previousSelect = document.body.style.userSelect;
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    resizingRef.current = {
      columnId,
      startX: e.clientX,
      startWidth,
    };

    const handleMouseMove = (event: MouseEvent) => {
      if (!resizingRef.current) return;
      const delta = event.clientX - resizingRef.current.startX;
      const nextWidth = Math.max(80, resizingRef.current.startWidth + delta);
      setTableSettings((prev) => ({
        ...prev,
        widths: { ...prev.widths, [columnId]: nextWidth },
      }));
    };

    const handleMouseUp = () => {
      resizingRef.current = null;
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousSelect;
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
    };

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
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
        onBack={() => setSelectedClientId(null)}
        initialCaseId={initialCaseId}
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

  const handleOpenClient = (clientId: number) => {
    setSelectedClientId(clientId);
    setInitialCaseId('all');
  };

  const getColumnWidth = (columnId: ColumnId) =>
    tableSettings.widths[columnId] ?? COLUMN_META[columnId].defaultWidth;

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
                {highlight(client.name ?? '', query)}
              </button>
            </div>
          </div>
        );
      case 'status':
        return <ClientStatusBadge status={client.clientStatus} />;
      case 'company':
        return (
          <span className="text-sm text-gray-700 block truncate">
            {highlight(client.company ?? '', query)}
          </span>
        );
      case 'email':
        return client.email ? (
          <span className="text-sm text-gray-700 block truncate">
            {highlight(client.email ?? '', query)}
          </span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'phone':
        return client.phone ? (
          <span className="text-sm text-gray-700 block truncate">
            {highlightPhone(client.phone ?? '', query)}
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
        return client.notes ? (
          <span className="text-sm text-gray-700 block truncate">{client.notes}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'createdAt':
        return <span className="text-sm text-gray-600 block truncate">{formatDate(client.createdAt)}</span>;
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
            {highlight(client.legalEntity.shortName ?? '', query)}
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
        return (
          <button
            type="button"
            onClick={() => handleOpenClient(client.id)}
            className="text-sm text-blue-600 hover:underline"
          >
            {(client.cases ?? []).length}
          </button>
        );
      case 'contractsCount':
        return (
          <span className="text-sm text-gray-700 block truncate">{(client.contracts ?? []).length}</span>
        );
      default:
        return null;
    }
  };

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
        <div className="mb-6 sm:mb-8">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-col items-center sm:items-start text-center sm:text-left gap-2">
              <div className="flex items-center gap-3">
                <Users size={32} className="text-blue-600" />
                <h1 className="text-2xl sm:text-3xl font-bold text-gray-900">{t('clients')}</h1>
              </div>
              <p className="text-gray-600">{t('manageClients')}</p>
            </div>
            <div className="w-full sm:w-auto flex flex-col sm:flex-row gap-2 sm:items-center">
              <div className="relative w-full sm:w-64">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2" size={16} />
                <input
                  type="text"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder={t('search') || 'Поиск по имени, телефону, компании или email'}
                  className="pl-9 pr-8 py-2 w-full rounded-lg border border-gray-200 bg-white text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
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
              <div className="w-full sm:w-56">
                <select
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
                </select>
              </div>
              <div className="w-full sm:w-40">
                <select
                  value={activityFilter}
                  onChange={(e) => setActivityFilter(e.target.value as 'all' | 'active' | 'inactive')}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="all">Все</option>
                  <option value="active">Активные</option>
                  <option value="inactive">Неактивные</option>
                </select>
              </div>
              <div className="flex gap-1 bg-gray-100 p-1 rounded-lg">
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
                  className="w-full sm:w-auto flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors font-medium">
                  <Plus size={20} />
                  {t('addClient')}
                </button>
              )}
            </div>
          </div>
        </div>

        {viewMode === 'table' && settingsOpen && (
          <div className="mb-6 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
              <div>
                <h2 className="text-sm font-semibold text-gray-900">Настройка таблицы</h2>
                <p className="text-xs text-gray-500">
                  Меняйте порядок кнопками, включайте нужные поля и меняйте ширину.
                </p>
              </div>
              <button
                type="button"
                onClick={resetTableSettings}
                className="text-sm text-blue-600 hover:underline"
              >
                Сбросить настройки
              </button>
            </div>
            <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {tableSettings.order.map((columnId, index) => {
                const meta = COLUMN_META[columnId];
                return (
                  <div
                    key={columnId}
                    className={`flex items-center gap-3 rounded-lg border px-3 py-2 ${
                      draggingColumnId === columnId
                        ? 'border-blue-200 bg-blue-50'
                        : 'border-gray-100 bg-gray-50'
                    }`}
                    draggable
                    onDragStart={(event) => handleColumnDragStart(columnId, event)}
                    onDragOver={handleColumnDragOver}
                    onDrop={(event) => handleColumnDrop(columnId, event)}
                    onDragEnd={handleColumnDragEnd}
                  >
                    <span
                      className="cursor-grab text-gray-400 active:cursor-grabbing"
                      aria-hidden="true"
                    >
                      <GripVertical size={16} />
                    </span>
                    <input
                      type="checkbox"
                      checked={tableSettings.visible[columnId]}
                      onChange={() => toggleColumn(columnId)}
                      className="h-4 w-4 rounded border-gray-300 text-blue-600"
                    />
                    <span className="text-sm text-gray-700 flex-1">{meta.label}</span>
                    <div className="flex items-center gap-1">
                      <button
                        type="button"
                        onClick={() => moveColumn(columnId, 'up')}
                        disabled={index === 0}
                        className="h-6 w-6 rounded border border-gray-200 bg-white text-xs text-gray-500 disabled:opacity-40"
                        aria-label="Переместить вверх"
                      >
                        ↑
                      </button>
                      <button
                        type="button"
                        onClick={() => moveColumn(columnId, 'down')}
                        disabled={index === tableSettings.order.length - 1}
                        className="h-6 w-6 rounded border border-gray-200 bg-white text-xs text-gray-500 disabled:opacity-40"
                        aria-label="Переместить вниз"
                      >
                        ↓
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>
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
                <select
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
                </select>
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
                        value={tableSettings.filters[columnId] ?? ''}
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
              <table className="min-w-full table-fixed">
                <colgroup>
                  {visibleColumnIds.map((columnId) => (
                    <col key={columnId} style={{ width: getColumnWidth(columnId) }} />
                  ))}
                  <col style={{ width: 140 }} />
                </colgroup>
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    {visibleColumnIds.map((columnId) => {
                      const meta = COLUMN_META[columnId];
                      const width = getColumnWidth(columnId);
                      return (
                        <th
                          key={columnId}
                          style={{ width }}
                          className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wider ${
                            draggingColumnId === columnId ? 'bg-blue-50 text-blue-700' : 'text-gray-700'
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
                      className={`hover:bg-gray-50 transition-colors ${
                        client.isActive ? '' : 'bg-gray-50 opacity-70'
                      }`}
                    >
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
              const totalCases = cases.length;

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

              const nonClosed = sortedByStatusThenDate.filter((k) => statusOrder(k.status) !== 2);
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
                            {highlight(client.name ?? '', query)}
                          </button>
                          <ClientStatusBadge status={client.clientStatus} />
                        </div>
                        {client.company && (
                          <p className="text-sm text-gray-500 block max-w-full truncate">
                            {highlight(client.company ?? '', query)}
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
                          <span className="truncate">{highlight(client.email ?? '', query)}</span>
                        </div>
                      )}
                      {client.phone && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Phone size={14} />
                          <span className="truncate">
                            {highlightPhone(client.phone ?? '', query)}
                          </span>
                        </div>
                      )}
                      {client.legalEntity && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Building2 size={14} className="text-blue-500" />
                          <span className="truncate">
                            {highlight(client.legalEntity.shortName ?? '', query)}
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
                    {totalCases > 0 ? (
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
                                {t('created') || 'Создано'}: {formatDate(c.createdAt)}
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
                  {client.notes && (
                    <p className="mt-4 text-sm text-gray-500 line-clamp-2">{client.notes}</p>
                  )}
                </div>
              );
            })}
          </div>
        )}

        {total > 0 && (
          <div className="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-gray-200 bg-white px-4 py-3 shadow-sm">
            <div className="flex items-center gap-2 text-sm text-gray-600">
              <span>Страница</span>
              <span className="font-semibold text-gray-900">
                {page} из {totalPages}
              </span>
              <span className="text-gray-400">·</span>
              <span>Всего: {total}</span>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() => setPage((prev) => Math.max(1, prev - 1))}
                disabled={page <= 1}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-50 disabled:opacity-40"
              >
                Назад
              </button>
              <button
                type="button"
                onClick={() => setPage((prev) => Math.min(totalPages, prev + 1))}
                disabled={page >= totalPages}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-50 disabled:opacity-40"
              >
                Вперёд
              </button>
              <div className="flex items-center gap-2 text-sm text-gray-600">
                <span>Показывать</span>
                <select
                  value={pageSize}
                  onChange={handlePageSizeChange}
                  className="rounded-lg border border-gray-200 bg-white px-2 py-1 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  {[5, 10, 25, 50, 100].map((size) => (
                    <option key={size} value={size}>
                      {size}
                    </option>
                  ))}
                </select>
                <span>записей</span>
              </div>
            </div>
          </div>
        )}

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
            onDelete={caseMode === 'edit' ? deleteCase : undefined}
          />
        )}
      </div>
    </div>
  );
}
