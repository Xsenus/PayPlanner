import { useEffect, useMemo, useState } from 'react';
import { Archive, ArrowUpDown, Briefcase, ClipboardPlus, Download, Loader2, Plus, RefreshCw, Search, Settings2, UserRound, WalletCards } from 'lucide-react';

import { apiService } from '../../services/api';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import type { ActResponsible, ClientCase } from '../../types';
import { toRuDate } from '../../utils/dateUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { CaseModal } from '../Clients/CaseModal';
import { CounterpartyPickerModal, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
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
type CaseScope = 'all' | 'active' | 'mine' | 'archive';
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
  { value: 'archive', label: 'Архив' },
];

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
  { key: 'lastViewedAt', label: 'Последний просмотр', sortKey: 'lastViewedAt', defaultWidth: 180 },
  { key: 'feeAmount', label: 'Гонорар', sortKey: 'feeAmount', defaultWidth: 140 },
  { key: 'priority', label: 'Приоритет', sortKey: 'priority', defaultWidth: 140 },
  { key: 'court', label: 'Суд', sortKey: 'court', defaultWidth: 240 },
  { key: 'nextHearingAt', label: 'Заседание', sortKey: 'nextHearingAt', defaultWidth: 140 },
  { key: 'incomeTotal', label: 'Счета', defaultWidth: 130 },
  { key: 'overdueTaskCount', label: 'Просрочено', defaultWidth: 130 },
  { key: 'progress', label: 'Прогресс', defaultWidth: 140 },
  { key: 'activity', label: 'Активность', defaultWidth: 140 },
  { key: 'incomePaid', label: 'Оплачено', defaultWidth: 130 },
  { key: 'incomeOutstanding', label: 'Долг', defaultWidth: 130 },
  { key: 'actions', label: 'Действия', defaultWidth: 150 },
];

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

