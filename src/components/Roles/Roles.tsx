import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  GripVertical,
  Pencil,
  Settings2,
  Shield,
  ShieldPlus,
  SlidersHorizontal,
  Trash2,
} from 'lucide-react';
import { authService, type Role as AuthRole } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import { RoleModal } from './RoleModal';
import { RolePermissionsModal } from './RolePermissionsModal';
import { getRolePermissions, subscribeOnPermissionsChange } from '../../services/permissionsService';
import { MENU_SECTION_KEYS } from '../../types/permissions';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';

const PAGE_SIZE_OPTIONS = [10, 20, 50];

const MENU_SECTION_LABELS: Record<(typeof MENU_SECTION_KEYS)[number], string> = {
  calendar: 'Календарь',
  reports: 'Отчеты',
  calculator: 'Калькулятор',
  clients: 'Клиенты',
  legalEntities: 'Юр. лица',
  accounts: 'Счета',
  acts: 'Акты',
  payments: 'Платежи',
  contracts: 'Договоры',
  dictionaries: 'Справочники',
};

const ROLE_COLUMNS = [
  { key: 'name', label: 'Название', defaultVisible: true, defaultWidth: 180 },
  { key: 'description', label: 'Описание', defaultVisible: true, defaultWidth: 260 },
  { key: 'createdAt', label: 'Создана', defaultVisible: true, defaultWidth: 140 },
  { key: 'permissions', label: 'Права доступа', defaultVisible: true, defaultWidth: 720 },
] as const;

type RoleColumnKey = (typeof ROLE_COLUMNS)[number]['key'];
export type Role = AuthRole;

const ROLE_COLUMN_LABELS: Record<RoleColumnKey, string> = ROLE_COLUMNS.reduce(
  (acc, column) => ({ ...acc, [column.key]: column.label }),
  {} as Record<RoleColumnKey, string>,
);

function formatDate(value?: string | null) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString('ru-RU');
}

