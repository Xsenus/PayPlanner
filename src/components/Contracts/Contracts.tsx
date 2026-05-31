import {
  ArrowDown,
  ArrowUp,
  GripVertical,
  Loader2,
  Pencil,
  RotateCcw,
  Search,
  SlidersHorizontal,
  Trash2,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { useTranslation } from '../../hooks/useTranslation';
import { useContracts, type ContractsSortKey } from '../../hooks/useContracts';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useDocumentStatusSettings } from '../../hooks/useDocumentStatusSettings';
import { apiService } from '../../services/api';
import type { Client, Contract, ContractStatus } from '../../types';
import { getContractBillingModeLabel, getContractEffectiveAmount, getContractExecutionMetrics } from '../../utils/contracts';
import { toRuDate, toDateInputValue } from '../../utils/dateUtils';
import { getDocumentStatusCodes, getDocumentStatusLabels } from '../../utils/documentStatusSettings';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { buildClientLookupOptions } from '../Clients/clientLookupOptions';
import { LookupSelect } from '../ui/LookupSelect';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { StatusChangeModal, type StatusChangeOption } from '../ui/StatusChangeModal';

type SortState = { key: ContractsSortKey; direction: 'asc' | 'desc' };
const CONTRACT_STATUS_ORDER: ContractStatus[] = ['Draft', 'NotSigned', 'RequiresClientSignature', 'OnApproval', 'Approved', 'ApprovalRejected', 'NeedsCorrection', 'OnSigning', 'Concluded', 'Terminated', 'Cancelled'];

interface ContractsProps {
  onOpenContract?: (id: number) => void;
  onOpenClient?: (id: number) => void;
}

const PAGE_SIZE_OPTIONS = [10, 20, 50, 100];

const CONTRACT_COLUMNS = [
  { key: 'number', label: 'Номер договора', defaultVisible: true, defaultWidth: 180, sortKey: 'number' as const },
  { key: 'title', label: 'Название договора', defaultVisible: true, defaultWidth: 240 },
  { key: 'date', label: 'Дата договора', defaultVisible: true, defaultWidth: 150, sortKey: 'date' as const },
  { key: 'startDate', label: 'Начало действия', defaultVisible: true, defaultWidth: 170 },
  { key: 'validUntil', label: 'Окончание действия', defaultVisible: true, defaultWidth: 180 },
  { key: 'terminationDate', label: 'Дата расторжения', defaultVisible: false, defaultWidth: 180 },
  { key: 'amount', label: 'Сумма', defaultVisible: true, defaultWidth: 150, sortKey: 'amount' as const },
  { key: 'status', label: 'Статус', defaultVisible: true, defaultWidth: 160 },
  { key: 'description', label: 'Описание', defaultVisible: true, defaultWidth: 280 },
  { key: 'clients', label: 'Клиенты', defaultVisible: true, defaultWidth: 320 },
  { key: 'createdAt', label: 'Создан', defaultVisible: false, defaultWidth: 150, sortKey: 'createdAt' as const },
] as const;

type ContractColumnKey = (typeof CONTRACT_COLUMNS)[number]['key'];

const CONTRACT_COLUMN_LABELS: Record<ContractColumnKey, string> = CONTRACT_COLUMNS.reduce(
  (acc, column) => ({ ...acc, [column.key]: column.label }),
  {} as Record<ContractColumnKey, string>,
);

function formatDate(value?: string | null) {
  return value ? toRuDate(value) || value : '—';
}

