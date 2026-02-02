import { ReactNode, useEffect, useMemo, useRef, useState, type MouseEvent } from 'react';
import {
  Users,
  Plus,
  Building,
  Building2,
  Mail,
  Phone,
  Eye,
  Edit,
  FolderKanban,
  PlusCircle,
  Search,
  X,
  LayoutGrid,
  List,
  Ban,
  ChevronDown,
  ChevronUp,
  GripVertical,
  Columns,
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
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { ClientStatusBadge } from './ClientStatusBadge';

type ClientColumnId =
  | 'id'
  | 'name'
  | 'company'
  | 'email'
  | 'phone'
  | 'status'
  | 'clientStatusId'
  | 'legalEntity'
  | 'legalEntityId'
  | 'legalInn'
  | 'legalOgrn'
  | 'legalKpp'
  | 'address'
  | 'notes'
  | 'isActive'
  | 'createdAt'
  | 'cases'
  | 'actions';

type ClientColumnMeta = {
  id: ClientColumnId;
  label: string;
  defaultVisible: boolean;
  minWidth?: number;
  align?: 'left' | 'right';
};

const CLIENT_COLUMNS: ClientColumnMeta[] = [
  { id: 'id', label: 'ID', defaultVisible: false, minWidth: 90 },
  { id: 'name', label: 'Клиент', defaultVisible: true, minWidth: 260 },
  { id: 'company', label: 'Компания', defaultVisible: true, minWidth: 200 },
  { id: 'status', label: 'Статус', defaultVisible: true, minWidth: 160 },
  { id: 'clientStatusId', label: 'ID статуса', defaultVisible: false, minWidth: 140 },
  { id: 'email', label: 'E-mail', defaultVisible: false, minWidth: 220 },
  { id: 'phone', label: 'Телефон', defaultVisible: false, minWidth: 180 },
  { id: 'legalEntity', label: 'Юр. лицо', defaultVisible: true, minWidth: 220 },
  { id: 'legalEntityId', label: 'ID юр. лица', defaultVisible: false, minWidth: 140 },
  { id: 'legalInn', label: 'ИНН', defaultVisible: false, minWidth: 160 },
  { id: 'legalOgrn', label: 'ОГРН', defaultVisible: false, minWidth: 160 },
  { id: 'legalKpp', label: 'КПП', defaultVisible: false, minWidth: 160 },
  { id: 'address', label: 'Адрес', defaultVisible: false, minWidth: 240 },
  { id: 'notes', label: 'Комментарий', defaultVisible: false, minWidth: 240 },
  { id: 'cases', label: 'Дела', defaultVisible: true, minWidth: 160 },
  { id: 'isActive', label: 'Активность', defaultVisible: false, minWidth: 140 },
  { id: 'createdAt', label: 'Создан', defaultVisible: false, minWidth: 150 },
  { id: 'actions', label: 'Действия', defaultVisible: true, minWidth: 160, align: 'right' },
];

const CLIENT_TABLE_SETTINGS_KEY = 'pp.clients_table_settings';
const CLIENT_STATUS_NONE = 'no-status';

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

function matchesClient(c: Client, q: string): boolean {
  const ql = q.trim().toLowerCase();
  if (!ql) return true;

  const qDigits = q.replace(/\D/g, '');
  const name = (c.name ?? '').toLowerCase();
  const company = (c.company ?? '').toLowerCase();
  const email = (c.email ?? '').toLowerCase();
  const phone = (c.phone ?? '').toLowerCase();
  const phoneDigits = (c.phone ?? '').replace(/\D/g, '');
  const legal = c.legalEntity;
  const legalShort = (legal?.shortName ?? '').toLowerCase();
  const legalFull = (legal?.fullName ?? '').toLowerCase();
  const legalInn = (legal?.inn ?? '').toLowerCase();
  const legalOgrn = (legal?.ogrn ?? '').toLowerCase();
  const legalKpp = (legal?.kpp ?? '').toLowerCase();
  const statusName = (c.clientStatus?.name ?? '').toLowerCase();
  const legalDigits = [legal?.inn ?? '', legal?.ogrn ?? '', legal?.kpp ?? '']
    .map((value) => value.replace(/\D/g, ''))
    .join(' ');

  return (
    name.includes(ql) ||
    company.includes(ql) ||
    email.includes(ql) ||
    phone.includes(ql) ||
    statusName.includes(ql) ||
    legalShort.includes(ql) ||
    legalFull.includes(ql) ||
    legalInn.includes(ql) ||
    legalOgrn.includes(ql) ||
    legalKpp.includes(ql) ||
    (!!qDigits && (phoneDigits.includes(qDigits) || legalDigits.includes(qDigits)))
  );
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

export function Clients() {
  const { clients, loading, createClient, updateClient, deleteClient, setClients, refresh } =
    useClients();
  const { legalEntities, loading: legalEntitiesLoading } = useLegalEntities();
  const { statuses: clientStatuses } = useClientStatuses();
  const { t } = useTranslation();
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
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
  const [activityFilter, setActivityFilter] = useState<'all' | 'active' | 'inactive'>('all');
  const [statusFilters, setStatusFilters] = useState<string[]>([]);
  const [statusFilterOpen, setStatusFilterOpen] = useState(false);
  const statusFilterRef = useRef<HTMLDivElement | null>(null);
  const [viewMode, setViewMode] = useState<'cards' | 'table'>(() => {
    try {
      return (localStorage.getItem('pp.clients_view') as 'cards' | 'table') || 'cards';
    } catch {
      return 'cards';
    }
  });
  const [tableSettingsOpen, setTableSettingsOpen] = useState(false);
  const tableSettingsRef = useRef<HTMLDivElement | null>(null);
  const [columnOrder, setColumnOrder] = useState<ClientColumnId[]>(() => {
    try {
      const raw = localStorage.getItem(CLIENT_TABLE_SETTINGS_KEY);
      if (!raw) return CLIENT_COLUMNS.map((col) => col.id);
      const parsed = JSON.parse(raw) as { order?: ClientColumnId[] };
      const fallback = CLIENT_COLUMNS.map((col) => col.id);
      if (!parsed?.order?.length) return fallback;
      const known = new Set(fallback);
      const sanitized = parsed.order.filter((id) => known.has(id));
      return sanitized.length ? [...sanitized, ...fallback.filter((id) => !sanitized.includes(id))] : fallback;
    } catch {
      return CLIENT_COLUMNS.map((col) => col.id);
    }
  });
  const [columnVisibility, setColumnVisibility] = useState<Record<ClientColumnId, boolean>>(() => {
    const defaults = CLIENT_COLUMNS.reduce<Record<ClientColumnId, boolean>>((acc, col) => {
      acc[col.id] = col.defaultVisible;
      return acc;
    }, {} as Record<ClientColumnId, boolean>);
    try {
      const raw = localStorage.getItem(CLIENT_TABLE_SETTINGS_KEY);
      if (!raw) return defaults;
      const parsed = JSON.parse(raw) as { visibility?: Record<ClientColumnId, boolean> };
      if (!parsed?.visibility) return defaults;
      return { ...defaults, ...parsed.visibility };
    } catch {
      return defaults;
    }
  });
  const [columnWidths, setColumnWidths] = useState<Record<ClientColumnId, number>>(() => {
    try {
      const raw = localStorage.getItem(CLIENT_TABLE_SETTINGS_KEY);
      if (!raw) return {} as Record<ClientColumnId, number>;
      const parsed = JSON.parse(raw) as { widths?: Record<ClientColumnId, number> };
      return parsed?.widths ?? ({} as Record<ClientColumnId, number>);
    } catch {
      return {} as Record<ClientColumnId, number>;
    }
  });
  const [draggingColumn, setDraggingColumn] = useState<ClientColumnId | null>(null);
  const [dragOverColumn, setDragOverColumn] = useState<ClientColumnId | null>(null);
  const filteredClients = useMemo(() => {
    const trimmed = query.trim();
    return clients.filter((c) => {
      if (trimmed && !matchesClient(c, trimmed)) return false;
      if (activityFilter === 'active' && !c.isActive) return false;
      if (activityFilter === 'inactive' && c.isActive) return false;
      if (statusFilters.length > 0) {
        const statusId = c.clientStatusId ? String(c.clientStatusId) : CLIENT_STATUS_NONE;
        if (!statusFilters.includes(statusId)) return false;
      }
      return true;
    });
  }, [clients, query, activityFilter, statusFilters]);

  const sortedClients = useMemo(
    () => [...filteredClients].sort((a, b) => Number(b.isActive) - Number(a.isActive)),
    [filteredClients],
  );

  const statusOptions = useMemo(
    () =>
      [...clientStatuses]
        .filter((status) => status.isActive !== false)
        .sort((a, b) => a.name.localeCompare(b.name)),
    [clientStatuses],
  );

  const statusFilterLabel = useMemo(() => {
    if (statusFilters.length === 0) return 'Все статусы';
    const lookup = new Map(statusOptions.map((s) => [String(s.id), s.name]));
    const names = statusFilters.map((id) => (id === CLIENT_STATUS_NONE ? 'Без статуса' : lookup.get(id) ?? id));
    if (names.length <= 2) return names.join(', ');
    return `${names.slice(0, 2).join(', ')} +${names.length - 2}`;
  }, [statusFilters, statusOptions]);

  const visibleColumnIds = useMemo(
    () => columnOrder.filter((id) => columnVisibility[id] !== false),
    [columnOrder, columnVisibility],
  );

  const columnMeta = useMemo(
    () => new Map<ClientColumnId, ClientColumnMeta>(CLIENT_COLUMNS.map((col) => [col.id, col])),
    [],
  );

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

  useEffect(() => {
    const handleClick = (event: MouseEvent) => {
      const target = event.target as Node;
      if (statusFilterOpen && statusFilterRef.current && !statusFilterRef.current.contains(target)) {
        setStatusFilterOpen(false);
      }
      if (tableSettingsOpen && tableSettingsRef.current && !tableSettingsRef.current.contains(target)) {
        setTableSettingsOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [statusFilterOpen, tableSettingsOpen]);

  useEffect(() => {
    try {
      localStorage.setItem(
        CLIENT_TABLE_SETTINGS_KEY,
        JSON.stringify({
          order: columnOrder,
          visibility: columnVisibility,
          widths: columnWidths,
        }),
      );
    } catch {
      /** */
    }
  }, [columnOrder, columnVisibility, columnWidths]);

  const toggleStatusFilter = (statusId: string) => {
    setStatusFilters((prev) => {
      if (prev.includes(statusId)) {
        return prev.filter((id) => id !== statusId);
      }
      return [...prev, statusId];
    });
  };

  const clearStatusFilters = () => setStatusFilters([]);

  const resetColumns = () => {
    setColumnOrder(CLIENT_COLUMNS.map((col) => col.id));
    setColumnVisibility(
      CLIENT_COLUMNS.reduce<Record<ClientColumnId, boolean>>((acc, col) => {
        acc[col.id] = col.defaultVisible;
        return acc;
      }, {} as Record<ClientColumnId, boolean>),
    );
    setColumnWidths({});
  };

  const moveColumn = (columnId: ClientColumnId, direction: 'up' | 'down') => {
    setColumnOrder((prev) => {
      const idx = prev.indexOf(columnId);
      if (idx === -1) return prev;
      const next = [...prev];
      const targetIdx = direction === 'up' ? idx - 1 : idx + 1;
      if (targetIdx < 0 || targetIdx >= next.length) return prev;
      const temp = next[targetIdx];
      next[targetIdx] = columnId;
      next[idx] = temp;
      return next;
    });
  };

  const startResize = (columnId: ClientColumnId, minWidth: number | undefined, event: MouseEvent) => {
    event.preventDefault();
    event.stopPropagation();
    const startX = event.clientX;
    const startWidth = columnWidths[columnId] ?? minWidth ?? 180;

    const onMove = (moveEvent: MouseEvent) => {
      const delta = moveEvent.clientX - startX;
      const nextWidth = Math.max(minWidth ?? 140, startWidth + delta);
      setColumnWidths((prev) => ({ ...prev, [columnId]: nextWidth }));
    };

    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
    };

    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  };

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
    window.alert(message || 'Недостаточно прав для выполнения действия.');
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

  if (loading || legalEntitiesLoading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">{t('loading')}</div>
      </div>
    );
  }

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
    setEditingClient(client);
    setIsModalOpen(true);
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

  const hasAnyClients = clients.length > 0;

  const handleViewModeChange = (mode: 'cards' | 'table') => {
    setViewMode(mode);
    try {
      localStorage.setItem('pp.clients_view', mode);
    } catch {
      /** */
    }
  };

  const renderColumnCell = (columnId: ClientColumnId, client: Client) => {
    switch (columnId) {
      case 'id':
        return <span className="text-sm text-gray-700">#{client.id}</span>;
      case 'name':
        return (
          <div className="flex items-center gap-3 min-w-0">
            <button
              type="button"
              onClick={() => setSelectedClientId(client.id)}
              className="w-10 h-10 bg-blue-100 rounded-lg flex items-center justify-center shrink-0 hover:bg-blue-200 transition-colors"
              title="Открыть карточку клиента"
            >
              <Users size={20} className="text-blue-600" />
            </button>
            <div className="min-w-0">
              <button
                type="button"
                onClick={() => setSelectedClientId(client.id)}
                className="text-left font-medium text-gray-900 hover:text-blue-600 truncate"
                title="Открыть карточку клиента"
              >
                {highlight(client.name ?? '', query)}
              </button>
              {client.company && (
                <div className="text-sm text-gray-500 truncate">
                  {highlight(client.company ?? '', query)}
                </div>
              )}
            </div>
          </div>
        );
      case 'company':
        return <span className="text-sm text-gray-700">{client.company || '—'}</span>;
      case 'email':
        return client.email ? (
          <span className="text-sm text-gray-700">{highlight(client.email ?? '', query)}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'phone':
        return client.phone ? (
          <span className="text-sm text-gray-700">{highlightPhone(client.phone ?? '', query)}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'status':
        return (
          <div className="flex flex-wrap items-center gap-2">
            <ClientStatusBadge status={client.clientStatus} />
            {!client.clientStatus && <span className="text-xs text-gray-400">Без статуса</span>}
          </div>
        );
      case 'clientStatusId':
        return (
          <span className="text-sm text-gray-700">
            {client.clientStatusId ? `#${client.clientStatusId}` : '—'}
          </span>
        );
      case 'legalEntity':
        return client.legalEntity ? (
          <div className="space-y-1 text-sm text-gray-600">
            <div className="flex items-center gap-2 text-gray-700">
              <Building2 size={14} className="text-blue-500" />
              <span className="truncate">{client.legalEntity.shortName ?? '—'}</span>
            </div>
          </div>
        ) : (
          <span className="text-xs text-gray-400">{t('legalEntityNotAssigned') || 'Не указано'}</span>
        );
      case 'legalEntityId':
        return (
          <span className="text-sm text-gray-700">
            {client.legalEntityId ? `#${client.legalEntityId}` : '—'}
          </span>
        );
      case 'legalInn':
        return <span className="text-sm text-gray-700">{client.legalEntity?.inn || '—'}</span>;
      case 'legalOgrn':
        return <span className="text-sm text-gray-700">{client.legalEntity?.ogrn || '—'}</span>;
      case 'legalKpp':
        return <span className="text-sm text-gray-700">{client.legalEntity?.kpp || '—'}</span>;
      case 'address':
        return client.address ? (
          <span className="text-sm text-gray-700">{client.address}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'notes':
        return client.notes ? (
          <span className="text-sm text-gray-700 line-clamp-2">{client.notes}</span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        );
      case 'isActive':
        return (
          <span
            className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${
              client.isActive ? 'bg-emerald-100 text-emerald-700' : 'bg-gray-200 text-gray-600'
            }`}
          >
            {client.isActive ? 'Активен' : 'Неактивен'}
          </span>
        );
      case 'createdAt':
        return <span className="text-sm text-gray-600">{formatDate(client.createdAt) || '—'}</span>;
      case 'cases': {
        const totalCases = (client.cases ?? []).length;
        return totalCases > 0 ? (
          <button
            type="button"
            onClick={() => {
              setSelectedClientId(client.id);
              setInitialCaseId('all');
            }}
            className="text-sm text-blue-600 hover:text-blue-700 hover:underline"
            title="Посмотреть дела"
          >
            {totalCases} {totalCases === 1 ? 'дело' : totalCases < 5 ? 'дела' : 'дел'}
          </button>
        ) : (
          <span className="text-xs text-gray-400">Нет</span>
        );
      }
      case 'actions':
        return (
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
                title={t('edit')}
              >
                <Edit size={16} />
              </button>
            )}
            <button
              type="button"
              onClick={() => setSelectedClientId(client.id)}
              className="p-2 text-gray-400 hover:text-gray-600 hover:bg-gray-100 rounded-lg transition-colors"
              title={t('viewDetails')}
            >
              <Eye size={16} />
            </button>
          </div>
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
              <div className="relative" ref={statusFilterRef}>
                <button
                  type="button"
                  onClick={() => setStatusFilterOpen((prev) => !prev)}
                  className="inline-flex w-full sm:w-auto items-center justify-between gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
                  aria-expanded={statusFilterOpen}
                >
                  <span className="truncate">Статус: {statusFilterLabel}</span>
                  <ChevronDown size={16} className="text-gray-500" />
                </button>
                {statusFilterOpen && (
                  <div className="absolute right-0 z-20 mt-2 w-64 rounded-lg border border-gray-200 bg-white shadow-lg p-3">
                    <div className="flex items-center justify-between mb-2">
                      <span className="text-xs font-semibold text-gray-500 uppercase">Статусы</span>
                      {statusFilters.length > 0 && (
                        <button
                          type="button"
                          onClick={clearStatusFilters}
                          className="text-xs text-blue-600 hover:text-blue-700"
                        >
                          Сбросить
                        </button>
                      )}
                    </div>
                    <div className="max-h-56 overflow-y-auto space-y-1 pr-1">
                      <label className="flex items-center gap-2 text-sm text-gray-700">
                        <input
                          type="checkbox"
                          checked={statusFilters.includes(CLIENT_STATUS_NONE)}
                          onChange={() => toggleStatusFilter(CLIENT_STATUS_NONE)}
                          className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                        />
                        Без статуса
                      </label>
                      {statusOptions.map((status) => (
                        <label key={status.id} className="flex items-center gap-2 text-sm text-gray-700">
                          <input
                            type="checkbox"
                            checked={statusFilters.includes(String(status.id))}
                            onChange={() => toggleStatusFilter(String(status.id))}
                            className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                          />
                          {status.name}
                        </label>
                      ))}
                    </div>
                  </div>
                )}
              </div>
              <select
                value={activityFilter}
                onChange={(e) => setActivityFilter(e.target.value as 'all' | 'active' | 'inactive')}
                className="w-full sm:w-auto rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700"
              >
                <option value="all">Все клиенты</option>
                <option value="active">Активные</option>
                <option value="inactive">Неактивные</option>
              </select>
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
                <div className="relative" ref={tableSettingsRef}>
                  <button
                    type="button"
                    onClick={() => setTableSettingsOpen((prev) => !prev)}
                    className="inline-flex w-full sm:w-auto items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
                  >
                    <Columns size={16} />
                    Настроить таблицу
                  </button>
                  {tableSettingsOpen && (
                    <div className="absolute right-0 z-20 mt-2 w-72 rounded-lg border border-gray-200 bg-white shadow-lg p-3">
                      <div className="flex items-center justify-between mb-2">
                        <span className="text-xs font-semibold text-gray-500 uppercase">Колонки</span>
                        <button
                          type="button"
                          onClick={resetColumns}
                          className="text-xs text-blue-600 hover:text-blue-700"
                        >
                          Сбросить
                        </button>
                      </div>
                      <div className="max-h-64 overflow-y-auto space-y-2 pr-1">
                        {columnOrder.map((columnId) => {
                          const meta = columnMeta.get(columnId);
                          if (!meta) return null;
                          const isVisible = columnVisibility[columnId] !== false;
                          return (
                            <div key={columnId} className="flex items-center gap-2">
                              <GripVertical size={14} className="text-gray-400 shrink-0" />
                              <label className="flex items-center gap-2 text-sm text-gray-700 flex-1">
                                <input
                                  type="checkbox"
                                  checked={isVisible}
                                  onChange={() => {
                                    const visibleCount = visibleColumnIds.length;
                                    if (isVisible && visibleCount === 1) return;
                                    setColumnVisibility((prev) => ({ ...prev, [columnId]: !isVisible }));
                                  }}
                                  className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                />
                                {meta.label}
                              </label>
                              <div className="flex items-center gap-1">
                                <button
                                  type="button"
                                  onClick={() => moveColumn(columnId, 'up')}
                                  className="p-1 rounded hover:bg-gray-100"
                                  title="Вверх"
                                >
                                  <ChevronUp size={14} />
                                </button>
                                <button
                                  type="button"
                                  onClick={() => moveColumn(columnId, 'down')}
                                  className="p-1 rounded hover:bg-gray-100"
                                  title="Вниз"
                                >
                                  <ChevronDown size={14} />
                                </button>
                              </div>
                            </div>
                          );
                        })}
                      </div>
                      <p className="mt-2 text-[11px] text-gray-400">
                        Перетаскивайте заголовки таблицы для быстрой перестановки и тяните за край
                        для изменения ширины.
                      </p>
                    </div>
                  )}
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

        {!hasAnyClients ? (
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
              <table className="w-full">
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    {visibleColumnIds.map((columnId) => {
                      const meta = columnMeta.get(columnId);
                      if (!meta) return null;
                      const width = columnWidths[columnId];
                      const isRight = meta.align === 'right';
                      const isDragOver = dragOverColumn === columnId && draggingColumn !== columnId;
                      return (
                        <th
                          key={columnId}
                          draggable
                          onDragStart={(e) => {
                            setDraggingColumn(columnId);
                            e.dataTransfer.effectAllowed = 'move';
                            e.dataTransfer.setData('text/plain', columnId);
                          }}
                          onDragOver={(e) => {
                            e.preventDefault();
                            setDragOverColumn(columnId);
                          }}
                          onDragLeave={() => setDragOverColumn(null)}
                          onDrop={(e) => {
                            e.preventDefault();
                            const sourceId =
                              draggingColumn ?? (e.dataTransfer.getData('text/plain') as ClientColumnId);
                            if (!sourceId || sourceId === columnId) {
                              setDraggingColumn(null);
                              setDragOverColumn(null);
                              return;
                            }
                            setColumnOrder((prev) => {
                              const next = [...prev];
                              const from = next.indexOf(sourceId);
                              const to = next.indexOf(columnId);
                              if (from === -1 || to === -1) return prev;
                              next.splice(from, 1);
                              next.splice(to, 0, sourceId);
                              return next;
                            });
                            setDraggingColumn(null);
                            setDragOverColumn(null);
                          }}
                          onDragEnd={() => {
                            setDraggingColumn(null);
                            setDragOverColumn(null);
                          }}
                          style={{
                            width: width ? `${width}px` : undefined,
                            minWidth: meta.minWidth ? `${meta.minWidth}px` : undefined,
                          }}
                          className={`relative px-6 py-3 text-xs font-semibold uppercase tracking-wider text-gray-700 ${
                            isRight ? 'text-right' : 'text-left'
                          } ${isDragOver ? 'bg-emerald-50' : ''}`}
                        >
                          <div className={`flex items-center gap-2 ${isRight ? 'justify-end' : ''}`}>
                            <span>{meta.label}</span>
                          </div>
                          <div
                            role="separator"
                            onMouseDown={(event) => startResize(columnId, meta.minWidth, event)}
                            className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                          />
                        </th>
                      );
                    })}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {sortedClients.map((client) => {
                    return (
                      <tr
                        key={client.id}
                        className={`hover:bg-gray-50 transition-colors ${
                          client.isActive ? '' : 'bg-gray-50 opacity-70'
                        }`}>
                        {visibleColumnIds.map((columnId) => {
                          const meta = columnMeta.get(columnId);
                          if (!meta) return null;
                          const isRight = meta.align === 'right';
                          return (
                            <td
                              key={columnId}
                              className={`px-6 py-4 align-top ${isRight ? 'text-right' : 'text-left'}`}
                            >
                              {renderColumnCell(columnId, client)}
                            </td>
                          );
                        })}
                      </tr>
                    );
                  })}
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
                        onClick={() => setSelectedClientId(client.id)}
                        className="w-12 h-12 bg-blue-100 rounded-lg flex items-center justify-center shrink-0 hover:bg-blue-200 transition-colors"
                        title="Открыть карточку клиента"
                      >
                        <Users size={24} className="text-blue-600" />
                      </button>
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <button
                            type="button"
                            onClick={() => setSelectedClientId(client.id)}
                            className="font-semibold text-gray-900 block max-w-full truncate hover:text-blue-600 text-left"
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
                          title={t('edit')}>
                          <Edit size={16} />
                        </button>
                      )}
                      <button
                        type="button"
                        onClick={() => setSelectedClientId(client.id)}
                        className="p-2 text-gray-400 hover:text-gray-600 hover:bg-gray-100 rounded-lg transition-colors"
                        title={t('viewDetails')}>
                        <Eye size={16} />
                      </button>
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
                  <div className="mt-4 pt-4 border-t border-gray-100">
                    <button
                      type="button"
                      onClick={() => setSelectedClientId(client.id)}
                      className="w-full text-center text-blue-600 hover:text-blue-700 font-medium text-sm transition-colors">
                      {t('viewDetails')}
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        <ClientModal
          isOpen={isModalOpen}
          onClose={handleCloseModal}
          onSubmit={handleSubmitClient}
          onDelete={handleDeleteClient}
          client={editingClient ?? undefined}
          legalEntities={legalEntities}
          clientStatuses={clientStatuses}
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