export const Roles = () => {
  const { isAdmin } = useAuth();
  const toast = useToast();
  const { confirm } = useDialog();

  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showModal, setShowModal] = useState(false);
  const [selectedRole, setSelectedRole] = useState<Role | null>(null);
  const [permissionRole, setPermissionRole] = useState<Role | null>(null);
  const [, forcePermissionsTick] = useState(0);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [settingsOpen, setSettingsOpen] = useState(false);

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
  } = usePersistedTableSettings<RoleColumnKey>({
    storageKey: 'pp.roles_table_settings.v1',
    settingsKey: 'roles_table_settings',
    columns: ROLE_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 10,
    minPageSize: 10,
    maxPageSize: 50,
    minColumnWidth: 140,
  });

  const fetchRoles = useCallback(
    async (targetPage = page, withLoading = true) => {
      try {
        if (withLoading) setLoading(true);
        setError('');

        const response = await authService.getRolesPaged({
          page: targetPage,
          pageSize,
        });

        if (targetPage > 1 && response.items.length === 0 && response.total > 0) {
          setPage(targetPage - 1);
          return;
        }

        setRoles(response.items);
        setTotal(response.total);
      } catch (err) {
        const message = extractDisplayErrorMessage(err, 'Не удалось загрузить роли');
        setError(message);
        setRoles([]);
        setTotal(0);
        toast.error(message);
      } finally {
        if (withLoading) setLoading(false);
      }
    },
    [page, pageSize, toast],
  );

  useEffect(() => {
    void fetchRoles(page);
  }, [fetchRoles, page]);

  useEffect(() => {
    setPage(1);
  }, [pageSize]);

  useEffect(() => {
    const unsubscribe = subscribeOnPermissionsChange(() => {
      forcePermissionsTick((prev) => prev + 1);
    });
    return unsubscribe;
  }, []);

  const totalPages = useMemo(
    () => Math.max(1, Math.ceil((total || 0) / (pageSize || 1))),
    [pageSize, total],
  );

  const refreshAfterMutation = useCallback(
    async (remainingRows: number) => {
      const nextPage = remainingRows === 0 && page > 1 ? page - 1 : page;
      if (nextPage !== page) {
        setPage(nextPage);
      } else {
        await fetchRoles(nextPage, false);
      }
    },
    [fetchRoles, page],
  );

  const handleDelete = async (role: Role) => {
    const confirmed = await confirm({
      title: 'Подтверждение удаления',
      description: `Удалить роль "${role.name}"?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!confirmed) return;

    try {
      await authService.deleteRole(role.id);
      toast.success('Роль удалена');
      await refreshAfterMutation(Math.max(0, roles.length - 1));
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить роль'));
    }
  };

  const renderPermissions = (role: Role) => {
    const permissions = getRolePermissions(role.id);

    return (
      <div className="grid gap-3 xl:grid-cols-2 2xl:grid-cols-3">
        {MENU_SECTION_KEYS.map((key) => {
          const sectionPermissions = permissions[key];
          const badges = [
            { label: 'Просмотр', allowed: sectionPermissions.canView },
            { label: 'Создание', allowed: sectionPermissions.canCreate },
            { label: 'Редактирование', allowed: sectionPermissions.canEdit },
            { label: 'Удаление', allowed: sectionPermissions.canDelete },
            { label: 'Экспорт', allowed: sectionPermissions.canExport },
          ];

          if (key === 'calendar') {
            badges.push({ label: 'Аналитика', allowed: permissions.calendar.canViewAnalytics });
          }

          return (
            <div key={key} className="rounded-lg border border-slate-200 bg-slate-50 p-3 shadow-sm">
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm font-semibold text-slate-800">{MENU_SECTION_LABELS[key]}</span>
                <span
                  className={`text-xs font-semibold ${
                    sectionPermissions.canView ? 'text-emerald-600' : 'text-slate-400'
                  }`}
                >
                  {sectionPermissions.canView ? 'Открыт' : 'Закрыт'}
                </span>
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5">
                {badges.map((badge) => (
                  <span
                    key={`${key}-${badge.label}`}
                    className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs font-medium ${
                      badge.allowed
                        ? 'border border-emerald-100 bg-emerald-50 text-emerald-700'
                        : 'border border-dashed border-slate-200 text-slate-400'
                    }`}
                  >
                    {badge.allowed ? 'Да' : 'Нет'} {badge.label}
                  </span>
                ))}
              </div>
            </div>
          );
        })}
      </div>
    );
  };

  const renderCell = (role: Role, columnKey: RoleColumnKey) => {
    switch (columnKey) {
      case 'name':
        return (
          <div className="flex items-center gap-2">
            <Shield className="h-4 w-4 text-slate-400" />
            <span className="font-medium text-slate-900">{role.name}</span>
          </div>
        );
      case 'description':
        return <span className="text-slate-600">{role.description || '—'}</span>;
      case 'createdAt':
        return <span className="text-sm text-slate-600">{formatDate(role.createdAt)}</span>;
      case 'permissions':
        return renderPermissions(role);
      default:
        return null;
    }
  };

  if (!isAdmin()) {
    return (
      <div className="p-8">
        <div className="mx-auto max-w-4xl">
          <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center">
            <Shield className="mx-auto mb-4 h-12 w-12 text-red-600" />
            <h2 className="mb-2 text-xl font-bold text-red-900">Доступ запрещен</h2>
            <p className="text-red-700">Нужны права администратора для доступа к управлению ролями.</p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="p-8">
      <div className="mx-auto max-w-[calc(100vw-2rem)]">
        <div className="mb-8 flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="text-3xl font-bold text-slate-900">Управление ролями</h1>
            <p className="mt-2 text-slate-600">
              Серверная пагинация, единый стиль таблицы и сохранение вида таблицы за пользователем.
            </p>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition-colors ${
                settingsOpen
                  ? 'border-blue-200 bg-blue-50 text-blue-700'
                  : 'border-slate-200 bg-white text-slate-600 hover:bg-slate-50'
              }`}
            >
              <SlidersHorizontal className="h-4 w-4" />
              Колонки
            </button>
            <button
              type="button"
              onClick={() => {
                setSelectedRole(null);
                setShowModal(true);
              }}
              className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-white transition-colors hover:bg-slate-800"
            >
              <ShieldPlus className="h-5 w-5" />
              Добавить роль
            </button>
          </div>
        </div>

        {settingsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={ROLE_COLUMNS.map((column) => ({
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

        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[1280px] table-fixed">
              <colgroup>
                {orderedVisibleColumns.map((columnKey) => (
                  <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                ))}
                <col style={{ width: 150 }} />
              </colgroup>
              <thead className="border-b border-slate-200 bg-slate-50">
                <tr>
                  {orderedVisibleColumns.map((columnKey) => (
                    <th
                      key={columnKey}
                      style={{ width: getColumnWidth(columnKey) }}
                      className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wider ${
                        draggingColumn === columnKey ? 'bg-blue-50 text-blue-700' : 'text-slate-700'
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
                        <span className="truncate">{ROLE_COLUMN_LABELS[columnKey]}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${ROLE_COLUMN_LABELS[columnKey]}`}
                        className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                        onMouseDown={(event) => handleResizeStart(columnKey, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wider text-slate-700">
                    Действия
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {loading ? (
                  <tr>
                    <td
                      colSpan={orderedVisibleColumns.length + 1}
                      className="px-4 py-12 text-center text-slate-500"
                    >
                      Загрузка ролей...
                    </td>
                  </tr>
                ) : error ? (
                  <tr>
                    <td
                      colSpan={orderedVisibleColumns.length + 1}
                      className="px-4 py-12 text-center text-rose-600"
                    >
                      {error}
                    </td>
                  </tr>
                ) : roles.length === 0 ? (
                  <tr>
                    <td
                      colSpan={orderedVisibleColumns.length + 1}
                      className="px-4 py-12 text-center text-slate-500"
                    >
                      Роли не найдены
                    </td>
                  </tr>
                ) : (
                  roles.map((role) => (
                    <tr key={role.id} className="transition-colors hover:bg-slate-50">
                      {orderedVisibleColumns.map((columnKey) => (
                        <td
                          key={columnKey}
                          style={{ width: getColumnWidth(columnKey) }}
                          className="max-w-0 overflow-hidden px-4 py-4 align-top"
                        >
                          {renderCell(role, columnKey)}
                        </td>
                      ))}
                      <td className="px-4 py-4">
                        <div className="flex items-center justify-end gap-2">
                          <button
                            type="button"
                            onClick={() => setPermissionRole(role)}
                            className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
                          >
                            <Settings2 className="h-4 w-4" />
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setSelectedRole(role);
                              setShowModal(true);
                            }}
                            className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
                          >
                            <Pencil className="h-4 w-4" />
                          </button>
                          <button
                            type="button"
                            onClick={() => void handleDelete(role)}
                            className="rounded-lg p-2 text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700"
                          >
                            <Trash2 className="h-4 w-4" />
                          </button>
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
            totalItems={total}
            pageSize={pageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            onPageChange={setPage}
            onPageSizeChange={(size) => setPageSize(size)}
          />
        </div>
      </div>

      {showModal ? (
        <RoleModal
          role={selectedRole}
          onClose={() => {
            setShowModal(false);
            setSelectedRole(null);
          }}
          onSaved={() => {
            void fetchRoles(page, false);
          }}
        />
      ) : null}

      {permissionRole ? (
        <RolePermissionsModal
          role={permissionRole}
          onClose={() => {
            setPermissionRole(null);
            forcePermissionsTick((prev) => prev + 1);
          }}
        />
      ) : null}
    </div>
  );
};