function getLatestCaseActivityAt(item: ClientCase) {
  const values = [item.lastTaskAt, item.activityConfirmedAt, item.lastViewedAt]
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

export function CasesRegistry({ onOpenCase, onOpenClient, onOpenLegalEntity, onOpenCaseFinance, allowCreate = true }: CasesRegistryProps) {
  const [items, setItems] = useState<ClientCase[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [caseScope, setCaseScope] = useState<CaseScope>('all');
  const [caseType, setCaseType] = useState('');
  const [responsibleUserId, setResponsibleUserId] = useState('');
  const [priority, setPriority] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [minFee, setMinFee] = useState('');
  const [maxFee, setMaxFee] = useState('');
  const [sortBy, setSortBy] = useState<SortKey>('createdAt');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const [reloadToken, setReloadToken] = useState(0);
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [bulkStatus, setBulkStatus] = useState('');
  const [bulkResponsibleUserId, setBulkResponsibleUserId] = useState('');
  const [bulkStage, setBulkStage] = useState('');
  const [bulkPriority, setBulkPriority] = useState('');
  const [bulkSaving, setBulkSaving] = useState(false);
  const [creatingTaskCaseId, setCreatingTaskCaseId] = useState<number | null>(null);
  const [courtSyncCaseId, setCourtSyncCaseId] = useState<number | null>(null);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [mobileFiltersOpen, setMobileFiltersOpen] = useState(false);
  const [ownerPickerOpen, setOwnerPickerOpen] = useState(false);
  const [caseDraft, setCaseDraft] = useState<ClientCase | null>(null);
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
    columns: CASE_COLUMN_CONFIGS.map((column) => ({
      key: column.key,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 25,
    maxPageSize: 200,
  });

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    Promise.all([
      apiService.getCasesV2({
        search: search.trim() || undefined,
        status: status || undefined,
        caseType: caseType.trim() || undefined,
        responsibleUserId: responsibleUserId ? Number(responsibleUserId) : undefined,
        priority: priority || undefined,
        from: from || undefined,
        to: to || undefined,
        minFee: minFee ? Number(minFee) : undefined,
        maxFee: maxFee ? Number(maxFee) : undefined,
        scope: caseScope === 'all' ? undefined : caseScope,
        sortBy,
        sortDir,
        page,
        pageSize,
      }),
      apiService.getActResponsibles().catch(() => [] as ActResponsible[]),
    ])
      .then(([casesResult, responsibleResult]) => {
        if (cancelled) return;
        setItems(casesResult.items ?? []);
        setTotal(casesResult.total ?? 0);
        setResponsibles(responsibleResult ?? []);
      })
      .catch((err) => {
        if (cancelled) return;
        setItems([]);
        setTotal(0);
        setError(err instanceof Error ? err.message : 'Не удалось загрузить реестр дел.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [caseScope, caseType, from, maxFee, minFee, page, pageSize, priority, reloadToken, responsibleUserId, search, sortBy, sortDir, status, to]);

  useEffect(() => {
    setPage(1);
  }, [caseScope, caseType, from, maxFee, minFee, pageSize, priority, responsibleUserId, search, status, to]);

  useEffect(() => {
    setSelectedIds((current) => current.filter((id) => items.some((item) => item.id === id)));
  }, [items]);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  const typeOptions = useMemo(() => {
    const values = Array.from(new Set(items.map((item) => item.caseType?.trim()).filter(Boolean) as string[]));
    return values.sort((a, b) => a.localeCompare(b, 'ru'));
  }, [items]);

  const caseColumnsByKey = useMemo(
    () => Object.fromEntries(CASE_COLUMN_CONFIGS.map((column) => [column.key, column])) as Record<CaseColumnKey, (typeof CASE_COLUMN_CONFIGS)[number]>,
    [],
  );
  const visibleCaseColumns = useMemo(
    () => orderedVisibleColumns.map((key) => caseColumnsByKey[key]).filter(Boolean),
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
    if (selectedIds.length === 0 || (!bulkStatus && !bulkResponsibleUserId && !bulkStage.trim() && !bulkPriority)) {
      return;
    }

    setBulkSaving(true);
    setError(null);
    try {
      await apiService.bulkUpdateCases({
        ids: selectedIds,
        status: bulkStatus || undefined,
        responsibleUserId: bulkResponsibleUserId ? Number(bulkResponsibleUserId) : undefined,
        stage: bulkStage.trim() || undefined,
        priority: bulkPriority || undefined,
      });
      setSelectedIds([]);
      setBulkStatus('');
      setBulkResponsibleUserId('');
      setBulkStage('');
      setBulkPriority('');
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
    try {
      await apiService.bulkUpdateCases({
        ids: selectedIds,
        status: 'Closed',
      });
      setSelectedIds([]);
      setBulkStatus('');
      setBulkResponsibleUserId('');
      setBulkStage('');
      setBulkPriority('');
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
      await apiService.createTaskJournalCaseAutomationTask(item.id);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать задачу по делу.');
    } finally {
      setCreatingTaskCaseId(null);
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
    if (sourceItems.length === 0) {
      return;
    }

    const header = ['№ дела', 'Название', 'Клиент/юр. лицо', 'Ответственный', 'Статус', 'Тип', 'Этап', 'Создано', 'Начало', 'Срок', 'Открытые задачи', 'Просроченные задачи', 'Выполненные задачи', 'Прогресс', 'Последняя активность', 'Гонорар', 'Счета', 'Оплачено', 'Долг', 'Последний просмотр', 'Активность подтвердил', 'Приоритет', 'Суд', '№ в суде', 'Заседание'];
    const rows = sourceItems.map((item) => [
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
      typeof item.feeAmount === 'number' ? formatCurrencySmart(item.feeAmount).full : '',
      formatMoney(item.incomeTotal),
      formatMoney(item.incomePaid),
      formatMoney(item.incomeOutstanding),
      toRuDate(item.lastViewedAt),
      item.activityConfirmedByUserName || '',
      PRIORITY_LABELS[item.priority ?? ''] ?? item.priority,
      item.courtName,
      item.courtCaseNumber,
      toRuDate(item.nextHearingAt),
    ]);
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

    if (item.clientId) {
      onOpenClient?.(item.clientId, item.id);
      return;
    }

    if (item.legalEntityId) {
      onOpenLegalEntity?.(item.legalEntityId, item.id);
    }
  };

  const openOwner = (item: ClientCase) => {
    if (item.clientId) {
      onOpenClient?.(item.clientId);
      return;
    }

    if (item.legalEntityId) {
      onOpenLegalEntity?.(item.legalEntityId);
    }
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
            <div className="font-medium text-slate-900">{item.title || 'Без названия'}</div>
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
            <div>{toRuDate(item.lastViewedAt) || '—'}</div>
            {item.lastViewedByUserName ? <div className="mt-1 text-xs text-slate-500">{item.lastViewedByUserName}</div> : null}
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
        return item.nextHearingAt ? toRuDate(item.nextHearingAt) : '—';
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
      case 'activity':
        return toRuDate(getLatestCaseActivityAt(item)) || '—';
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
            <button
              type="button"
              onClick={() => void createTaskForCase(item)}
              disabled={creatingTaskCaseId === item.id}
              aria-label="Создать задачу по делу"
              title="Создать задачу по делу"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-emerald-200 text-emerald-700 hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-50">
              {creatingTaskCaseId === item.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />}
            </button>
            <button
              type="button"
              onClick={() => openOwner(item)}
              aria-label="Открыть владельца"
              title="Открыть владельца"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-slate-200 text-slate-700 hover:bg-slate-50">
              <UserRound className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => onOpenCaseFinance?.(item)}
              disabled={!onOpenCaseFinance}
              aria-label="Открыть финансы дела"
              title="Открыть финансы дела"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-indigo-200 text-indigo-700 hover:bg-indigo-50 disabled:cursor-not-allowed disabled:opacity-50">
              <WalletCards className="h-4 w-4" />
            </button>
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
          <input
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
            <div className="mt-1 text-slate-700">{toRuDate(item.dueDate) || toRuDate(item.nextHearingAt) || '—'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">Индикатор риска</div>
            <div className={`mt-1 font-semibold ${overdueCount > 0 ? 'text-rose-700' : 'text-emerald-700'}`}>{riskLabel}</div>
          </div>
        </div>

        <div className="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3">
          <div className="text-sm text-slate-600">
            <span className="font-semibold text-slate-900">{formatMoney(item.feeAmount)}</span>
            {typeof item.incomeOutstanding === 'number' ? <span className="ml-2 text-rose-700">Долг: {formatMoney(item.incomeOutstanding)}</span> : null}
          </div>
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
                onClick={() => setOwnerPickerOpen(true)}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700">
                <Plus className="h-4 w-4" /> Создать дело
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => setReloadToken((value) => value + 1)}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
              <RefreshCw className="h-4 w-4" /> Обновить
            </button>
            <button
              type="button"
              onClick={() => exportCsv()}
              disabled={items.length === 0}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
              <Download className="h-4 w-4" /> CSV
            </button>
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
            columns={CASE_COLUMN_CONFIGS.map((column) => ({ key: column.key, label: column.label }))}
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
          <div className="mb-3 inline-flex rounded-lg bg-slate-100 p-1">
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
            <select value={status} onChange={(event) => setStatus(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
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
            <select value={responsibleUserId} onChange={(event) => setResponsibleUserId(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              <option value="">Все ответственные</option>
              {responsibles.map((responsible) => <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>)}
            </select>
            <select value={priority} onChange={(event) => setPriority(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
          </div>

          <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-6">
            <input type="date" value={from} onChange={(event) => setFrom(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата с" />
            <input type="date" value={to} onChange={(event) => setTo(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата по" />
            <input type="number" min="0" value={minFee} onChange={(event) => setMinFee(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" placeholder="Гонорар от" />
            <input type="number" min="0" value={maxFee} onChange={(event) => setMaxFee(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" placeholder="Гонорар до" />
            <select value={pageSize} onChange={(event) => setPageSize(Number(event.target.value))} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm">
              {[25, 50, 100, 200].map((size) => <option key={size} value={size}>{size} строк</option>)}
            </select>
            <button
              type="button"
              onClick={() => {
                setSearch('');
                setCaseScope('all');
                setStatus('');
                setCaseType('');
                setResponsibleUserId('');
                setPriority('');
                setFrom('');
                setTo('');
                setMinFee('');
                setMaxFee('');
              }}
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 hover:bg-slate-100">
              Сбросить
            </button>
          </div>
        </div>

        {selectedIds.length > 0 ? (
          <div className="rounded-xl border border-blue-100 bg-blue-50/60 p-4 shadow-sm">
            <div className="flex flex-col gap-3 xl:flex-row xl:flex-wrap xl:items-center">
              <div className="text-sm font-semibold text-blue-900">
                Выбрано дел: {selectedIds.length}
              </div>
              <div className="grid gap-1 text-xs text-blue-900 sm:grid-cols-3">
                <span>Гонорар: {formatCurrencySmart(selectedSummary.feeAmount).full}</span>
                <span>Оплачено: {formatCurrencySmart(selectedSummary.incomePaid).full}</span>
                <span>Долг: {formatCurrencySmart(selectedSummary.incomeOutstanding).full}</span>
              </div>
              <select
                value={bulkStatus}
                onChange={(event) => setBulkStatus(event.target.value)}
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="">Не менять статус</option>
                {STATUS_OPTIONS.filter((option) => option.value).map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
              <select
                value={bulkResponsibleUserId}
                onChange={(event) => setBulkResponsibleUserId(event.target.value)}
                aria-label="Массово сменить ответственного"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="">Не менять ответственного</option>
                {responsibles.map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>
                ))}
              </select>
              <input
                value={bulkStage}
                onChange={(event) => setBulkStage(event.target.value)}
                aria-label="Массово сменить стадию"
                placeholder="Не менять стадию"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm"
              />
              <select
                value={bulkPriority}
                onChange={(event) => setBulkPriority(event.target.value)}
                aria-label="Массово сменить приоритет"
                className="h-10 rounded-lg border border-blue-100 bg-white px-3 text-sm">
                <option value="">Не менять приоритет</option>
                {PRIORITY_OPTIONS.filter((option) => option.value).map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
              <button
                type="button"
                onClick={() => void applyBulkUpdate()}
                disabled={bulkSaving || (!bulkStatus && !bulkResponsibleUserId && !bulkStage.trim() && !bulkPriority)}
                className="h-10 rounded-lg bg-blue-600 px-4 text-sm font-semibold text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50">
                {bulkSaving ? 'Сохраняем...' : 'Применить'}
              </button>
              <button
                type="button"
                onClick={() => exportCsv(selectedItems, 'cases-registry-selected.csv')}
                disabled={selectedItems.length === 0 || bulkSaving}
                className="inline-flex h-10 items-center gap-2 rounded-lg border border-blue-100 bg-white px-4 text-sm font-medium text-blue-700 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-50">
                <Download className="h-4 w-4" /> CSV выбранных
              </button>
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
                    <input
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
                    <tr key={item.id} className="align-top hover:bg-slate-50">
                      <td className="px-4 py-3">
                        <input
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
        />
      ) : null}
    </div>
  );
}
