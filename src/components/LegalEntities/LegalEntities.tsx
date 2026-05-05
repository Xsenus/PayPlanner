import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertCircle,
  Building2,
  Edit,
  Eye,
  Factory,
  GripVertical,
  LayoutGrid,
  List,
  Loader2,
  Mail,
  MapPin,
  Phone,
  Plus,
  Search,
  SlidersHorizontal,
  Trash2,
  Users,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { apiService } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { LegalEntityDetail, LegalEntityInput, LegalEntitySummary } from '../../types';
import { LegalEntityModal } from './LegalEntityModal';

const PAGE_SIZE_OPTIONS = [12, 24, 48];

const LEGAL_ENTITY_COLUMNS = [
  { key: 'shortName', label: 'Название', defaultVisible: true, defaultWidth: 220 },
  { key: 'requisites', label: 'Реквизиты', defaultVisible: true, defaultWidth: 220 },
  { key: 'director', label: 'Директор', defaultVisible: false, defaultWidth: 180 },
  { key: 'contacts', label: 'Контакты', defaultVisible: true, defaultWidth: 220 },
  { key: 'address', label: 'Адрес', defaultVisible: false, defaultWidth: 280 },
  { key: 'clients', label: 'Клиенты', defaultVisible: true, defaultWidth: 180 },
  { key: 'createdAt', label: 'Создано', defaultVisible: false, defaultWidth: 140 },
] as const;

type LegalEntityColumnKey = (typeof LEGAL_ENTITY_COLUMNS)[number]['key'];

const LEGAL_ENTITY_COLUMN_LABELS: Record<LegalEntityColumnKey, string> = LEGAL_ENTITY_COLUMNS.reduce(
  (acc, column) => ({ ...acc, [column.key]: column.label }),
  {} as Record<LegalEntityColumnKey, string>,
);

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function highlight(text: string | null | undefined, query: string) {
  if (!text) return null;
  const normalizedQuery = query.trim();
  if (!normalizedQuery) return text;

  const regex = new RegExp(`(${escapeRegExp(normalizedQuery)})`, 'gi');
  const parts = text.split(regex);

  return parts.map((part, index) =>
    index % 2 === 1 ? (
      <mark key={`${part}-${index}`} className="rounded bg-yellow-200 px-1">
        {part}
      </mark>
    ) : (
      part
    ),
  );
}

function formatDate(value?: string | null) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString('ru-RU');
}