function formatMoney(value?: number | null) {
  if (typeof value !== 'number') return '—';
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

export function Contracts({ onOpenContract, onOpenClient }: ContractsProps) {
  const { user } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const permissions = useRolePermissions(user?.role?.id).contracts;
  const toast = useToast();
  const { confirm } = useDialog();
  const { t } = useTranslation();
  const documentStatusSettings = useDocumentStatusSettings();

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [clientFilter, setClientFilter] = useState<string>('all');
  const [statusFilter, setStatusFilter] = useState<'all' | ContractStatus>('all');
  const [search, setSearch] = useState('');
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const [deletingId, setDeletingId] = useState<number | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [statusChangeContract, setStatusChangeContract] = useState<Contract | null>(null);
  const [statusChangeValue, setStatusChangeValue] = useState<ContractStatus>('Draft');
  const [statusChangeReason, setStatusChangeReason] = useState('');
  const [statusChangeSubmitting, setStatusChangeSubmitting] = useState(false);
  const [statusChangeError, setStatusChangeError] = useState<string | null>(null);

  const debouncedSearch = useDebouncedValue(search.trim(), 350);
  const clientIdFilter = clientFilter === 'all' ? undefined : Number(clientFilter);

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
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<ContractColumnKey>({
    storageKey: `pp.contracts_table_settings.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'contracts_table_settings',
    columns: CONTRACT_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 20,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 120,
  });

  const { contracts, loading, refreshing, error, pagination, refresh } = useContracts({
    from: from || undefined,
    to: to || undefined,
    clientId: Number.isNaN(clientIdFilter) ? undefined : clientIdFilter,
    status: statusFilter === 'all' ? undefined : statusFilter,
    search: debouncedSearch || undefined,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  useEffect(() => {
    setPage(1);
  }, [from, to, clientFilter, statusFilter, debouncedSearch, sortState, pageSize]);

  useEffect(() => {
    void apiService
      .getClientsV1({ sortBy: 'name', sortDir: 'asc' })
      .then((data) => setClients(data ?? []))
      .catch(() => setClients([]));
  }, []);

  useEffect(() => {
    if (!error) return;
    toast.error(error);
  }, [error, toast]);

  const totalPages = useMemo(
    () => Math.max(1, Math.ceil((pagination.total || 0) / (pagination.pageSize || pageSize || 1))),
    [pageSize, pagination.pageSize, pagination.total],
  );
  const clientOptions = useMemo(
    () => buildClientLookupOptions(clients),
    [clients],
  );

  const columnsByKey = useMemo(
    () => Object.fromEntries(CONTRACT_COLUMNS.map((column) => [column.key, column])) as Record<ContractColumnKey, (typeof CONTRACT_COLUMNS)[number]>,
    [],
  );

  const visibleColumnsOrdered = useMemo(
    () => orderedVisibleColumns.map((key) => columnsByKey[key]).filter(Boolean),
    [columnsByKey, orderedVisibleColumns],
  );

  const defaultContractStatusLabels = useMemo<Record<ContractStatus, string>>(
    () => ({
      Draft: 'Черновик',
      NotSigned: 'Не подписан',
      RequiresClientSignature: 'Требуется подпись клиента',
      OnApproval: 'На согласовании',
      Approved: 'Согласован',
      ApprovalRejected: 'Отказ в согласовании',
      NeedsCorrection: 'Требует исправления',
      OnSigning: 'На подписании',
      Concluded: 'Подписан',
      Terminated: 'Расторгнут',
      Cancelled: 'Аннулирован',
    }),
    [],
  );

  const contractStatusLabels = useMemo(
    () => getDocumentStatusLabels(documentStatusSettings, 'contract', defaultContractStatusLabels),
    [defaultContractStatusLabels, documentStatusSettings],
  );

  const contractStatusClasses = useMemo<Record<ContractStatus, string>>(
    () => ({
      Draft: 'bg-slate-100 text-slate-700 border border-slate-200',
      NotSigned: 'bg-slate-100 text-slate-700 border border-slate-200',
      RequiresClientSignature: 'bg-amber-100 text-amber-800 border border-amber-200',
      OnApproval: 'bg-cyan-100 text-cyan-700 border border-cyan-200',
      Approved: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
      ApprovalRejected: 'bg-rose-100 text-rose-700 border border-rose-200',
      NeedsCorrection: 'bg-orange-100 text-orange-700 border border-orange-200',
      OnSigning: 'bg-violet-100 text-violet-700 border border-violet-200',
      Concluded: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
      Terminated: 'bg-orange-100 text-orange-700 border border-orange-200',
      Cancelled: 'bg-rose-100 text-rose-700 border border-rose-200',
    }),
    [],
  );
  const contractStatusDescriptions = useMemo<Record<ContractStatus, string>>(
    () => ({
      Draft: 'Договор вернется в черновик, даты подписания и закрытия будут очищены.',
      NotSigned: 'Договор будет отмечен как неподписанный.',
      RequiresClientSignature: 'Договор будет ожидать подпись клиента.',
      OnApproval: 'Договор будет отправлен на внутреннее согласование.',
      Approved: 'Договор будет отмечен как согласованный.',
      ApprovalRejected: 'В журнал попадет отказ в согласовании договора.',
      NeedsCorrection: 'Договор будет возвращен на исправление.',
      OnSigning: 'Договор будет отмечен как отправленный на подписание.',
      Concluded: 'Договор будет отмечен как подписанный.',
      Terminated: 'Договор будет расторгнут сегодняшней датой.',
      Cancelled: 'Договор будет аннулирован сегодняшней датой.',
    }),
    [],
  );

  const contractStatusCodes = useMemo(
    () => getDocumentStatusCodes(documentStatusSettings, 'contract', CONTRACT_STATUS_ORDER),
    [documentStatusSettings],
  );

  const contractStatusOptions: StatusChangeOption<ContractStatus>[] = useMemo(
    () => contractStatusCodes.map((status) => ({
      value: status,
      label: contractStatusLabels[status],
      description: contractStatusDescriptions[status],
      className: contractStatusClasses[status],
    })),
    [contractStatusClasses, contractStatusCodes, contractStatusDescriptions, contractStatusLabels],
  );

  const handleSort = (key: ContractsSortKey) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
  };

  const handleResetFilters = () => {
    setFrom('');
    setTo('');
    setClientFilter('all');
    setStatusFilter('all');
    setSearch('');
  };

  const handleDelete = useCallback(
    async (contract: Contract) => {
      if (!permissions.canDelete) return;

      const confirmed = await confirm({
        title: 'Подтверждение удаления',
        description: `Удалить договор «${contract.number}»?`,
        confirmLabel: 'Удалить',
        cancelLabel: 'Отмена',
        variant: 'danger',
      });
      if (!confirmed) return;

      setDeletingId(contract.id);
      try {
        await apiService.deleteContract(contract.id);
        toast.success('Договор удален.');
        if (contracts.length === 1 && page > 1) {
          setPage((current) => Math.max(1, current - 1));
        } else {
          await refresh();
        }
      } catch (err) {
        toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить договор.'));
      } finally {
        setDeletingId(null);
      }
    },
    [confirm, contracts.length, page, permissions.canDelete, refresh, toast],
  );

  const openContractStatusChange = useCallback((contract: Contract) => {
    if (!permissions.canEdit) return;
    setStatusChangeContract(contract);
    setStatusChangeValue(contract.status ?? 'Draft');
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [permissions.canEdit]);

  const closeContractStatusChange = useCallback(() => {
    if (statusChangeSubmitting) return;
    setStatusChangeContract(null);
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [statusChangeSubmitting]);

  const submitContractStatusChange = useCallback(async () => {
    if (!permissions.canEdit || !statusChangeContract) return;

    try {
      setStatusChangeSubmitting(true);
      setStatusChangeError(null);
      await apiService.updateContractStatus(statusChangeContract.id, statusChangeValue, statusChangeReason.trim() || null);
      setStatusChangeContract(null);
      setStatusChangeReason('');
      await refresh();
      toast.success('Статус договора обновлен.');
    } catch (err) {
      setStatusChangeError(extractDisplayErrorMessage(err, 'Не удалось изменить статус договора.'));
    } finally {
      setStatusChangeSubmitting(false);
    }
  }, [permissions.canEdit, refresh, statusChangeContract, statusChangeReason, statusChangeValue, toast]);

  const renderSortIcon = (sortKey?: ContractsSortKey) => {
    if (!sortKey || sortState.key !== sortKey) {
      return null;
    }

    return sortState.direction === 'asc' ? (
      <ArrowUp className="h-4 w-4 text-blue-600" />
    ) : (
      <ArrowDown className="h-4 w-4 text-blue-600" />
    );
  };

  const renderCell = (contract: Contract, columnKey: ContractColumnKey) => {
    switch (columnKey) {
      case 'number':
        return (
          <button
            type="button"
            className="font-medium text-emerald-700 transition-colors hover:text-emerald-800 hover:underline"
            onClick={(event) => {
              event.stopPropagation();
              onOpenContract?.(contract.id);
            }}
          >
            {contract.number}
          </button>
        );
      case 'title':
        return contract.title?.trim() ? (
          <div className="truncate text-slate-700" title={contract.title}>
            {contract.title}
          </div>
        ) : (
          '—'
        );
      case 'date':
        return formatDate(contract.date);
      case 'startDate':
        return formatDate(contract.startDate);
      case 'validUntil':
        return contract.isOpenEnded ? 'До исполнения обязательств' : formatDate(contract.validUntil);
      case 'terminationDate':
        return formatDate(contract.terminationDate);
      case 'amount':
        return (
          <div className="flex flex-col">
            <span className="font-medium text-slate-900">{formatMoney(getContractEffectiveAmount(contract))}</span>
            <span className="text-xs text-slate-500">
              {getContractBillingModeLabel(contract.billingMode)}
              {contract.billingMode === 'Fixed' ? ` · ${getContractExecutionMetrics(contract).label}` : ''}
            </span>
          </div>
        );
      case 'status': {
        const status = contract.status ?? 'Draft';
        return (
          permissions.canEdit ? (
            <button
              type="button"
              onClick={(event) => {
                event.stopPropagation();
                openContractStatusChange(contract);
              }}
              className="inline-flex rounded-full transition hover:brightness-95 focus:outline-none focus:ring-2 focus:ring-blue-200"
              title="Изменить статус договора"
            >
              <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${contractStatusClasses[status]}`}>
                {contractStatusLabels[status]}
              </span>
            </button>
          ) : (
            <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${contractStatusClasses[status]}`}>
              {contractStatusLabels[status]}
            </span>
          )
        );
      }
      case 'description':
        return contract.description?.trim() ? (
          <div className="truncate text-slate-700" title={contract.description}>
            {contract.description}
          </div>
        ) : (
          '—'
        );
      case 'clients':
        return (contract.clients.length + (contract.legalEntities?.length ?? 0)) > 0 ? (
          <div className="flex flex-wrap gap-2">
            {contract.clients.map((client) => (
              <button
                key={client.id}
                type="button"
                onClick={(event) => {
                  event.stopPropagation();
                  onOpenClient?.(client.id);
                }}
                className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-3 py-1 text-xs text-blue-700 transition-colors hover:border-blue-200 hover:bg-blue-50"
              >
                <span className="max-w-[180px] truncate">{client.name}</span>
                {client.clientStatus ? <ClientStatusBadge status={client.clientStatus} /> : null}
              </button>
            ))}
            {(contract.legalEntities ?? []).map((entity) => (
              <span
                key={`legal:${entity.id}`}
                className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-3 py-1 text-xs text-slate-700"
              >
                <span className="max-w-[180px] truncate">{entity.shortName || entity.fullName || `Юрлицо #${entity.id}`}</span>
                <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[11px] uppercase text-slate-500">Юрлицо</span>
              </span>
            ))}
          </div>
        ) : (
          '—'
        );
      case 'createdAt':
        return formatDate(contract.createdAt);
      default:
        return '—';
    }
  };

  const statusChangeCurrentValue = statusChangeContract?.status ?? 'Draft';
  const statusChangeTargetLabel = contractStatusLabels[statusChangeValue] ?? statusChangeValue;
  const statusChangeSubject = statusChangeContract
    ? `Договор №${statusChangeContract.number}${statusChangeContract.title ? ` · ${statusChangeContract.title}` : ''}`
    : 'Договор не выбран';
  const statusChangeConsequences = [
    `Статус договора изменится с «${contractStatusLabels[statusChangeCurrentValue]}» на «${statusChangeTargetLabel}».`,
    statusChangeValue === 'Terminated'
      ? 'Дата расторжения будет установлена сегодняшним днем.'
      : statusChangeValue === 'Cancelled'
        ? 'Дата аннулирования будет установлена сегодняшним днем.'
        : 'Связанные даты подписания и закрытия будут синхронизированы с выбранным статусом.',
    'После подтверждения список договоров обновится.',
  ];

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 py-8 sm:px-6 lg:px-8">
        <div className="mb-8 flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div className="space-y-2">
            <h1 className="text-3xl font-bold text-slate-900">{t('contracts') ?? 'Договоры'}</h1>
            <p className="max-w-2xl text-sm text-slate-600">
              {t('contractsSubtitle') ?? 'Управляйте договорами и привязывайте их к клиентам.'}
            </p>
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium shadow-sm transition-colors ${
                settingsOpen
                  ? 'border-blue-200 bg-blue-50 text-blue-700'
                  : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
              }`}
            >
              <SlidersHorizontal className="h-4 w-4" />
              {t('tableSettings') ?? 'Таблица'}
            </button>
            <button
              type="button"
              onClick={() => void refresh()}
              disabled={refreshing}
              className={`inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 ${
                refreshing ? 'cursor-wait opacity-70' : ''
              }`}
            >
              <RotateCcw className={refreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
              {t('update') ?? 'Обновить'}
            </button>
            {permissions.canCreate ? (
              <button
                type="button"
                className="inline-flex items-center rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700"
                onClick={() => setActiveTab('contractCreate')}
              >
                {t('contractsCreate') ?? t('create') ?? 'Новый договор'}
              </button>
            ) : null}
          </div>
        </div>

        <div className="mb-6 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-5">
            <label className="flex flex-col gap-1 text-sm text-slate-700">
              {t('contractsFrom') ?? 'С даты'}
              <input
                type="date"
                value={toDateInputValue(from)}
                onChange={(event) => setFrom(event.target.value)}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-700">
              {t('contractsTo') ?? 'По дату'}
              <input
                type="date"
                value={toDateInputValue(to)}
                onChange={(event) => setTo(event.target.value)}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
            </label>
            <LookupSelect
              label={t('contractsClientFilter') ?? t('client') ?? 'Клиент'}
              value={clientFilter === 'all' ? '' : clientFilter}
              options={clientOptions}
              onChange={(value) => setClientFilter(value || 'all')}
              placeholder={t('allClients') ?? 'Все клиенты'}
              title="Выбор клиента"
              dictionaryLabel="Открыть справочник"
              searchPlaceholder="Поиск по ФИО, компании, телефону или email"
              emptyOptionLabel={t('allClients') ?? 'Все клиенты'}
              emptyOptionDescription="Показать договоры всех клиентов"
              buttonClassName="border-gray-300 text-slate-700 focus:border-blue-500"
              noOptionsText="Клиенты не найдены"
            />
            <label className="flex flex-col gap-1 text-sm text-slate-700">
              Статус
              <select
                value={statusFilter}
                onChange={(event) => setStatusFilter(event.target.value === 'all' ? 'all' : (event.target.value as ContractStatus))}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              >
                <option value="all">Все статусы</option>
                {contractStatusCodes.map((status) => (
                  <option key={status} value={status}>
                    {contractStatusLabels[status]}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-700">
              {t('search') ?? 'Поиск'}
              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  className="w-full rounded-lg border border-gray-300 py-2 pl-9 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  placeholder={t('contractsSearchPlaceholder') ?? 'Поиск по номеру, названию, описанию или клиенту'}
                />
              </div>
            </label>
          </div>
          <div className="mt-4 flex justify-end">
            <button
              type="button"
              onClick={handleResetFilters}
              className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 transition-colors hover:bg-gray-50"
            >
              <RotateCcw className="h-4 w-4" />
              {t('paymentsFiltersReset') ?? 'Сбросить фильтры'}
            </button>
          </div>
        </div>

        {settingsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={CONTRACT_COLUMNS.map((column) => ({
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
                  <span>{t('rowsPerPage') ?? 'Строк на странице'}</span>
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

        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table
              className="w-full min-w-[1180px] table-fixed"
              style={{ width: getTableWidth(120, 1180), minWidth: '100%' }}
            >
              <colgroup>
                {orderedVisibleColumns.map((columnKey) => (
                  <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                ))}
                <col style={{ width: 120 }} />
              </colgroup>
              <thead className="border-b border-slate-200 bg-slate-50">
                <tr>
                  {visibleColumnsOrdered.map((column) => (
                    <th
                      key={column.key}
                      style={{ width: getColumnWidth(column.key) }}
                      className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wider ${
                        draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : 'text-slate-700'
                      }`}
                      onDragOver={handleColumnDragOver}
                      onDrop={(event) => handleColumnDrop(column.key, event)}
                    >
                      <button
                        type="button"
                        draggable
                        onDragStart={(event) => handleColumnDragStart(column.key, event)}
                        onDragEnd={handleColumnDragEnd}
                        className="absolute left-2 top-1/2 z-10 inline-flex -translate-y-1/2 cursor-grab rounded p-1 text-slate-400 active:cursor-grabbing"
                        title="Перетащите, чтобы изменить порядок"
                        aria-label={`Перетащите колонку ${CONTRACT_COLUMN_LABELS[column.key]}, чтобы изменить порядок`}
                      >
                        <GripVertical className="h-4 w-4" />
                      </button>
                      <div className="flex items-center justify-between gap-2">
                        {column.sortKey ? (
                          <button
                            type="button"
                            onClick={() => handleSort(column.sortKey)}
                            className="inline-flex min-w-0 items-center gap-2 text-left transition-colors hover:text-blue-700"
                          >
                            <div
                              className="inline-flex min-w-0 items-center gap-2 pl-8"
                              draggable
                              onDragStart={(event) => handleColumnDragStart(column.key, event)}
                              onDragEnd={handleColumnDragEnd}
                              title="Перетащите, чтобы изменить порядок"
                            >
                              <span className="truncate">{CONTRACT_COLUMN_LABELS[column.key]}</span>
                            </div>
                            {renderSortIcon(column.sortKey)}
                          </button>
                        ) : (
                          <div
                            className="inline-flex min-w-0 items-center gap-2 pl-8"
                            draggable
                            onDragStart={(event) => handleColumnDragStart(column.key, event)}
                            onDragEnd={handleColumnDragEnd}
                            title="Перетащите, чтобы изменить порядок"
                          >
                            <span className="truncate">{CONTRACT_COLUMN_LABELS[column.key]}</span>
                          </div>
                        )}
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${CONTRACT_COLUMN_LABELS[column.key]}`}
                        className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                        onMouseDown={(event) => handleResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wider text-slate-700">
                    {t('actions') ?? 'Действия'}
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {loading ? (
                  <tr>
                    <td colSpan={visibleColumnsOrdered.length + 1} className="px-4 py-12 text-center text-slate-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t('loading') ?? 'Загрузка...'}
                      </div>
                    </td>
                  </tr>
                ) : error ? (
                  <tr>
                    <td colSpan={visibleColumnsOrdered.length + 1} className="px-4 py-12 text-center text-rose-600">
                      {error}
                    </td>
                  </tr>
                ) : contracts.length === 0 ? (
                  <tr>
                    <td colSpan={visibleColumnsOrdered.length + 1} className="px-4 py-12 text-center text-slate-500">
                      {t('contractsEmptyTitle') ?? 'Договоры отсутствуют'}
                    </td>
                  </tr>
                ) : (
                  contracts.map((contract) => (
                    <tr
                      key={contract.id}
                      className="cursor-pointer transition-colors hover:bg-slate-50"
                      onClick={() => onOpenContract?.(contract.id)}
                    >
                      {visibleColumnsOrdered.map((column) => (
                        <td
                          key={column.key}
                          style={{ width: getColumnWidth(column.key) }}
                          className="max-w-0 overflow-hidden px-4 py-4 align-top text-sm text-slate-700"
                        >
                          {renderCell(contract, column.key)}
                        </td>
                      ))}
                      <td className="px-4 py-4">
                        <div className="flex items-center justify-end gap-2">
                          {permissions.canEdit ? (
                            <button
                              type="button"
                              onClick={(event) => {
                                event.stopPropagation();
                                onOpenContract?.(contract.id);
                              }}
                              className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
                              title={t('edit') ?? 'Редактировать'}
                              aria-label={t('edit') ?? 'Редактировать'}
                            >
                              <Pencil className="h-4 w-4" />
                            </button>
                          ) : null}
                          {permissions.canDelete ? (
                            <button
                              type="button"
                              onClick={(event) => {
                                event.stopPropagation();
                                void handleDelete(contract);
                              }}
                              className="rounded-lg p-2 text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700"
                              title={t('delete') ?? 'Удалить'}
                              aria-label={t('delete') ?? 'Удалить'}
                            >
                              {deletingId === contract.id ? (
                                <Loader2 className="h-4 w-4 animate-spin" />
                              ) : (
                                <Trash2 className="h-4 w-4" />
                              )}
                            </button>
                          ) : null}
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <TablePagination
            page={page}
            totalPages={totalPages}
            totalItems={pagination.total || 0}
            pageSize={pageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            onPageChange={setPage}
            onPageSizeChange={setPageSize}
            recordsLabel={t('recordsFound') ?? 'Найдено записей'}
            pageSizeLabel={t('rowsPerPage') ?? 'Строк на странице'}
            previousLabel={t('previous') ?? 'Назад'}
            nextLabel={t('next') ?? 'Вперед'}
          />
        </div>

        <StatusChangeModal<ContractStatus>
          open={statusChangeContract !== null}
          title="Смена статуса договора"
          subject={statusChangeSubject}
          currentLabel={contractStatusLabels[statusChangeCurrentValue]}
          currentValue={statusChangeCurrentValue}
          value={statusChangeValue}
          options={contractStatusOptions}
          onChange={(value) => {
            setStatusChangeValue(value);
            if (statusChangeError) {
              setStatusChangeError(null);
            }
          }}
          reason={statusChangeReason}
          reasonRequired={Boolean(user?.requiresFinancialActionReason)}
          onReasonChange={(value) => {
            setStatusChangeReason(value);
            if (statusChangeError) {
              setStatusChangeError(null);
            }
          }}
          consequences={statusChangeConsequences}
          onCancel={closeContractStatusChange}
          onConfirm={submitContractStatusChange}
          confirmLabel={`Изменить статус договора на «${statusChangeTargetLabel}»`}
          error={statusChangeError}
          loading={statusChangeSubmitting}
        />
      </div>
    </div>
  );
}
