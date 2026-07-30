import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { useEffect, useMemo, useRef, useState, type MouseEvent } from 'react';
import { Archive, ArrowUpDown, Bookmark, Briefcase, ClipboardPlus, Download, Loader2, LockKeyhole, Pencil, Plus, Printer, RefreshCw, Search, Settings2, ShieldAlert, Trash2, UserRound, WalletCards } from 'lucide-react';

import { useAuth } from '../../contexts/AuthContext';
import { apiService } from '../../services/api';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { useDialog } from '../../hooks/useDialog';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import type { ActResponsible, ClientCase, ClientCaseAccessRequest } from '../../types';
import { buildCaseTaskPayload } from '../../utils/caseTaskPayload';
import { toRuDate } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { CaseModal } from '../Clients/CaseModal';
import { CounterpartyPickerModal, type CounterpartyKind, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
import { TableColumnSettings } from '../ui/TableColumnSettings';

type SortKey =
  | 'createdAt'
  | 'caseNumber'
  | 'title'
  | 'client'
  | 'caseType'
  | 'stage'
  | 'status'
  | 'responsible'
  | 'startDate'
  | 'dueDate'
  | 'feeAmount'
  | 'priority'
  | 'court'
  | 'nextHearingAt'
  | 'lastViewedAt';

type SortDir = 'asc' | 'desc';
type CaseScope = 'all' | 'active' | 'mine' | 'control' | 'archive';
type CaseActivityFilter = 'all' | 'active' | 'inactive';
type CaseRegistryViewState = {
  search: string;
  status: string;
  caseScope: CaseScope;
  activityFilter: CaseActivityFilter;
  caseType: string;
  responsibleUserId: string;
  priority: string;
  from: string;
  to: string;
  minFee: string;
  maxFee: string;
  sortBy: SortKey;
  sortDir: SortDir;
  pageSize: number;
};
type CaseRegistrySavedView = {
  id: string;
  name: string;
  filters: CaseRegistryViewState;
  updatedAt: string;
};
type CaseContextMenuState = {
  item: ClientCase;
  x: number;
  y: number;
};
type CaseColumnKey =
  | 'caseNumber'
  | 'title'
  | 'client'
  | 'responsible'
  | 'status'
  | 'caseType'
  | 'stage'
  | 'createdAt'
  | 'startDate'
  | 'dueDate'
  | 'lastViewedAt'
  | 'feeAmount'
  | 'priority'
  | 'court'
  | 'nextHearingAt'
  | 'incomeTotal'
  | 'overdueTaskCount'
  | 'progress'
  | 'activity'
  | 'incomePaid'
  | 'incomeOutstanding'
  | 'actions';

interface CasesRegistryProps {
  onOpenCase?: (caseId: number) => void;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenLegalEntity?: (legalEntityId: number, caseId?: number) => void;
  onOpenCaseFinance?: (caseItem: ClientCase) => void;
  allowCreate?: boolean;
}

const STATUS_OPTIONS = [
  { value: '', label: 'Все статусы' },
  { value: 'Open', label: 'В работе' },
  { value: 'OnHold', label: 'Приостановлено' },
  { value: 'Closed', label: 'Закрыто' },
  { value: 'Lost', label: 'Проиграно' },
];

const PRIORITY_OPTIONS = [
  { value: '', label: 'Любой приоритет' },
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Критический' },
];

const CASE_SCOPE_OPTIONS: Array<{ value: CaseScope; label: string }> = [
  { value: 'all', label: 'Все' },
  { value: 'active', label: 'Активные' },
  { value: 'mine', label: 'Мои' },
  { value: 'control', label: 'На контроле' },
  { value: 'archive', label: 'Архив' },
];
const CASE_ACTIVITY_FILTER_OPTIONS: Array<{ value: CaseActivityFilter; label: string }> = [
  { value: 'all', label: 'Любая активность' },
  { value: 'active', label: 'Активные по движению' },
  { value: 'inactive', label: 'Неактивные' },
];
const CASE_REGISTRY_VIEW_STORAGE_KEY = 'pp.cases_registry_views.v1';
const INACTIVE_CASE_DAYS = 14;
const DAY_MS = 24 * 60 * 60 * 1000;

const CASE_COLUMN_CONFIGS: Array<{
  key: CaseColumnKey;
  label: string;
  sortKey?: SortKey;
  defaultWidth: number;
}> = [
  { key: 'caseNumber', label: '№ дела', sortKey: 'caseNumber', defaultWidth: 120 },
  { key: 'title', label: 'Название', sortKey: 'title', defaultWidth: 260 },
  { key: 'client', label: 'Клиент', sortKey: 'client', defaultWidth: 220 },
  { key: 'responsible', label: 'Ответственный', sortKey: 'responsible', defaultWidth: 220 },
  { key: 'status', label: 'Статус', sortKey: 'status', defaultWidth: 130 },
  { key: 'caseType', label: 'Тип', sortKey: 'caseType', defaultWidth: 160 },
  { key: 'stage', label: 'Этап', sortKey: 'stage', defaultWidth: 190 },
  { key: 'createdAt', label: 'Создано', sortKey: 'createdAt', defaultWidth: 130 },
  { key: 'startDate', label: 'Начало', sortKey: 'startDate', defaultWidth: 130 },
  { key: 'dueDate', label: 'Срок', sortKey: 'dueDate', defaultWidth: 130 },
  { key: 'lastViewedAt', label: 'Просмотр ответственным', sortKey: 'lastViewedAt', defaultWidth: 210 },
  { key: 'feeAmount', label: 'Гонорар', sortKey: 'feeAmount', defaultWidth: 140 },
  { key: 'priority', label: 'Приоритет', sortKey: 'priority', defaultWidth: 140 },
  { key: 'court', label: 'Суд', sortKey: 'court', defaultWidth: 240 },
  { key: 'nextHearingAt', label: 'Заседание', sortKey: 'nextHearingAt', defaultWidth: 140 },
  { key: 'incomeTotal', label: 'Счета', defaultWidth: 130 },
  { key: 'overdueTaskCount', label: 'Просрочено', defaultWidth: 130 },
  { key: 'progress', label: 'Прогресс', defaultWidth: 140 },
  { key: 'activity', label: 'Автоконтроль', defaultWidth: 260 },
  { key: 'incomePaid', label: 'Оплачено', defaultWidth: 130 },
  { key: 'incomeOutstanding', label: 'Долг', defaultWidth: 130 },
  { key: 'actions', label: 'Действия', defaultWidth: 150 },
];

const FINANCIAL_CASE_COLUMNS = new Set<CaseColumnKey>(['feeAmount', 'incomeTotal', 'incomePaid', 'incomeOutstanding']);

const STATUS_LABELS: Record<string, string> = {
  Open: 'В работе',
  OnHold: 'Приостановлено',
  Closed: 'Закрыто',
  Lost: 'Проиграно',
};

const PRIORITY_LABELS: Record<string, string> = {
  low: 'Низкий',
  normal: 'Обычный',
  high: 'Высокий',
  urgent: 'Критический',
};

function getOwnerName(item: ClientCase) {
  return item.client?.name || item.legalEntity?.shortName || item.legalEntity?.fullName || '—';
}

function getResponsibleName(item: ClientCase, responsibles: ActResponsible[]) {
  return (
    item.responsibleUser?.fullName ||
    item.responsibleUserName ||
    responsibles.find((responsible) => responsible.id === item.responsibleUserId)?.fullName ||
    'Ответственный владельца'
  );
}

function getResponsibleLastViewedAt(item: ClientCase) {
  return item.lastViewedByResponsibleAt ?? item.lastViewedAt ?? null;
}

function getResponsibleLastViewedByName(item: ClientCase) {
  return item.lastViewedByResponsibleUserName ?? item.lastViewedByUserName ?? null;
}

function getStatusClass(status?: string | null) {
  if (status === 'Closed') return 'bg-emerald-100 text-emerald-700 border-emerald-200';
  if (status === 'Lost') return 'bg-rose-100 text-rose-700 border-rose-200';
  if (status === 'OnHold') return 'bg-amber-100 text-amber-700 border-amber-200';
  return 'bg-blue-100 text-blue-700 border-blue-200';
}

function getPriorityClass(priority?: string | null) {
  if (priority === 'urgent') return 'bg-rose-100 text-rose-700 border-rose-200';
  if (priority === 'high') return 'bg-orange-100 text-orange-700 border-orange-200';
  if (priority === 'low') return 'bg-slate-100 text-slate-600 border-slate-200';
  return 'bg-indigo-100 text-indigo-700 border-indigo-200';
}

function csvCell(value: unknown) {
  const text = value === null || value === undefined ? '' : String(value);
  return `"${text.replace(/"/g, '""')}"`;
}

function formatMoney(value?: number | null) {
  return typeof value === 'number' ? formatCurrencySmart(value).full : '—';
}

function formatProfileDateTime(value: string | null | undefined, timeZoneId: string) {
  return value ? formatDateTimeInTimeZone(value, timeZoneId) : '';
}

function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function getLatestCaseActivityAt(item: ClientCase) {
  if (item.lastMeaningfulActivityAt) {
    return item.lastMeaningfulActivityAt;
  }

  const values = [item.lastTaskAt, item.activityConfirmedAt, getResponsibleLastViewedAt(item)]
    .map((value) => {
      if (!value) return null;
      const date = new Date(value);
      return Number.isNaN(date.getTime()) ? null : date;
    })
    .filter((value): value is Date => Boolean(value));

  if (values.length === 0) {
    return null;
  }

  return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString();
}

function getCaseActivityAgeDays(item: ClientCase) {
  const latestActivity = getLatestCaseActivityAt(item) || item.createdAt;
  const date = new Date(latestActivity);
  if (Number.isNaN(date.getTime())) {
    return null;
  }

  return Math.max(0, Math.floor((Date.now() - date.getTime()) / DAY_MS));
}

function getCaseActivityBadge(item: ClientCase) {
  if (item.status === 'Closed' || item.status === 'Lost') {
    return {
      label: 'Архив',
      className: 'border-slate-200 bg-slate-100 text-slate-600',
      hint: 'Дело завершено',
    };
  }

  if (item.inactivityRemindersEnabled === false) {
    return {
      label: 'Контроль выключен',
      className: 'border-slate-200 bg-slate-100 text-slate-600',
      hint: 'Индивидуальное исключение',
    };
  }

  if (item.inactivityControlExclusionReason) {
    return {
      label: 'Исключено',
      className: 'border-slate-200 bg-slate-100 text-slate-600',
      hint: 'Контроль временно не выполняется',
    };
  }

  const days = getCaseActivityAgeDays(item);
  if (item.nextInactivityControlAt) {
    const nextControlAt = new Date(item.nextInactivityControlAt);
    const isDue = !Number.isNaN(nextControlAt.getTime()) && nextControlAt.getTime() <= Date.now();
    return {
      label: days === null ? 'Автоконтроль' : `Нет движения ${days} дн.`,
      className: isDue
        ? 'border-amber-200 bg-amber-50 text-amber-700'
        : 'border-blue-200 bg-blue-50 text-blue-700',
      hint: isDue ? 'Срок контроля наступил' : 'Проверка запланирована',
    };
  }

  if (days !== null && days >= INACTIVE_CASE_DAYS) {
    return {
      label: `Нет движения ${days} дн.`,
      className: 'border-amber-200 bg-amber-50 text-amber-700',
      hint: 'Дело попадает под автоконтроль',
    };
  }

  return {
    label: 'Активно',
    className: 'border-emerald-200 bg-emerald-50 text-emerald-700',
    hint: days === null ? 'Активность не рассчитана' : `Последнее движение ${days} дн. назад`,
  };
}

function loadSavedRegistryViews(): CaseRegistrySavedView[] {
  if (typeof window === 'undefined') {
    return [];
  }

  try {
    const raw = window.localStorage.getItem(CASE_REGISTRY_VIEW_STORAGE_KEY);
    if (!raw) {
      return [];
    }

    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .filter((item): item is CaseRegistrySavedView => (
        Boolean(item)
        && typeof item === 'object'
        && typeof (item as CaseRegistrySavedView).id === 'string'
        && typeof (item as CaseRegistrySavedView).name === 'string'
        && Boolean((item as CaseRegistrySavedView).filters)
      ))
      .slice(0, 20);
  } catch {
    return [];
  }
}

function persistSavedRegistryViews(views: CaseRegistrySavedView[]) {
  if (typeof window === 'undefined') {
    return;
  }

  window.localStorage.setItem(CASE_REGISTRY_VIEW_STORAGE_KEY, JSON.stringify(views.slice(0, 20)));
}

function buildCaseUpdatePayload(base: ClientCase, patch: Partial<ClientCase>): Omit<ClientCase, 'id' | 'createdAt' | 'payments'> {
  return {
    clientId: patch.clientId ?? base.clientId ?? null,
    legalEntityId: patch.legalEntityId ?? base.legalEntityId ?? null,
    client: patch.client ?? base.client ?? null,
    legalEntity: patch.legalEntity ?? base.legalEntity ?? null,
    title: patch.title ?? base.title ?? '',
    description: patch.description ?? base.description ?? '',
    status: patch.status ?? base.status ?? 'Open',
    caseNumber: patch.caseNumber ?? base.caseNumber ?? '',
    caseType: patch.caseType ?? base.caseType ?? '',
    stage: patch.stage ?? base.stage ?? '',
    responsibleUserId: patch.responsibleUserId ?? base.responsibleUserId ?? null,
    startDate: patch.startDate ?? base.startDate ?? null,
    dueDate: patch.dueDate ?? base.dueDate ?? null,
    feeAmount: patch.feeAmount ?? base.feeAmount ?? 0,
    priority: patch.priority ?? base.priority ?? 'normal',
    courtName: patch.courtName ?? base.courtName ?? '',
    courtCaseNumber: patch.courtCaseNumber ?? base.courtCaseNumber ?? '',
    courtUrl: patch.courtUrl ?? base.courtUrl ?? '',
    nextHearingAt: patch.nextHearingAt ?? base.nextHearingAt ?? null,
    claimant: patch.claimant ?? base.claimant ?? '',
    defendant: patch.defendant ?? base.defendant ?? '',
    thirdParties: patch.thirdParties ?? base.thirdParties ?? '',
    representatives: patch.representatives ?? base.representatives ?? '',
    judgeName: patch.judgeName ?? base.judgeName ?? '',
    courtroom: patch.courtroom ?? base.courtroom ?? '',
    courtSecretaryContacts: patch.courtSecretaryContacts ?? base.courtSecretaryContacts ?? '',
    claimFiledAt: patch.claimFiledAt ?? base.claimFiledAt ?? null,
    courtDecisionAt: patch.courtDecisionAt ?? base.courtDecisionAt ?? null,
    appealDeadlineAt: patch.appealDeadlineAt ?? base.appealDeadlineAt ?? null,
    cassationDeadlineAt: patch.cassationDeadlineAt ?? base.cassationDeadlineAt ?? null,
    motionDeadlineAt: patch.motionDeadlineAt ?? base.motionDeadlineAt ?? null,
    objectionDeadlineAt: patch.objectionDeadlineAt ?? base.objectionDeadlineAt ?? null,
    documentsSummary: patch.documentsSummary ?? base.documentsSummary ?? '',
    eventHistory: patch.eventHistory ?? base.eventHistory ?? '',
    strategy: patch.strategy ?? base.strategy ?? '',
    inactivityRemindersEnabled: patch.inactivityRemindersEnabled ?? base.inactivityRemindersEnabled ?? true,
    isUnderControl: patch.isUnderControl ?? base.isUnderControl ?? false,
    isHiddenFromExecutor: patch.isHiddenFromExecutor ?? base.isHiddenFromExecutor ?? false,
  } as Omit<ClientCase, 'id' | 'createdAt' | 'payments'>;
}

export function CasesRegistry({ onOpenCase, onOpenClient, onOpenLegalEntity, onOpenCaseFinance, allowCreate = true }: CasesRegistryProps) {
  const { user, isAdmin } = useAuth();
  const { prompt } = useDialog();
  const permissions = useRolePermissions(user?.role?.id);
  const canEditCases = isAdmin() || permissions.cases.canEdit;
  const canExportCases = isAdmin() || permissions.cases.canExport;
  const canViewFinance = isAdmin() || permissions.payments.canView;
  const canViewClients = isAdmin() || permissions.clients.canView;
  const canViewLegalEntities = isAdmin() || permissions.legalEntities.canView;
  const canManageCaseAccess = canEditCases;
  const allowedOwnerScopes = useMemo<CounterpartyKind[]>(() => {
    const scopes: CounterpartyKind[] = [];
    if (canViewClients) scopes.push('client');
    if (canViewLegalEntities) scopes.push('legalEntity');
    return scopes;
  }, [canViewClients, canViewLegalEntities]);
  const canSelectCaseOwner = allowedOwnerScopes.length > 0;
  const [items, setItems] = useState<ClientCase[]>([]);
  const [accessRequests, setAccessRequests] = useState<ClientCaseAccessRequest[]>([]);
  const [accessRequestsLoading, setAccessRequestsLoading] = useState(false);
  const [resolvingAccessCaseId, setResolvingAccessCaseId] = useState<number | null>(null);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [registryExporting, setRegistryExporting] = useState(false);
  const [registryExcelExporting, setRegistryExcelExporting] = useState(false);
  const [registryPdfExporting, setRegistryPdfExporting] = useState(false);
  const [registryPrinting, setRegistryPrinting] = useState(false);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 300);
  const previousRegistrySearchRef = useRef(debouncedSearch);
  const [status, setStatus] = useState('');
  const [caseScope, setCaseScope] = useState<CaseScope>('all');
  const [activityFilter, setActivityFilter] = useState<CaseActivityFilter>('all');
  const [caseType, setCaseType] = useState('');
  const [responsibleUserId, setResponsibleUserId] = useState('');
  const [priority, setPriority] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [minFee, setMinFee] = useState('');
  const [maxFee, setMaxFee] = useState('');
  const [sortBy, setSortBy] = useState<SortKey>('createdAt');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const [savedViews, setSavedViews] = useState<CaseRegistrySavedView[]>(() => loadSavedRegistryViews());
  const [selectedSavedViewId, setSelectedSavedViewId] = useState('');
  const [reloadToken, setReloadToken] = useState(0);
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [bulkStatus, setBulkStatus] = useState('');
  const [bulkResponsibleUserId, setBulkResponsibleUserId] = useState('');
  const [bulkStage, setBulkStage] = useState('');
  const [bulkPriority, setBulkPriority] = useState('');
  const [bulkControlState, setBulkControlState] = useState<'keep' | 'on' | 'off'>('keep');
  const [bulkSaving, setBulkSaving] = useState(false);
  const [creatingTaskCaseId, setCreatingTaskCaseId] = useState<number | null>(null);
  const [controlCaseId, setControlCaseId] = useState<number | null>(null);
  const [courtSyncCaseId, setCourtSyncCaseId] = useState<number | null>(null);
  const timeZoneId = useUserTimeZone();
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [mobileFiltersOpen, setMobileFiltersOpen] = useState(false);
  const [ownerPickerOpen, setOwnerPickerOpen] = useState(false);
  const [caseDraft, setCaseDraft] = useState<ClientCase | null>(null);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseContextMenu, setCaseContextMenu] = useState<CaseContextMenuState | null>(null);
  const caseColumnConfigs = useMemo(
    () => CASE_COLUMN_CONFIGS.filter((column) => canViewFinance || !FINANCIAL_CASE_COLUMNS.has(column.key)),
    [canViewFinance],
  );
  const {
    visibleColumns,
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
  } = usePersistedTableSettings<CaseColumnKey>({
    storageKey: 'pp.cases_registry_table_settings.v1',
    settingsKey: 'cases_registry_table_settings_v1',
    columns: caseColumnConfigs.map((column) => ({
      key: column.key,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 25,
    maxPageSize: 200,
  });

  useEffect(() => {
    if (canViewFinance) {
      return;
    }

    setMinFee('');
    setMaxFee('');
    setSortBy((current) => current === 'feeAmount' ? 'createdAt' : current);
  }, [canViewFinance]);

  useEffect(() => {
    let cancelled = false;
    void apiService.getActResponsibles()
      .then((result) => {
        if (!cancelled) {
          setResponsibles(result ?? []);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setResponsibles([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [reloadToken]);

  useEffect(() => {
    if (!canManageCaseAccess) {
      setAccessRequests([]);
      setAccessRequestsLoading(false);
      return undefined;
    }

    let cancelled = false;
    const controller = new AbortController();
    setAccessRequestsLoading(true);
    void apiService.getCaseAccessRequests({ signal: controller.signal })
      .then((result) => {
        if (!cancelled) {
          setAccessRequests(result ?? []);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setAccessRequests([]);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setAccessRequestsLoading(false);
        }
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [canManageCaseAccess, reloadToken]);

  useEffect(() => {
    if (page !== 1 && previousRegistrySearchRef.current !== debouncedSearch) {
      return undefined;
    }
    previousRegistrySearchRef.current = debouncedSearch;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    void apiService.getCasesV2({
      search: debouncedSearch || undefined,
      status: status || undefined,
      caseType: caseType.trim() || undefined,
      responsibleUserId: responsibleUserId ? Number(responsibleUserId) : undefined,
      priority: priority || undefined,
      from: from || undefined,
      to: to || undefined,
      minFee: canViewFinance && minFee ? Number(minFee) : undefined,
      maxFee: canViewFinance && maxFee ? Number(maxFee) : undefined,
      scope: caseScope === 'all' ? undefined : caseScope,
      activity: activityFilter === 'all' ? undefined : activityFilter,
      inactiveDays: INACTIVE_CASE_DAYS,
      sortBy,
      sortDir,
      page,
      pageSize,
    }, { signal: controller.signal })
      .then((casesResult) => {
        if (controller.signal.aborted) return;
        setItems(casesResult.items ?? []);
        setTotal(casesResult.total ?? 0);
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        setItems([]);
        setTotal(0);
        setError(err instanceof Error ? err.message : 'Не удалось загрузить реестр дел.');
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [activityFilter, canViewFinance, caseScope, caseType, debouncedSearch, from, maxFee, minFee, page, pageSize, priority, reloadToken, responsibleUserId, sortBy, sortDir, status, to]);

  useEffect(() => {
    setPage(1);
  }, [activityFilter, caseScope, caseType, debouncedSearch, from, maxFee, minFee, pageSize, priority, responsibleUserId, status, to]);

  useEffect(() => {
    setSelectedIds((current) => current.filter((id) => items.some((item) => item.id === id)));
  }, [items]);

  useEffect(() => {
    if (!caseContextMenu) return undefined;

    const closeMenu = () => setCaseContextMenu(null);
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        closeMenu();
      }
    };

    window.addEventListener('click', closeMenu);
    window.addEventListener('scroll', closeMenu, true);
    window.addEventListener('keydown', handleKeyDown);

    return () => {
      window.removeEventListener('click', closeMenu);
      window.removeEventListener('scroll', closeMenu, true);
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [caseContextMenu]);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  const typeOptions = useMemo(() => {
    const values = Array.from(new Set(items.map((item) => item.caseType?.trim()).filter(Boolean) as string[]));
    return values.sort((a, b) => a.localeCompare(b, 'ru'));
  }, [items]);

  const caseColumnsByKey = useMemo(
    () => Object.fromEntries(caseColumnConfigs.map((column) => [column.key, column])) as Partial<Record<CaseColumnKey, (typeof CASE_COLUMN_CONFIGS)[number]>>,
    [caseColumnConfigs],
  );
  const visibleCaseColumns = useMemo(
    () => orderedVisibleColumns
      .map((key) => caseColumnsByKey[key])
      .filter((column): column is (typeof CASE_COLUMN_CONFIGS)[number] => Boolean(column)),
    [caseColumnsByKey, orderedVisibleColumns],
  );

  const changeSort = (nextSortBy: SortKey) => {
    if (sortBy === nextSortBy) {
      setSortDir((current) => (current === 'asc' ? 'desc' : 'asc'));
      return;
    }

    setSortBy(nextSortBy);
    setSortDir('asc');
  };

  const changeCaseScope = (nextScope: CaseScope) => {
    setCaseScope(nextScope);
    if (nextScope === 'active' || nextScope === 'archive') {
      setStatus('');
    }
  };

  const captureRegistryViewState = (): CaseRegistryViewState => ({
    search,
    status,
    caseScope,
    activityFilter,
    caseType,
    responsibleUserId,
    priority,
    from,
    to,
    minFee,
    maxFee,
    sortBy,
    sortDir,
    pageSize,
  });

  const applyRegistryViewState = (filters: CaseRegistryViewState) => {
    setSearch(filters.search ?? '');
    setStatus(filters.status ?? '');
    setCaseScope(filters.caseScope ?? 'all');
    setActivityFilter(filters.activityFilter ?? 'all');
    setCaseType(filters.caseType ?? '');
    setResponsibleUserId(filters.responsibleUserId ?? '');
    setPriority(filters.priority ?? '');
    setFrom(filters.from ?? '');
    setTo(filters.to ?? '');
    setMinFee(filters.minFee ?? '');
    setMaxFee(filters.maxFee ?? '');
    setSortBy(filters.sortBy ?? 'createdAt');
    setSortDir(filters.sortDir ?? 'desc');
    setPageSize(filters.pageSize || 25);
    setPage(1);
  };

  const saveCurrentView = async () => {
    const currentView = savedViews.find((view) => view.id === selectedSavedViewId);
    const name = (await prompt({
      title: 'Сохранить представление',
      label: 'Название представления',
      defaultValue: currentView?.name ?? '',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    }))?.trim();
    if (!name) {
      return;
    }

    const viewId = currentView?.id ?? `view-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const nextView: CaseRegistrySavedView = {
      id: viewId,
      name,
      filters: captureRegistryViewState(),
      updatedAt: new Date().toISOString(),
    };
    const nextViews = [nextView, ...savedViews.filter((view) => view.id !== viewId)].slice(0, 20);
    setSavedViews(nextViews);
    setSelectedSavedViewId(viewId);
    persistSavedRegistryViews(nextViews);
    setNotice(`Представление «${name}» сохранено.`);
  };

  const selectSavedView = (viewId: string) => {
    setSelectedSavedViewId(viewId);
    const view = savedViews.find((item) => item.id === viewId);
    if (!view) {
      return;
    }

    applyRegistryViewState(view.filters);
  };

  const deleteSelectedView = () => {
    if (!selectedSavedViewId) {
      return;
    }

    const view = savedViews.find((item) => item.id === selectedSavedViewId);
    const nextViews = savedViews.filter((item) => item.id !== selectedSavedViewId);
    setSavedViews(nextViews);
    setSelectedSavedViewId('');
    persistSavedRegistryViews(nextViews);
    setNotice(view ? `Представление «${view.name}» удалено.` : 'Представление удалено.');
  };

  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds]);
  const selectedItems = useMemo(
    () => items.filter((item) => selectedIdSet.has(item.id)),
    [items, selectedIdSet],
  );
  const selectedSummary = useMemo(() => selectedItems.reduce(
    (acc, item) => ({
      feeAmount: acc.feeAmount + (item.feeAmount ?? 0),
      incomePaid: acc.incomePaid + (item.incomePaid ?? 0),
      incomeOutstanding: acc.incomeOutstanding + (item.incomeOutstanding ?? 0),
    }),
    { feeAmount: 0, incomePaid: 0, incomeOutstanding: 0 },
  ), [selectedItems]);
  const allPageSelected = items.length > 0 && items.every((item) => selectedIdSet.has(item.id));

  const buildRegistryQueryParams = () => ({
    search: search.trim() || undefined,
    status: status || undefined,
    caseType: caseType.trim() || undefined,
    responsibleUserId: responsibleUserId ? Number(responsibleUserId) : undefined,
    priority: priority || undefined,
    from: from || undefined,
    to: to || undefined,
    minFee: canViewFinance && minFee ? Number(minFee) : undefined,
    maxFee: canViewFinance && maxFee ? Number(maxFee) : undefined,
    scope: caseScope === 'all' ? undefined : caseScope,
    activity: activityFilter === 'all' ? undefined : activityFilter,
    inactiveDays: INACTIVE_CASE_DAYS,
    sortBy,
    sortDir,
  });

  const exportRegistryCsv = async () => {
    if (!canExportCases) {
      setError('У роли нет права на выгрузку реестра дел.');
      return;
    }

    setRegistryExporting(true);
    setError(null);
    try {
      const { blob, fileName } = await apiService.exportCasesRegistryCsv(buildRegistryQueryParams());
      downloadBlob(fileName || 'cases-registry.csv', blob);
      setNotice('Реестр дел выгружен в CSV.');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось выгрузить реестр дел в CSV.'));
    } finally {
      setRegistryExporting(false);
    }
  };

  const exportRegistryExcel = async () => {
    if (!canExportCases) {
      setError('У роли нет права на выгрузку реестра дел.');
      return;
    }

    setRegistryExcelExporting(true);
    setError(null);
    try {
      const { blob, fileName } = await apiService.exportCasesRegistryExcel(buildRegistryQueryParams());
      downloadBlob(fileName || 'cases-registry.xls', blob);
      setNotice('Реестр дел выгружен в Excel.');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось выгрузить реестр дел в Excel.'));
    } finally {
      setRegistryExcelExporting(false);
    }
  };

  const exportRegistryPdf = async () => {
    if (!canExportCases) {
      setError('У роли нет права на выгрузку реестра дел.');
      return;
    }

    setRegistryPdfExporting(true);
    setError(null);
    try {
      const { blob, fileName } = await apiService.exportCasesRegistryPdf(buildRegistryQueryParams());
      downloadBlob(fileName || 'cases-registry.pdf', blob);
      setNotice('Реестр дел выгружен в PDF.');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось выгрузить реестр дел в PDF.'));
    } finally {
      setRegistryPdfExporting(false);
    }
  };

  const printRegistry = async () => {
    if (!canExportCases) {
      setError('У роли нет права на печать реестра дел.');
      return;
    }

    setRegistryPrinting(true);
    setError(null);
    try {
      const html = await apiService.getCasesRegistryPrintHtml(buildRegistryQueryParams());
      const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener,noreferrer');
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось открыть печатную форму реестра дел.'));
    } finally {
      setRegistryPrinting(false);
    }
  };

  const resolveAccessRequest = async (request: ClientCaseAccessRequest, grant: boolean) => {
    setResolvingAccessCaseId(request.clientCaseId);
    setError(null);
    setNotice(null);
    try {
      await apiService.resolveCaseAccessRequest(request.clientCaseId, { grant });
      setAccessRequests((current) => current.filter((item) => item.clientCaseId !== request.clientCaseId));
      setNotice(grant
        ? `Доступ к делу «${request.title || request.caseNumber || request.clientCaseId}» открыт.`
        : `Запрос доступа к делу «${request.title || request.caseNumber || request.clientCaseId}» отклонен.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось обработать запрос доступа к делу.'));
    } finally {
      setResolvingAccessCaseId(null);
    }
  };

  const toggleSelected = (id: number) => {
    setSelectedIds((current) => (
      current.includes(id)
        ? current.filter((itemId) => itemId !== id)
        : [...current, id]
    ));
  };

  const togglePageSelected = () => {
    if (allPageSelected) {
      setSelectedIds((current) => current.filter((id) => !items.some((item) => item.id === id)));
      return;
    }

    setSelectedIds((current) => Array.from(new Set([...current, ...items.map((item) => item.id)])));
  };

  const applyBulkUpdate = async () => {
    if (
      selectedIds.length === 0
      || (!bulkStatus && !bulkResponsibleUserId && !bulkStage.trim() && !bulkPriority && bulkControlState === 'keep')
    ) {
      return;
    }

    setBulkSaving(true);
    setError(null);
    setNotice(null);
    try {
      await apiService.bulkUpdateCases({
        ids: selectedIds,
        status: bulkStatus || undefined,
        responsibleUserId: bulkResponsibleUserId ? Number(bulkResponsibleUserId) : undefined,
        stage: bulkStage.trim() || undefined,
        priority: bulkPriority || undefined,
        isUnderControl: bulkControlState === 'keep' ? undefined : bulkControlState === 'on',
      });
      const selectedCount = selectedIds.length;
      setSelectedIds([]);
      setBulkStatus('');
      setBulkResponsibleUserId('');
      setBulkStage('');
      setBulkPriority('');
      setBulkControlState('keep');
      setNotice(`Массовое изменение применено: обновлено дел ${selectedCount}.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось выполнить групповое изменение дел.');
    } finally {
      setBulkSaving(false);
    }
  };

  const archiveSelectedCases = async () => {
    if (selectedIds.length === 0) {
      return;
    }

    setBulkSaving(true);
    setError(null);
    setNotice(null);
    try {
      await apiService.bulkUpdateCases({
        ids: selectedIds,
        status: 'Closed',
      });
      const selectedCount = selectedIds.length;
      setSelectedIds([]);
      setBulkStatus('');
      setBulkResponsibleUserId('');
      setBulkStage('');
      setBulkPriority('');
      setBulkControlState('keep');
      setNotice(`В архив отправлено дел: ${selectedCount}.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось отправить выбранные дела в архив.');
    } finally {
      setBulkSaving(false);
    }
  };

  const archiveCase = async (id: number) => {
    setBulkSaving(true);
    setError(null);
    try {
      await apiService.bulkUpdateCases({
        ids: [id],
        status: 'Closed',
      });
      setSelectedIds((current) => current.filter((itemId) => itemId !== id));
      setNotice('Дело отправлено в архив.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось отправить дело в архив.');
    } finally {
      setBulkSaving(false);
    }
  };

  const createTaskForCase = async (item: ClientCase) => {
    setCreatingTaskCaseId(item.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.createTaskJournalItem(buildCaseTaskPayload(item, timeZoneId));
      setNotice('Задача по делу создана.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать задачу по делу.');
    } finally {
      setCreatingTaskCaseId(null);
    }
  };

  const toggleCaseControl = async (item: ClientCase) => {
    setControlCaseId(item.id);
    setError(null);
    setNotice(null);
    try {
      const updated = await apiService.updateCaseControl(item.id, !item.isUnderControl);
      setItems((current) => current.map((caseItem) => (caseItem.id === updated.id ? updated : caseItem)));
      setNotice(updated.isUnderControl ? 'Дело поставлено на контроль.' : 'Дело снято с контроля.');
      if (caseScope === 'control') {
        setReloadToken((value) => value + 1);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось обновить контроль дела.');
    } finally {
      setControlCaseId(null);
    }
  };

  const runCourtSyncForCase = async (item: ClientCase) => {
    setCourtSyncCaseId(item.id);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.runCaseCourtSync(item.id);
      setNotice(result.message);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось запустить проверку судебного источника.');
    } finally {
      setCourtSyncCaseId(null);
    }
  };

  const exportCsv = (sourceItems = items, fileName = 'cases-registry.csv') => {
    if (!canExportCases) {
      setError('У роли нет права на выгрузку реестра дел.');
      return;
    }

    if (sourceItems.length === 0) {
      return;
    }

    const header = ['№ дела', 'Название', 'Клиент/юр. лицо', 'Ответственный', 'Статус', 'Тип', 'Этап', 'Создано', 'Начало', 'Срок', 'Открытые задачи', 'Просроченные задачи', 'Выполненные задачи', 'Прогресс', 'Последняя активность', 'Следующий автоконтроль', 'Причина исключения', 'Просмотр ответственным', 'Активность подтвердил', 'Приоритет', 'Суд', '№ в суде', 'Заседание'];
    if (canViewFinance) {
      header.splice(15, 0, 'Гонорар', 'Счета', 'Оплачено', 'Долг');
    }
    const rows = sourceItems.map((item) => {
      const row = [
      item.caseNumber,
      item.title,
      getOwnerName(item),
      getResponsibleName(item, responsibles),
      STATUS_LABELS[item.status] ?? item.status,
      item.caseType,
      item.stage,
      toRuDate(item.createdAt),
      toRuDate(item.startDate),
      toRuDate(item.dueDate),
      item.openTaskCount ?? 0,
      item.overdueTaskCount ?? 0,
      item.completedTaskCount ?? 0,
      `${item.taskProgressPercent ?? 0}%`,
      toRuDate(getLatestCaseActivityAt(item)),
      formatProfileDateTime(item.nextInactivityControlAt, timeZoneId),
      item.inactivityControlExclusionReason || '',
      toRuDate(getResponsibleLastViewedAt(item)),
      item.activityConfirmedByUserName || '',
      PRIORITY_LABELS[item.priority ?? ''] ?? item.priority,
      item.courtName,
      item.courtCaseNumber,
      formatProfileDateTime(item.nextHearingAt, timeZoneId),
      ];
      if (canViewFinance) {
        row.splice(
          15,
          0,
          typeof item.feeAmount === 'number' ? formatCurrencySmart(item.feeAmount).full : '',
          formatMoney(item.incomeTotal),
          formatMoney(item.incomePaid),
          formatMoney(item.incomeOutstanding),
        );
      }

      return row;
    });
    const csv = [header, ...rows].map((row) => row.map(csvCell).join(';')).join('\n');
    const blob = new Blob([`\uFEFF${csv}`], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
  };

  const openCase = (item: ClientCase) => {
    if (onOpenCase) {
      onOpenCase(item.id);
      return;
    }

    if (item.clientId && canViewClients) {
      onOpenClient?.(item.clientId, item.id);
      return;
    }

    if (item.legalEntityId && canViewLegalEntities) {
      onOpenLegalEntity?.(item.legalEntityId, item.id);
    }
  };

  const openOwner = (item: ClientCase) => {
    if (item.clientId && canViewClients) {
      onOpenClient?.(item.clientId);
      return;
    }

    if (item.legalEntityId && canViewLegalEntities) {
      onOpenLegalEntity?.(item.legalEntityId);
    }
  };

  const openCaseContextMenu = (event: MouseEvent<HTMLTableRowElement>, item: ClientCase) => {
    event.preventDefault();
    setSelectedIds((current) => (current.includes(item.id) ? current : [item.id]));

    const menuWidth = 250;
    const menuHeight = 320;
    const viewportWidth = typeof window === 'undefined' ? 0 : window.innerWidth;
    const viewportHeight = typeof window === 'undefined' ? 0 : window.innerHeight;
    const x = viewportWidth ? Math.min(event.clientX, Math.max(8, viewportWidth - menuWidth - 8)) : event.clientX;
    const y = viewportHeight ? Math.min(event.clientY, Math.max(8, viewportHeight - menuHeight - 8)) : event.clientY;

    setCaseContextMenu({ item, x, y });
  };

  const runContextMenuAction = (action: () => void) => {
    setCaseContextMenu(null);
    action();
  };

  const updateCase = async (patch: Partial<ClientCase>) => {
    if (!editingCase) return;

    const updated = await apiService.updateCaseV2(editingCase.id, buildCaseUpdatePayload(editingCase, patch));
    setItems((current) => current.map((item) => (item.id === updated.id ? updated : item)));
    setEditingCase(null);
    setNotice('Дело обновлено.');
    setReloadToken((value) => value + 1);
  };

  const renderCaseCell = (columnKey: CaseColumnKey, item: ClientCase) => {
    switch (columnKey) {
      case 'caseNumber':
        return (
          <button type="button" onClick={() => openCase(item)} className="font-semibold text-blue-700 hover:underline">
            {item.caseNumber || `#${item.id}`}
          </button>
        );
      case 'title':
        return (
          <div>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium text-slate-900">{item.title || 'Без названия'}</span>
              {item.isUnderControl ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-2 py-0.5 text-xs font-semibold text-rose-700">
                  <ShieldAlert className="h-3 w-3" />
                  На контроле
                </span>
              ) : null}
            </div>
            {item.description ? <div className="mt-1 line-clamp-2 text-xs text-slate-500">{item.description}</div> : null}
          </div>
        );
      case 'client':
        return getOwnerName(item);
      case 'responsible':
        return getResponsibleName(item, responsibles);
      case 'status':
        return (
          <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${getStatusClass(item.status)}`}>
            {STATUS_LABELS[item.status] ?? item.status}
          </span>
        );
      case 'caseType':
        return item.caseType || '—';
      case 'stage':
        return item.stage || '—';
      case 'createdAt':
        return toRuDate(item.createdAt) || '—';
      case 'startDate':
        return toRuDate(item.startDate) || '—';
      case 'dueDate':
        return toRuDate(item.dueDate) || '—';
      case 'lastViewedAt':
        return (
          <div>
            <div>{toRuDate(getResponsibleLastViewedAt(item)) || '—'}</div>
            {getResponsibleLastViewedByName(item) ? <div className="mt-1 text-xs text-slate-500">{getResponsibleLastViewedByName(item)}</div> : null}
          </div>
        );
      case 'feeAmount':
        return <span className="font-semibold text-slate-900">{typeof item.feeAmount === 'number' ? formatCurrencySmart(item.feeAmount).full : '—'}</span>;
      case 'priority':
        return (
          <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${getPriorityClass(item.priority)}`}>
            {PRIORITY_LABELS[item.priority ?? ''] ?? item.priority ?? 'Обычный'}
          </span>
        );
      case 'court':
        return (
          <div>
            <div>{item.courtName || '—'}</div>
            {item.courtCaseNumber ? <div className="mt-1 text-xs text-slate-500">№ {item.courtCaseNumber}</div> : null}
          </div>
        );
      case 'nextHearingAt':
        return item.nextHearingAt ? formatProfileDateTime(item.nextHearingAt, timeZoneId) : '—';
      case 'incomeTotal':
        return <span className="font-semibold text-slate-900">{formatMoney(item.incomeTotal)}</span>;
      case 'overdueTaskCount':
        return <span className={`font-semibold ${(item.overdueTaskCount ?? 0) > 0 ? 'text-rose-700' : 'text-slate-700'}`}>{item.overdueTaskCount ?? 0}</span>;
      case 'progress':
        return (
          <div>
            <div className="font-semibold text-slate-900">{item.taskProgressPercent ?? 0}%</div>
            <div className="text-xs text-slate-500">
              {item.completedTaskCount ?? 0} / {(item.completedTaskCount ?? 0) + (item.openTaskCount ?? 0)}
            </div>
          </div>
        );
      case 'activity': {
        const activity = getCaseActivityBadge(item);
        return (
          <div>
            <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${activity.className}`}>
              {activity.label}
            </span>
            <div className="mt-1 text-xs text-slate-500">{activity.hint}</div>
            <div className="mt-1 text-xs text-slate-600">
              Последняя: {formatProfileDateTime(getLatestCaseActivityAt(item), timeZoneId) || '—'}
            </div>
            {item.nextInactivityControlAt ? (
              <div className="mt-1 text-xs font-medium text-blue-700">
                Следующая: {formatProfileDateTime(item.nextInactivityControlAt, timeZoneId)}
              </div>
            ) : null}
            {item.inactivityControlExclusionReason ? (
              <div className="mt-1 break-words text-xs font-medium text-amber-700">
                Исключено: {item.inactivityControlExclusionReason}
              </div>
            ) : null}
          </div>
        );
      }
      case 'incomePaid':
        return <span className="font-semibold text-emerald-700">{formatMoney(item.incomePaid)}</span>;
      case 'incomeOutstanding':
        return <span className={`font-semibold ${(item.incomeOutstanding ?? 0) > 0 ? 'text-rose-700' : 'text-slate-700'}`}>{formatMoney(item.incomeOutstanding)}</span>;
      case 'actions':
        return (
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => openCase(item)}
              aria-label="Открыть дело"
              title="Открыть дело"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-slate-200 text-blue-700 hover:bg-blue-50">
              <Briefcase className="h-4 w-4" />
            </button>
            {canEditCases ? (
              <button
                type="button"
                onClick={() => setEditingCase(item)}
                aria-label="Редактировать дело"
                title="Редактировать дело"
                className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-blue-200 text-blue-700 hover:bg-blue-50">
                <Pencil className="h-4 w-4" />
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => void createTaskForCase(item)}
              disabled={creatingTaskCaseId === item.id}
              aria-label="Создать задачу по делу"
              title="Создать задачу по делу"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-emerald-200 text-emerald-700 hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-50">
              {creatingTaskCaseId === item.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />}
            </button>
            {canEditCases ? (
              <button
                type="button"
                onClick={() => void toggleCaseControl(item)}
                disabled={controlCaseId === item.id}
                aria-label={item.isUnderControl ? 'Снять дело с контроля' : 'Поставить дело на контроль'}
                title={item.isUnderControl ? 'Снять с контроля' : 'Поставить на контроль'}
                className={`inline-flex h-8 w-8 items-center justify-center rounded-full border disabled:cursor-not-allowed disabled:opacity-50 ${item.isUnderControl ? 'border-rose-200 text-rose-700 hover:bg-rose-50' : 'border-slate-200 text-slate-500 hover:bg-rose-50 hover:text-rose-700'}`}>
                {controlCaseId === item.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <ShieldAlert className="h-4 w-4" />}
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => openOwner(item)}
              aria-label="Открыть владельца"
              title="Открыть владельца"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-slate-200 text-slate-700 hover:bg-slate-50">
              <UserRound className="h-4 w-4" />
            </button>
            {canViewFinance ? (
              <button
                type="button"
                onClick={() => onOpenCaseFinance?.(item)}
                disabled={!onOpenCaseFinance}
                aria-label="Открыть финансы дела"
                title="Открыть финансы дела"
                className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-indigo-200 text-indigo-700 hover:bg-indigo-50 disabled:cursor-not-allowed disabled:opacity-50">
                <WalletCards className="h-4 w-4" />
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => void runCourtSyncForCase(item)}
              disabled={courtSyncCaseId === item.id}
              aria-label="Проверить суд по делу"
              title="Проверить суд по делу"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-cyan-200 text-cyan-700 hover:bg-cyan-50 disabled:cursor-not-allowed disabled:opacity-50">
              {courtSyncCaseId === item.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            </button>
            <button
              type="button"
              onClick={() => void archiveCase(item.id)}
              disabled={bulkSaving || item.status === 'Closed'}
              aria-label="В архив"
              title="В архив"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-amber-200 text-amber-700 hover:bg-amber-50 disabled:cursor-not-allowed disabled:opacity-50">
              <Archive className="h-4 w-4" />
            </button>
          </div>
        );
      default:
        return null;
    }
  };

  const renderCaseMobileCard = (item: ClientCase) => {
    const overdueCount = item.overdueTaskCount ?? 0;
    const riskLabel = overdueCount > 0 ? `${overdueCount} просроч.` : item.status === 'Closed' ? 'Закрыто' : 'Без риска';

    return (
      <article key={item.id} className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="flex items-start gap-3">
          <SystemCheckbox
            type="checkbox"
            checked={selectedIdSet.has(item.id)}
            onChange={() => toggleSelected(item.id)}
            aria-label={`Выбрать дело ${item.caseNumber || item.id}`}
            className="mt-1 h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
          />
          <div className="min-w-0 flex-1">
            <button type="button" onClick={() => openCase(item)} className="text-left font-semibold text-blue-700 hover:underline">
              {item.caseNumber || `#${item.id}`} · {item.title || 'Без названия'}
            </button>
            <div className="mt-1 text-sm text-slate-600">{getOwnerName(item)}</div>
          </div>
        </div>

        <div className="mt-4 grid gap-3 text-sm sm:grid-cols-2">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">Статус</div>
            <span className={`mt-1 inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${getStatusClass(item.status)}`}>
              {STATUS_LABELS[item.status] ?? item.status}
            </span>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">Ответственный</div>
            <div className="mt-1 text-slate-700">{getResponsibleName(item, responsibles)}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">Ближайший срок</div>
            <div className="mt-1 text-slate-700">{toRuDate(item.dueDate) || formatProfileDateTime(item.nextHearingAt, timeZoneId) || '—'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">Индикатор риска</div>
            <div className={`mt-1 font-semibold ${overdueCount > 0 ? 'text-rose-700' : 'text-emerald-700'}`}>{riskLabel}</div>
          </div>
        </div>

        <div className="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3">
          {canViewFinance ? (
            <div className="text-sm text-slate-600">
              <span className="font-semibold text-slate-900">{formatMoney(item.feeAmount)}</span>
              {typeof item.incomeOutstanding === 'number' ? <span className="ml-2 text-rose-700">Долг: {formatMoney(item.incomeOutstanding)}</span> : null}
            </div>
          ) : <span />}
          {renderCaseCell('actions', item)}
        </div>
      </article>
    );
  };

  const startCreateCase = (selection: CounterpartySelection | null) => {
    if (!selection) return;
    setCaseDraft({
      id: 0,
      clientId: selection.kind === 'client' ? selection.id : null,
      legalEntityId: selection.kind === 'legalEntity' ? selection.id : null,
      client: selection.kind === 'client' ? (selection.client ?? null) : null,
      legalEntity: selection.kind === 'legalEntity' ? (selection.legalEntity ?? null) : null,
      title: selection.name ? `${selection.name} - новое дело` : '',
      description: '',
      status: 'Open',
      priority: 'normal',
      feeAmount: 0,
      createdAt: new Date().toISOString(),
    } as ClientCase);
  };

  const createCase = async (patch: Partial<ClientCase>) => {
    if (!caseDraft) return;

    const payload = {
      clientId: caseDraft.clientId ?? null,
      legalEntityId: caseDraft.legalEntityId ?? null,
      title: patch.title ?? caseDraft.title,
      description: patch.description ?? caseDraft.description ?? '',
      status: patch.status ?? caseDraft.status,
      caseNumber: patch.caseNumber ?? '',
      caseType: patch.caseType ?? '',
      stage: patch.stage ?? '',
      responsibleUserId: patch.responsibleUserId ?? null,
      startDate: patch.startDate ?? null,
      dueDate: patch.dueDate ?? null,
      feeAmount: patch.feeAmount ?? 0,
      priority: patch.priority ?? 'normal',
      courtName: patch.courtName ?? '',
      courtCaseNumber: patch.courtCaseNumber ?? '',
      courtUrl: patch.courtUrl ?? '',
      nextHearingAt: patch.nextHearingAt ?? null,
      claimant: patch.claimant ?? '',
      defendant: patch.defendant ?? '',
      thirdParties: patch.thirdParties ?? '',
      representatives: patch.representatives ?? '',
      judgeName: patch.judgeName ?? '',
      courtroom: patch.courtroom ?? '',
      courtSecretaryContacts: patch.courtSecretaryContacts ?? '',
      claimFiledAt: patch.claimFiledAt ?? null,
      appealDeadlineAt: patch.appealDeadlineAt ?? null,
      motionDeadlineAt: patch.motionDeadlineAt ?? null,
      objectionDeadlineAt: patch.objectionDeadlineAt ?? null,
      documentsSummary: patch.documentsSummary ?? '',
      eventHistory: patch.eventHistory ?? '',
      strategy: patch.strategy ?? '',
    } as Omit<ClientCase, 'id' | 'createdAt' | 'payments'>;

    await apiService.createCaseV2(payload);
    setCaseDraft(null);
    setReloadToken((value) => value + 1);
  };

  return (
    <div className="min-h-screen bg-slate-50 px-4 py-6 sm:px-6 lg:px-8">
      <div className="mx-auto max-w-[1500px] space-y-5">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <div className="flex items-center gap-3">
              <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-blue-100 text-blue-700">
                <Briefcase className="h-6 w-6" />
              </div>
              <div>
                <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Юридическая работа</p>
                <h1 className="text-3xl font-bold text-slate-900">Реестр дел</h1>
              </div>
            </div>
            <p className="mt-2 max-w-3xl text-sm text-slate-600">
              Общий журнал дел с ответственными, сроками, гонорарами и судебными реквизитами.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            {allowCreate ? (
              <button
                type="button"
                onClick={() => {
                  if (canSelectCaseOwner) {
                    setOwnerPickerOpen(true);
                  }
                }}
                disabled={!canSelectCaseOwner}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60">
                <Plus className="h-4 w-4" /> Создать дело
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => setReloadToken((value) => value + 1)}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
              <RefreshCw className="h-4 w-4" /> Обновить
            </button>
            {canExportCases ? (
              <>
                <button
                  type="button"
                  onClick={() => void exportRegistryCsv()}
                  disabled={loading || registryExporting}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                  {registryExporting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} CSV
                </button>
                <button
                  type="button"
                  onClick={() => void exportRegistryExcel()}
                  disabled={loading || registryExcelExporting}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                  {registryExcelExporting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} Excel
                </button>
                <button
                  type="button"
                  onClick={() => void exportRegistryPdf()}
                  disabled={loading || registryPdfExporting}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                  {registryPdfExporting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} PDF
                </button>
                <button
                  type="button"
                  onClick={() => void printRegistry()}
                  disabled={loading || registryPrinting}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                  {registryPrinting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Printer className="h-4 w-4" />} Печать
                </button>
              </>
            ) : null}
            <button
              type="button"
              onClick={() => setColumnsOpen((value) => !value)}
              className={`inline-flex items-center gap-2 rounded-lg border px-4 py-2 text-sm font-medium ${columnsOpen ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-100'}`}>
              <Settings2 className="h-4 w-4" /> Колонки
            </button>
          </div>
        </div>

        {columnsOpen ? (
          <TableColumnSettings
            title="Настройка колонок реестра дел"
            description="Выберите видимые колонки, измените порядок и ширину. Настройка сохраняется под пользователя."
            columns={caseColumnConfigs.map((column) => ({ key: column.key, label: column.label }))}
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

        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="mb-3 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div className="inline-flex w-fit rounded-lg bg-slate-100 p-1">
              {CASE_SCOPE_OPTIONS.map((option) => (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => changeCaseScope(option.value)}
                  className={`rounded-md px-3 py-1.5 text-sm font-medium transition ${caseScope === option.value ? 'bg-white text-blue-700 shadow-sm' : 'text-slate-600 hover:text-slate-900'}`}>
                  {option.label}
                </button>
              ))}
            </div>
            <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
              <SystemSelect
                value={selectedSavedViewId}
                onChange={(event) => selectSavedView(event.target.value)}
                aria-label="Сохраненные представления"
                className="h-10 min-w-[220px] rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-700">
                <option value="">Сохраненные представления</option>
                {savedViews.map((view) => (
                  <option key={view.id} value={view.id}>{view.name}</option>
                ))}
              </SystemSelect>
              <button
                type="button"
                onClick={saveCurrentView}
                className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 hover:bg-slate-100">
                <Bookmark className="h-4 w-4" /> Сохранить вид
              </button>
              <button
                type="button"
                onClick={deleteSelectedView}
                disabled={!selectedSavedViewId}
                className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50"
                aria-label="Удалить сохраненное представление">
                <Trash2 className="h-4 w-4" /> Удалить
              </button>
            </div>
          </div>
          <div className="mb-3 md:hidden">
            <button
              type="button"
              onClick={() => setMobileFiltersOpen((value) => !value)}
              className="inline-flex w-full items-center justify-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 text-sm font-semibold text-slate-700">
              <Search className="h-4 w-4" />
              {mobileFiltersOpen ? 'Скрыть фильтры' : 'Показать фильтры'}
            </button>
          </div>
          <div className={`${mobileFiltersOpen ? 'grid' : 'hidden'} gap-3 md:grid md:grid-cols-2 xl:grid-cols-6`}>
            <label className="relative xl:col-span-2">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 bg-white pl-9 pr-3 text-sm outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
                placeholder="Поиск по делу, клиенту, суду"
              />
            </label>
            <SystemSelect value={status} onChange={(event) => setStatus(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </SystemSelect>
            <input
              list="case-types"
              value={caseType}
              onChange={(event) => setCaseType(event.target.value)}
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm"
              placeholder="Тип дела"
            />
            <datalist id="case-types">
              {typeOptions.map((type) => <option key={type} value={type} />)}
            </datalist>
            <SystemSelect value={responsibleUserId} onChange={(event) => setResponsibleUserId(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              <option value="">Все ответственные</option>
              {responsibles.map((responsible) => <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>)}
            </SystemSelect>
            <SystemSelect value={priority} onChange={(event) => setPriority(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </SystemSelect>
            <SystemSelect value={activityFilter} onChange={(event) => setActivityFilter(event.target.value as CaseActivityFilter)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {CASE_ACTIVITY_FILTER_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </SystemSelect>
          </div>

          <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-6">
            <SystemDateInput type="date" value={from} onChange={(event) => setFrom(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата с" />
            <SystemDateInput type="date" value={to} onChange={(event) => setTo(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата по" />
            {canViewFinance ? (
              <>
                <input type="number" min="0" value={minFee} onChange={(event) => setMinFee(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" placeholder="Гонорар от" />
                <input type="number" min="0" value={maxFee} onChange={(event) => setMaxFee(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" placeholder="Гонорар до" />
              </>
            ) : null}
            <SystemSelect value={pageSize} onChange={(event) => setPageSize(Number(event.target.value))} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {[25, 50, 100, 200].map((size) => <option key={size} value={size}>{size} строк</option>)}
            </SystemSelect>
            <button
              type="button"
              onClick={() => {
                setSearch('');
                setCaseScope('all');
                setActivityFilter('all');
                setStatus('');
                setCaseType('');
                setResponsibleUserId('');
                setPriority('');
                setFrom('');
                setTo('');
                setMinFee('');
                setMaxFee('');
                setSelectedSavedViewId('');
              }}
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 hover:bg-slate-100">
              Сбросить
            </button>
          </div>
        </div>

        {canManageCaseAccess && (accessRequestsLoading || accessRequests.length > 0) ? (
          <section className="rounded-xl border border-amber-200 bg-amber-50/70 p-4 shadow-sm">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <div className="inline-flex items-center gap-2 text-sm font-semibold text-amber-950">
                  <LockKeyhole className="h-4 w-4" />
                  Запросы доступа к скрытым делам
                </div>
                <div className="mt-1 text-xs text-amber-800">
                  {accessRequestsLoading ? 'Проверяем ожидающие запросы...' : `Ожидает решения: ${accessRequests.length}`}
                </div>
              </div>
            </div>
            {accessRequests.length > 0 ? (
              <div className="mt-3 grid gap-3 xl:grid-cols-2">
                {accessRequests.map((request) => {
                  const caseLabel = request.caseNumber ? `№ ${request.caseNumber}` : `#${request.clientCaseId}`;
                  return (
                    <article key={request.clientCaseId} className="rounded-lg border border-amber-200 bg-white p-3">
                      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                        <div className="min-w-0">
                          <button
                            type="button"
                            onClick={() => onOpenCase?.(request.clientCaseId)}
                            className="text-left text-sm font-semibold text-blue-700 hover:underline">
                            {caseLabel} · {request.title || 'Без названия'}
                          </button>
                          <div className="mt-1 text-xs text-slate-600">
                            {request.ownerName || 'Владелец не указан'} · ответственный: {request.responsibleName || 'не указан'}
                          </div>
                          <div className="mt-1 text-xs text-slate-500">
                            Запросил: {request.requestedByUserName || 'пользователь'} · {toRuDate(request.requestedAt) || 'дата не указана'}
                          </div>
                          {request.reason ? <div className="mt-2 text-sm text-slate-700">{request.reason}</div> : null}
                        </div>
                        <div className="flex shrink-0 flex-wrap items-center gap-2">
                          <button
                            type="button"
                            onClick={() => onOpenCase?.(request.clientCaseId)}
                            className="h-9 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 hover:bg-slate-100">
                            Открыть
                          </button>
                          <button
                            type="button"
                            onClick={() => void resolveAccessRequest(request, true)}
                            disabled={resolvingAccessCaseId === request.clientCaseId}
                            className="h-9 rounded-lg bg-emerald-600 px-3 text-sm font-semibold text-white hover:bg-emerald-700 disabled:cursor-not-allowed disabled:opacity-50">
                            Разрешить
                          </button>
                          <button
                            type="button"
                            onClick={() => void resolveAccessRequest(request, false)}
                            disabled={resolvingAccessCaseId === request.clientCaseId}
                            className="h-9 rounded-lg border border-rose-200 bg-rose-50 px-3 text-sm font-medium text-rose-700 hover:bg-rose-100 disabled:cursor-not-allowed disabled:opacity-50">
                            Отклонить
                          </button>
                        </div>
                      </div>
                    </article>
                  );
                })}
              </div>
            ) : null}
          </section>
        ) : null}

        {selectedIds.length > 0 ? (
          <div className="rounded-xl border border-blue-100 bg-blue-50/60 p-4 shadow-sm">
            <div className="flex flex-col gap-3 xl:flex-row xl:flex-wrap xl:items-center">
              <div className="text-sm font-semibold text-blue-900">
                Выбрано дел: {selectedIds.length}
              </div>
              {canViewFinance ? (
                <div className="grid gap-1 text-xs text-blue-900 sm:grid-cols-3">
                  <span>Гонорар: {formatCurrencySmart(selectedSummary.feeAmount).full}</span>
                  <span>Оплачено: {formatCurrencySmart(selectedSummary.incomePaid).full}</span>
                  <span>Долг: {formatCurrencySmart(selectedSummary.incomeOutstanding).full}</span>
                </div>
              ) : null}
              <SystemSelect
                value={bulkStatus}
                onChange={(event) => setBulkStatus(event.target.value)}
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="">Не менять статус</option>
                {STATUS_OPTIONS.filter((option) => option.value).map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
              <SystemSelect
                value={bulkResponsibleUserId}
                onChange={(event) => setBulkResponsibleUserId(event.target.value)}
                aria-label="Массово сменить ответственного"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="">Не менять ответственного</option>
                {responsibles.map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>
                ))}
              </SystemSelect>
              <input
                value={bulkStage}
                onChange={(event) => setBulkStage(event.target.value)}
                aria-label="Массово сменить стадию"
                placeholder="Не менять стадию"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm"
              />
              <SystemSelect
                value={bulkPriority}
                onChange={(event) => setBulkPriority(event.target.value)}
                aria-label="Массово сменить приоритет"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="">Не менять приоритет</option>
                {PRIORITY_OPTIONS.filter((option) => option.value).map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
              <SystemSelect
                value={bulkControlState}
                onChange={(event) => setBulkControlState(event.target.value as 'keep' | 'on' | 'off')}
                aria-label="Массово изменить контроль"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="keep">Не менять контроль</option>
                <option value="on">Поставить на контроль</option>
                <option value="off">Снять с контроля</option>
              </SystemSelect>
              <button
                type="button"
                onClick={() => void applyBulkUpdate()}
                disabled={bulkSaving || (!bulkStatus && !bulkResponsibleUserId && !bulkStage.trim() && !bulkPriority && bulkControlState === 'keep')}
                className="h-10 rounded-lg bg-blue-600 px-4 text-sm font-semibold text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50">
                {bulkSaving ? 'Сохраняем...' : 'Применить'}
              </button>
              {canExportCases ? (
                <button
                  type="button"
                  onClick={() => exportCsv(selectedItems, 'cases-registry-selected.csv')}
                  disabled={selectedItems.length === 0 || bulkSaving}
                  className="inline-flex h-10 items-center gap-2 rounded-lg border border-blue-100 bg-white px-4 text-sm font-medium text-blue-700 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-50">
                  <Download className="h-4 w-4" /> CSV выбранных
                </button>
              ) : null}
              <button
                type="button"
                onClick={() => void archiveSelectedCases()}
                disabled={bulkSaving || selectedIds.length === 0}
                className="h-10 rounded-lg border border-amber-200 bg-amber-50 px-4 text-sm font-medium text-amber-800 hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-50">
                В архив
              </button>
              <button
                type="button"
                onClick={() => setSelectedIds([])}
                disabled={bulkSaving}
                className="h-10 rounded-lg border border-blue-100 bg-white px-4 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                Снять выбор
              </button>
            </div>
          </div>
        ) : null}

        {error ? (
          <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
        ) : null}
        {notice ? (
          <div className="rounded-xl border border-cyan-200 bg-cyan-50 px-4 py-3 text-sm text-cyan-800">{notice}</div>
        ) : null}

        <div className="space-y-3 md:hidden">
          <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600 shadow-sm">
            <span className="font-semibold text-slate-900">Краткий список дел</span>
            <span>Найдено: {total}</span>
          </div>
          {loading ? (
            <div className="rounded-xl border border-slate-200 bg-white px-4 py-8 text-center text-sm text-slate-500">
              <span className="inline-flex items-center gap-2"><Loader2 className="h-4 w-4 animate-spin" /> Загрузка дел...</span>
            </div>
          ) : items.length === 0 ? (
            <div className="rounded-xl border border-slate-200 bg-white px-4 py-8 text-center text-sm text-slate-500">По текущим фильтрам дел нет.</div>
          ) : (
            items.map(renderCaseMobileCard)
          )}
          <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600 shadow-sm">
            <button
              type="button"
              onClick={() => setPage((value) => Math.max(1, value - 1))}
              disabled={page <= 1 || loading}
              className="rounded-lg border border-slate-200 px-3 py-1.5 disabled:cursor-not-allowed disabled:opacity-50">
              Назад
            </button>
            <span>{page} / {totalPages}</span>
            <button
              type="button"
              onClick={() => setPage((value) => Math.min(totalPages, value + 1))}
              disabled={page >= totalPages || loading}
              className="rounded-lg border border-slate-200 px-3 py-1.5 disabled:cursor-not-allowed disabled:opacity-50">
              Вперёд
            </button>
          </div>
        </div>

        <div className="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm md:block">
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm" style={{ minWidth: getTableWidth(48, 1200) }}>
              <thead className="bg-slate-50 text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">
                <tr>
                  <th className="w-12 px-4 py-3">
                    <SystemCheckbox
                      type="checkbox"
                      checked={allPageSelected}
                      onChange={togglePageSelected}
                      aria-label="Выбрать все дела на странице"
                      className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                    />
                  </th>
                  {visibleCaseColumns.map((column) => (
                    <th
                      key={column.key}
                      className="relative px-4 py-3"
                      style={{ width: getColumnWidth(column.key) }}>
                      {column.sortKey ? (
                        <button type="button" onClick={() => changeSort(column.sortKey!)} className="inline-flex items-center gap-1 hover:text-slate-900">
                          {column.label}
                          <ArrowUpDown className="h-3.5 w-3.5" />
                        </button>
                      ) : (
                        <span>{column.label}</span>
                      )}
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${column.label}`}
                        onMouseDown={(event) => handleResizeStart(column.key, event)}
                        className="absolute right-0 top-2 h-7 w-1 cursor-col-resize rounded bg-transparent hover:bg-blue-300"
                      />
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {loading ? (
                  <tr>
                    <td colSpan={visibleCaseColumns.length + 1} className="px-4 py-12 text-center text-slate-500">
                      <span className="inline-flex items-center gap-2"><Loader2 className="h-4 w-4 animate-spin" /> Загрузка дел...</span>
                    </td>
                  </tr>
                ) : items.length === 0 ? (
                  <tr>
                    <td colSpan={visibleCaseColumns.length + 1} className="px-4 py-12 text-center text-slate-500">По текущим фильтрам дел нет.</td>
                  </tr>
                ) : (
                  items.map((item) => (
                    <tr
                      key={item.id}
                      onContextMenu={(event) => openCaseContextMenu(event, item)}
                      className="align-top hover:bg-slate-50">
                      <td className="px-4 py-3">
                        <SystemCheckbox
                          type="checkbox"
                          checked={selectedIdSet.has(item.id)}
                          onChange={() => toggleSelected(item.id)}
                          aria-label={`Выбрать дело ${item.caseNumber || item.id}`}
                          className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                        />
                      </td>
                      {visibleCaseColumns.map((column) => (
                        <td key={column.key} className="px-4 py-3 text-slate-700" style={{ width: getColumnWidth(column.key) }}>
                          {renderCaseCell(column.key, item)}
                        </td>
                      ))}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <div className="flex flex-col gap-3 border-t border-slate-100 px-4 py-3 text-sm text-slate-600 sm:flex-row sm:items-center sm:justify-between">
            <div>Найдено: {total}</div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => setPage((value) => Math.max(1, value - 1))}
                disabled={page <= 1 || loading}
                className="rounded-lg border border-slate-200 px-3 py-1.5 disabled:cursor-not-allowed disabled:opacity-50">
                Назад
              </button>
              <span>{page} / {totalPages}</span>
              <button
                type="button"
                onClick={() => setPage((value) => Math.min(totalPages, value + 1))}
                disabled={page >= totalPages || loading}
                className="rounded-lg border border-slate-200 px-3 py-1.5 disabled:cursor-not-allowed disabled:opacity-50">
                Вперёд
              </button>
            </div>
          </div>
        </div>
      </div>

      <CounterpartyPickerModal
        open={ownerPickerOpen}
        title="Выбор владельца дела"
        counterpartyRole="client"
        allowedScopes={allowedOwnerScopes}
        onClose={() => setOwnerPickerOpen(false)}
        onSelect={startCreateCase}
      />

      {caseDraft ? (
        <CaseModal
          isOpen
          caseData={caseDraft}
          responsibles={responsibles}
          onClose={() => setCaseDraft(null)}
          onSave={createCase}
          allowEdit={allowCreate}
          canViewFinance={canViewFinance}
          canUseContactRelatedParties={canViewClients}
          canUseLegalEntityRelatedParties={canViewLegalEntities}
        />
      ) : null}
      {editingCase ? (
        <CaseModal
          isOpen
          caseData={editingCase}
          responsibles={responsibles}
          onClose={() => setEditingCase(null)}
          onSave={updateCase}
          allowEdit={canEditCases}
          canViewFinance={canViewFinance}
          canUseContactRelatedParties={canViewClients}
          canUseLegalEntityRelatedParties={canViewLegalEntities}
        />
      ) : null}
      {caseContextMenu ? (
        <div
          role="menu"
          aria-label={`Действия по делу ${caseContextMenu.item.caseNumber || caseContextMenu.item.id}`}
          className="fixed z-[1200] w-[250px] overflow-hidden rounded-xl border border-slate-200 bg-white py-1 text-sm shadow-xl"
          style={{ left: caseContextMenu.x, top: caseContextMenu.y }}>
          <button
            type="button"
            role="menuitem"
            onClick={() => runContextMenuAction(() => openCase(caseContextMenu.item))}
            className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-slate-50">
            <Briefcase className="h-4 w-4 text-blue-700" /> Открыть карточку
          </button>
          {canEditCases ? (
            <button
              type="button"
              role="menuitem"
              onClick={() => runContextMenuAction(() => setEditingCase(caseContextMenu.item))}
              className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-slate-50">
              <Pencil className="h-4 w-4 text-blue-700" /> Редактировать дело
            </button>
          ) : null}
          <button
            type="button"
            role="menuitem"
            onClick={() => runContextMenuAction(() => void createTaskForCase(caseContextMenu.item))}
            disabled={creatingTaskCaseId === caseContextMenu.item.id}
            className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50">
            <ClipboardPlus className="h-4 w-4 text-emerald-700" /> Создать задачу
          </button>
          {canEditCases ? (
            <button
              type="button"
              role="menuitem"
              onClick={() => runContextMenuAction(() => void toggleCaseControl(caseContextMenu.item))}
              disabled={controlCaseId === caseContextMenu.item.id}
              className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-50">
              <ShieldAlert className="h-4 w-4 text-rose-700" /> {caseContextMenu.item.isUnderControl ? 'Снять с контроля' : 'Поставить на контроль'}
            </button>
          ) : null}
          <button
            type="button"
            role="menuitem"
            onClick={() => runContextMenuAction(() => openOwner(caseContextMenu.item))}
            disabled={
              (!caseContextMenu.item.clientId || !canViewClients)
              && (!caseContextMenu.item.legalEntityId || !canViewLegalEntities)
            }
            className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50">
            <UserRound className="h-4 w-4 text-slate-700" /> Открыть владельца
          </button>
          {canViewFinance ? (
            <button
              type="button"
              role="menuitem"
              onClick={() => runContextMenuAction(() => onOpenCaseFinance?.(caseContextMenu.item))}
              disabled={!onOpenCaseFinance}
              className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50">
              <WalletCards className="h-4 w-4 text-indigo-700" /> Финансы дела
            </button>
          ) : null}
          <button
            type="button"
            role="menuitem"
            onClick={() => runContextMenuAction(() => void runCourtSyncForCase(caseContextMenu.item))}
            disabled={courtSyncCaseId === caseContextMenu.item.id}
            className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50">
            <RefreshCw className="h-4 w-4 text-cyan-700" /> Проверить суд
          </button>
          <div className="my-1 border-t border-slate-100" />
          <button
            type="button"
            role="menuitem"
            onClick={() => runContextMenuAction(() => void archiveCase(caseContextMenu.item.id))}
            disabled={bulkSaving || caseContextMenu.item.status === 'Closed'}
            className="flex w-full items-center gap-2 px-3 py-2 text-left text-amber-700 hover:bg-amber-50 disabled:cursor-not-allowed disabled:opacity-50">
            <Archive className="h-4 w-4" /> В архив
          </button>
        </div>
      ) : null}
    </div>
  );
}