export function LegalEntities() {
  const { t } = useTranslation();
  const { user } = useAuth();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const legalPermissions = permissions.legalEntities;
  const { confirm } = useDialog();

  const [items, setItems] = useState<LegalEntitySummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [viewMode, setViewMode] = useState<'cards' | 'table'>('cards');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [detailPageOpen, setDetailPageOpen] = useState(false);
  const [editing, setEditing] = useState<LegalEntityDetail | null>(null);
  const [modalBusy, setModalBusy] = useState(false);
  const [readOnlyMode, setReadOnlyMode] = useState(false);

  const debouncedSearch = useDebouncedValue(search, 300);

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
  } = usePersistedTableSettings<LegalEntityColumnKey>({
    storageKey: 'pp.legal_entities_table_settings.v1',
    settingsKey: 'legal_entities_table_settings',
    columns: LEGAL_ENTITY_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 12,
    minPageSize: 12,
    maxPageSize: 96,
    minColumnWidth: 140,
  });

  const fetchLegalEntities = useCallback(
    async (targetPage = page, withLoading = true) => {
      try {
        if (withLoading) setLoading(true);
        setError(null);

        const response = await apiService.getLegalEntitiesPaged({
          search: debouncedSearch.trim() || undefined,
          page: targetPage,
          pageSize,
        });

        if (targetPage > 1 && response.items.length === 0 && response.total > 0) {
          setPage(targetPage - 1);
          return;
        }

        setItems(response.items ?? []);
        setTotal(response.total ?? 0);
      } catch (err) {
        const message = extractDisplayErrorMessage(err, 'Не удалось загрузить юридические лица');
        setItems([]);
        setTotal(0);
        setError(message);
        toast.error(message);
      } finally {
        if (withLoading) setLoading(false);
      }
    },
    [debouncedSearch, page, pageSize, toast],
  );

  useEffect(() => {
    if (!detailPageOpen) {
      void fetchLegalEntities(page);
    }
  }, [detailPageOpen, fetchLegalEntities, page]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, pageSize]);

  const totalPages = useMemo(
    () => Math.max(1, Math.ceil((total || 0) / (pageSize || 1))),
    [pageSize, total],
  );

  const canCreate = legalPermissions.canCreate;
  const canEdit = legalPermissions.canEdit;
  const canDelete = legalPermissions.canDelete;

  const allowDetailEdit = editing ? !readOnlyMode && canEdit : false;
  const allowDetailDelete = editing ? !readOnlyMode && canDelete : false;

  const refreshAfterMutation = useCallback(
    async (remainingRows: number) => {
      const nextPage = remainingRows === 0 && page > 1 ? page - 1 : page;
      if (nextPage !== page) {
        setPage(nextPage);
      } else {
        await fetchLegalEntities(nextPage, false);
      }
    },
    [fetchLegalEntities, page],
  );

  const openDetailPage = async (id: number, allowEditing: boolean) => {
    try {
      setModalBusy(true);
      const detail = await apiService.getLegalEntity(id);
      setEditing(detail);
      setReadOnlyMode(!allowEditing);
      setDetailPageOpen(true);
    } catch (err) {
      toast.error(
        extractDisplayErrorMessage(err, 'Не удалось загрузить данные юридического лица'),
      );
    } finally {
      setModalBusy(false);
    }
  };

  const handleCreate = () => {
    if (!canCreate) return;
    setEditing(null);
    setReadOnlyMode(false);
    setDetailPageOpen(true);
  };

  const handleSubmit = async (payload: LegalEntityInput) => {
    const saved = editing
      ? await apiService.updateLegalEntity(editing.id, payload)
      : await apiService.createLegalEntity(payload);

    setEditing(saved);
    setReadOnlyMode(false);
    toast.success(editing ? 'Юр. лицо обновлено' : 'Юр. лицо создано');

    if (editing) {
      await fetchLegalEntities(page, false);
    } else if (page === 1) {
      await fetchLegalEntities(1, false);
    } else {
      setPage(1);
    }

    return saved;
  };

  const handleDeleteFromDetail =
    editing && allowDetailDelete
      ? async () => {
          await apiService.deleteLegalEntity(editing.id);
          toast.success('Юр. лицо удалено');
          setEditing(null);
          setReadOnlyMode(false);
          setDetailPageOpen(false);
          await refreshAfterMutation(Math.max(0, items.length - 1));
        }
      : undefined;

  const handleDeleteDirect = async (entity: LegalEntitySummary) => {
    if (!canDelete) return;

    const approved = await confirm({
      title: 'Подтверждение удаления',
      description: `Удалить юридическое лицо "${entity.shortName}"?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    try {
      await apiService.deleteLegalEntity(entity.id);
      toast.success('Юр. лицо удалено');
      await refreshAfterMutation(Math.max(0, items.length - 1));
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить юридическое лицо'));
    }
  };

  const renderTableCell = (entity: LegalEntitySummary, columnKey: LegalEntityColumnKey) => {
    switch (columnKey) {
      case 'shortName':
        return (
          <div className="min-w-0">
            <div className="truncate font-medium text-slate-900">
              {highlight(entity.shortName, debouncedSearch)}
            </div>
            {entity.fullName ? (
              <div className="mt-1 text-xs text-slate-500">
                {highlight(entity.fullName, debouncedSearch)}
              </div>
            ) : null}
          </div>
        );
      case 'requisites':
        return (
          <div className="space-y-1 text-xs text-slate-600">
            {entity.inn ? <div>ИНН: {highlight(entity.inn, debouncedSearch)}</div> : null}
            {entity.kpp ? <div>КПП: {highlight(entity.kpp, debouncedSearch)}</div> : null}
            {entity.ogrn ? <div>ОГРН: {highlight(entity.ogrn, debouncedSearch)}</div> : null}
          </div>
        );
      case 'director':
        return <span className="text-slate-600">{entity.director || '—'}</span>;
      case 'contacts':
        return (
          <div className="space-y-1 text-xs text-slate-600">
            {entity.phone ? (
              <div className="flex items-center gap-1">
                <Phone className="h-3.5 w-3.5 text-slate-400" />
                <span>{entity.phone}</span>
              </div>
            ) : null}
            {entity.email ? (
              <div className="flex items-center gap-1">
                <Mail className="h-3.5 w-3.5 text-slate-400" />
                <span className="break-all">{entity.email}</span>
              </div>
            ) : null}
            {!entity.phone && !entity.email ? '—' : null}
          </div>
        );
      case 'address':
        return (
          <div className="text-sm text-slate-600">
            {entity.address ? highlight(entity.address, debouncedSearch) : '—'}
          </div>
        );
      case 'clients':
        return (
          <div className="space-y-1">
            <div className="text-sm font-medium text-slate-700">{entity.clientsCount}</div>
            {(entity.clients ?? []).length > 0 ? (
              <div className="flex flex-wrap gap-1">
                {(entity.clients ?? []).slice(0, 3).map((client) => (
                  <span
                    key={client.id}
                    className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-600"
                  >
                    {client.name}
                  </span>
                ))}
              </div>
            ) : null}
          </div>
        );
      case 'createdAt':
        return <span className="text-sm text-slate-600">{formatDate(entity.createdAt)}</span>;
      default:
        return null;
    }
  };

  return (
    <div className="p-6">
      <div className="mb-6 flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-gray-900">
            {t('legalEntitiesTitle') || 'Юридические лица'}
          </h1>
          <p className="text-sm text-gray-500">
            {t('legalEntitiesSubtitle') ||
              'Реестр юридических лиц с серверной пагинацией и единым табличным видом.'}
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <div className="flex rounded-xl border border-gray-200 p-1 shadow-sm">
            <button
              type="button"
              onClick={() => setViewMode('cards')}
              className={`flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium transition ${
                viewMode === 'cards'
                  ? 'bg-blue-600 text-white shadow'
                  : 'text-gray-600 hover:bg-gray-100'
              }`}
            >
              <LayoutGrid size={16} />
              {t('legalEntityViewCards') || 'Карточки'}
            </button>
            <button
              type="button"
              onClick={() => setViewMode('table')}
              className={`flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium transition ${
                viewMode === 'table'
                  ? 'bg-blue-600 text-white shadow'
                  : 'text-gray-600 hover:bg-gray-100'
              }`}
            >
              <List size={16} />
              {t('legalEntityViewTable') || 'Таблица'}
            </button>
          </div>

          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
            <input
              type="text"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder={t('legalEntitySearchPlaceholder') || 'Поиск по названию, ИНН, директору'}
              className="w-72 rounded-xl border border-gray-200 px-3 py-2 pl-9 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>

          {viewMode === 'table' ? (
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className={`inline-flex items-center gap-2 rounded-xl border px-3 py-2 text-sm font-medium transition-colors ${
                settingsOpen
                  ? 'border-blue-200 bg-blue-50 text-blue-700'
                  : 'border-gray-200 bg-white text-gray-600 hover:bg-gray-50'
              }`}
            >
              <SlidersHorizontal className="h-4 w-4" />
              Колонки
            </button>
          ) : null}

          <button
            type="button"
            onClick={handleCreate}
            disabled={!canCreate}
            className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300"
          >
            <Plus size={16} />
            {t('addLegalEntity') || 'Добавить юр. лицо'}
          </button>
        </div>
      </div>

      {settingsOpen && viewMode === 'table' ? (
        <div className="mb-6">
          <TableColumnSettings
            columns={LEGAL_ENTITY_COLUMNS.map((column) => ({
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

      {error ? (
        <div className="mb-4 flex items-center gap-2 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          <AlertCircle size={18} />
          <span>{error}</span>
        </div>
      ) : null}

      {(loading || modalBusy) && !detailPageOpen ? (
        <div className="mb-4 flex items-center gap-2 rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm text-blue-700">
          <Loader2 className="h-4 w-4 animate-spin" />
          <span>{t('loading') || 'Загрузка данных...'}</span>
        </div>
      ) : null}

      {detailPageOpen ? (
        <LegalEntityModal
          isOpen={detailPageOpen}
          onClose={() => {
            setDetailPageOpen(false);
            setEditing(null);
            setReadOnlyMode(false);
          }}
          onSubmit={handleSubmit}
          onDelete={handleDeleteFromDetail}
          initial={editing}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit={editing ? allowDetailEdit : canCreate}
          allowDelete={allowDetailDelete}
          mode="page"
          onBack={() => {
            setDetailPageOpen(false);
            setEditing(null);
            setReadOnlyMode(false);
          }}
        />
      ) : items.length === 0 && !loading ? (
        <div className="rounded-2xl border border-dashed border-gray-300 bg-white p-10 text-center text-gray-500">
          <div className="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-gray-100">
            <Factory className="h-8 w-8 text-gray-400" />
          </div>
          <h3 className="mt-4 text-lg font-semibold text-gray-900">
            {t('legalEntitiesEmptyTitle') || 'Юридические лица не найдены'}
          </h3>
          <p className="mt-2 text-sm text-gray-500">
            {t('legalEntitiesEmptyDescription') ||
              'Измените параметры поиска или добавьте новое юридическое лицо.'}
          </p>
        </div>
      ) : viewMode === 'cards' ? (
        <>
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {items.map((entity) => (
              <div
                key={entity.id}
                className="group rounded-2xl border border-gray-200 bg-white p-5 shadow-sm transition hover:border-blue-400 hover:shadow-lg"
              >
                <div className="flex items-start justify-between">
                  <div>
                    <button
                      type="button"
                      onClick={() => void openDetailPage(entity.id, canEdit)}
                      className="text-left text-lg font-semibold text-gray-900 transition hover:text-blue-600"
                    >
                      {highlight(entity.shortName, debouncedSearch)}
                    </button>
                    {entity.fullName ? (
                      <p className="mt-1 text-sm text-gray-500">
                        {highlight(entity.fullName, debouncedSearch)}
                      </p>
                    ) : null}
                  </div>
                  <div className="rounded-full bg-blue-50 p-3 text-blue-500">
                    <Building2 size={18} />
                  </div>
                </div>

                <div className="mt-4 space-y-2 text-sm text-gray-600">
                  <div className="flex flex-wrap gap-x-4 gap-y-1">
                    {entity.inn ? <span>ИНН: {highlight(entity.inn, debouncedSearch)}</span> : null}
                    {entity.kpp ? <span>КПП: {highlight(entity.kpp, debouncedSearch)}</span> : null}
                    {entity.ogrn ? <span>ОГРН: {highlight(entity.ogrn, debouncedSearch)}</span> : null}
                  </div>
                  {entity.address ? (
                    <div className="flex items-center gap-2">
                      <MapPin size={14} className="text-gray-400" />
                      <span>{highlight(entity.address, debouncedSearch)}</span>
                    </div>
                  ) : null}
                  <div className="flex flex-wrap gap-3 text-xs text-gray-500">
                    {entity.phone ? (
                      <span className="flex items-center gap-1">
                        <Phone size={12} /> {entity.phone}
                      </span>
                    ) : null}
                    {entity.email ? (
                      <span className="flex items-center gap-1">
                        <Mail size={12} /> {entity.email}
                      </span>
                    ) : null}
                    {entity.director ? <span>Директор: {entity.director}</span> : null}
                  </div>
                </div>

                <div className="mt-4 rounded-xl bg-gray-50 p-3 text-sm">
                  <div className="flex items-center justify-between text-gray-600">
                    <div className="flex items-center gap-2 font-medium text-gray-700">
                      <Users size={16} className="text-blue-500" />
                      Клиенты: {entity.clientsCount}
                    </div>
                    <button
                      type="button"
                      onClick={() => void openDetailPage(entity.id, canEdit)}
                      className="rounded-md p-1 text-blue-600 hover:bg-blue-50 hover:text-blue-700"
                    >
                      <Eye size={14} />
                    </button>
                  </div>
                  {(entity.clients ?? []).length > 0 ? (
                    <div className="mt-2 flex flex-wrap gap-1 text-xs text-gray-500">
                      {(entity.clients ?? []).slice(0, 4).map((client) => (
                        <span key={client.id} className="rounded-full bg-white px-2 py-0.5 shadow">
                          {client.name}
                        </span>
                      ))}
                    </div>
                  ) : null}
                </div>

                <div className="mt-4 flex items-center justify-between text-xs text-gray-400">
                  <span>Создано: {formatDate(entity.createdAt)}</span>
                  {entity.updatedAt ? <span>Обновлено: {formatDate(entity.updatedAt)}</span> : null}
                </div>

                <div className="mt-4 flex items-center gap-2 border-t border-gray-100 pt-3 text-sm">
                  <button
                    type="button"
                    onClick={() => void openDetailPage(entity.id, false)}
                    className="inline-flex items-center rounded-lg border border-gray-200 p-2 text-gray-600 hover:border-blue-400 hover:text-blue-600"
                  >
                    <Eye size={14} />
                  </button>
                  <button
                    type="button"
                    onClick={() => void openDetailPage(entity.id, true)}
                    disabled={!canEdit}
                    className="inline-flex items-center rounded-lg border border-blue-200 bg-blue-50 p-2 text-blue-600 hover:border-blue-400 disabled:cursor-not-allowed disabled:border-gray-200 disabled:bg-gray-100 disabled:text-gray-400"
                  >
                    <Edit size={14} />
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleDeleteDirect(entity)}
                    disabled={!canDelete}
                    className="ml-auto inline-flex items-center rounded-lg border border-red-200 bg-red-50 p-2 text-red-600 hover:border-red-300 disabled:cursor-not-allowed disabled:border-gray-200 disabled:bg-gray-100 disabled:text-gray-300"
                  >
                    <Trash2 size={14} />
                  </button>
                </div>
              </div>
            ))}
          </div>

          <div className="mt-6 rounded-xl border border-slate-200 bg-white shadow-sm">
            <TablePagination
              page={page}
              totalPages={totalPages}
              totalItems={total}
              pageSize={pageSize}
              pageSizeOptions={PAGE_SIZE_OPTIONS}
              onPageChange={setPage}
              onPageSizeChange={(size) => setPageSize(size)}
              recordsLabel="Найдено организаций"
            />
          </div>
        </>
      ) : (
        <div className="overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table
              className="w-full min-w-[1180px] table-fixed"
              style={{ width: getTableWidth(160, 1180), minWidth: '100%' }}
            >
              <colgroup>
                {orderedVisibleColumns.map((columnKey) => (
                  <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                ))}
                <col style={{ width: 160 }} />
              </colgroup>
              <thead className="border-b border-gray-200 bg-gray-50">
                <tr>
                  {orderedVisibleColumns.map((columnKey) => (
                    <th
                      key={columnKey}
                      style={{ width: getColumnWidth(columnKey) }}
                      className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wider ${
                        draggingColumn === columnKey ? 'bg-blue-50 text-blue-700' : 'text-gray-600'
                      }`}
                      onDragOver={handleColumnDragOver}
                      onDrop={(event) => handleColumnDrop(columnKey, event)}
                    >
                      <div
                        className="inline-flex min-w-0 select-none items-center gap-2 cursor-grab active:cursor-grabbing"
                        draggable
                        onDragStart={(event) => handleColumnDragStart(columnKey, event)}
                        onDragEnd={handleColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок"
                      >
                        <GripVertical className="h-4 w-4 text-slate-400" />
                        <span className="truncate">{LEGAL_ENTITY_COLUMN_LABELS[columnKey]}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${LEGAL_ENTITY_COLUMN_LABELS[columnKey]}`}
                        className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                        onMouseDown={(event) => handleResizeStart(columnKey, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wider text-gray-600">
                    Действия
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                {items.map((entity) => (
                  <tr key={entity.id} className="hover:bg-gray-50">
                    {orderedVisibleColumns.map((columnKey) => (
                      <td
                        key={columnKey}
                        style={{ width: getColumnWidth(columnKey) }}
                        className="max-w-0 overflow-hidden px-4 py-4 align-top"
                      >
                        {renderTableCell(entity, columnKey)}
                      </td>
                    ))}
                    <td className="px-4 py-4">
                      <div className="flex justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => void openDetailPage(entity.id, false)}
                          className="rounded-lg border border-gray-200 px-3 py-1.5 text-xs text-gray-600 hover:border-blue-400 hover:text-blue-600"
                        >
                          <Eye size={14} />
                        </button>
                        <button
                          type="button"
                          onClick={() => void openDetailPage(entity.id, true)}
                          disabled={!canEdit}
                          className="rounded-lg border border-blue-200 px-3 py-1.5 text-xs text-blue-600 hover:border-blue-400 disabled:cursor-not-allowed disabled:border-gray-200 disabled:text-gray-300"
                        >
                          <Edit size={14} />
                        </button>
                        <button
                          type="button"
                          onClick={() => void handleDeleteDirect(entity)}
                          disabled={!canDelete}
                          className="rounded-lg border border-red-200 px-3 py-1.5 text-xs text-red-600 hover:border-red-300 disabled:cursor-not-allowed disabled:border-gray-200 disabled:text-gray-300"
                        >
                          <Trash2 size={14} />
                        </button>
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
            totalItems={total}
            pageSize={pageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            onPageChange={setPage}
            onPageSizeChange={(size) => setPageSize(size)}
            recordsLabel="Найдено организаций"
          />
        </div>
      )}
    </div>
  );
}
