import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Copy,
  GripVertical,
  Pencil,
  Settings2,
  Shield,
  ShieldPlus,
  SlidersHorizontal,
  Trash2,
  UsersRound,
} from 'lucide-react';
import { authService, type Role as AuthRole } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import { RoleModal } from './RoleModal';
import { RolePermissionsModal } from './RolePermissionsModal';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { MENU_SECTION_KEYS } from '../../types/permissions';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { getObjectAuditActorLabel } from '../../utils/objectAudit';

const PAGE_SIZE_OPTIONS = [10, 20, 50];

const ROLE_COLUMNS = [
  { key: 'name', label: 'Название', defaultVisible: true, defaultWidth: 240 },
  { key: 'description', label: 'Описание', defaultVisible: true, defaultWidth: 300 },
  { key: 'users', label: 'Пользователи', defaultVisible: true, defaultWidth: 140 },
  { key: 'updated', label: 'Изменение прав', defaultVisible: true, defaultWidth: 220 },
  { key: 'type', label: 'Тип роли', defaultVisible: false, defaultWidth: 180 },
  { key: 'access', label: 'Доступ', defaultVisible: false, defaultWidth: 200 },
] as const;

type RoleColumnKey = (typeof ROLE_COLUMNS)[number]['key'];
export type Role = AuthRole;

type RoleDialogState =
  | { mode: 'create'; role: null; sourceRole: null }
  | { mode: 'edit'; role: Role; sourceRole: null }
  | { mode: 'copy'; role: null; sourceRole: Role }
  | null;

const ROLE_COLUMN_LABELS: Record<RoleColumnKey, string> = ROLE_COLUMNS.reduce(
  (acc, column) => ({ ...acc, [column.key]: column.label }),
  {} as Record<RoleColumnKey, string>,
);

function formatDate(value?: string | null) {
  if (!value) return '—';

  return new Date(value).toLocaleDateString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
}

function formatDateTime(value?: string | null) {
  if (!value) return '—';

  return new Date(value).toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function formatAuditUserName(value?: string | null) {
  return value?.trim() ? getObjectAuditActorLabel(value) : 'После создания права не менялись';
}

function getAccessibleSectionsLabel(role: Role) {
  const total = role.totalSectionsCount ?? MENU_SECTION_KEYS.length;
  const accessible = role.accessibleSectionsCount ?? 0;
  return `${accessible} из ${total} разделов`;
}

export const Roles = () => {
  const { user, isAdmin } = useAuth();
  const currentRolePermissions = useRolePermissions(user?.role?.id);
  const toast = useToast();
  const { confirm } = useDialog();

  const canViewRoles = isAdmin() || currentRolePermissions.roles.canView;
  const canCreateRoles = isAdmin() || currentRolePermissions.roles.canCreate;
  const canEditRoles = isAdmin() || currentRolePermissions.roles.canEdit;
  const canDeleteRoles = isAdmin() || currentRolePermissions.roles.canDelete;

  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [dialogState, setDialogState] = useState<RoleDialogState>(null);
  const [permissionRole, setPermissionRole] = useState<Role | null>(null);
  const [selectedRoleId, setSelectedRoleId] = useState<number | null>(null);
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
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<RoleColumnKey>({
    storageKey: 'pp.roles_table_settings.v3',
    settingsKey: 'roles_table_settings',
    columns: ROLE_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 10,
    minPageSize: 10,
    maxPageSize: 50,
    minColumnWidth: 120,
  });

  const selectedRole = useMemo(
    () => roles.find((role) => role.id === selectedRoleId) ?? null,
    [roles, selectedRoleId],
  );

  const totalPages = useMemo(
    () => Math.max(1, Math.ceil((total || 0) / (pageSize || 1))),
    [pageSize, total],
  );

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
    if (!canViewRoles) {
      setLoading(false);
      return;
    }

    void fetchRoles(page);
  }, [canViewRoles, fetchRoles, page]);

  useEffect(() => {
    setPage(1);
  }, [pageSize]);

  useEffect(() => {
    if (roles.length === 0) {
      setSelectedRoleId(null);
      return;
    }

    if (!selectedRoleId || !roles.some((role) => role.id === selectedRoleId)) {
      setSelectedRoleId(roles[0].id);
    }
  }, [roles, selectedRoleId]);

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
      title: 'Удаление роли',
      description: `Удалить роль «${role.name}»?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!confirmed) {
      return;
    }

    try {
      await authService.deleteRole(role.id);
      toast.success('Роль удалена');
      await refreshAfterMutation(Math.max(0, roles.length - 1));
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить роль'));
    }
  };

  const renderCell = (role: Role, columnKey: RoleColumnKey) => {
    switch (columnKey) {
      case 'name':
        return (
          <div className="space-y-2">
            <div className="flex items-center gap-2">
              <Shield className="h-4 w-4 text-slate-400" />
              <span className="font-semibold text-slate-900">{role.name}</span>
            </div>
            <span
              className={`inline-flex items-center rounded-full border px-2.5 py-1 text-[11px] font-medium ${
                role.isSystemRole
                  ? 'border-amber-200 bg-amber-50 text-amber-700'
                  : 'border-slate-200 bg-slate-50 text-slate-600'
              }`}
            >
              {role.isSystemRole ? 'Системная роль' : 'Пользовательская'}
            </span>
          </div>
        );
      case 'description':
        return (
          <div className="text-sm leading-6 text-slate-600">
            {role.description?.trim() || 'Описание не задано'}
          </div>
        );
      case 'users':
        return (
          <div className="space-y-1">
            <div className="flex items-center gap-2 text-slate-900">
              <UsersRound className="h-4 w-4 text-slate-400" />
              <span className="font-semibold">{role.assignedUsersCount ?? 0}</span>
            </div>
            <div className="text-xs text-slate-500">
              {(role.assignedUsersCount ?? 0) > 0 ? 'Назначена пользователям' : 'Пока не назначена'}
            </div>
          </div>
        );
      case 'updated':
        return (
          <div className="space-y-1">
            <div className="text-sm font-medium text-slate-900">
              {role.permissionsUpdatedAt ? formatDateTime(role.permissionsUpdatedAt) : 'Не изменялись'}
            </div>
            <div className="text-xs text-slate-500">
              {formatAuditUserName(role.permissionsUpdatedByUserName)}
            </div>
          </div>
        );
      case 'type':
        return (
          <span
            className={`inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-medium ${
              role.isSystemRole
                ? 'border-amber-200 bg-amber-50 text-amber-700'
                : 'border-blue-200 bg-blue-50 text-blue-700'
            }`}
          >
            {role.isSystemRole ? 'Стандартная' : 'Настраиваемая'}
          </span>
        );
      case 'access':
        return (
          <div className="space-y-1">
            <div className="text-sm font-medium text-slate-900">{getAccessibleSectionsLabel(role)}</div>
            <div className="text-xs text-slate-500">
              {(role.accessibleSectionsCount ?? 0) === (role.totalSectionsCount ?? MENU_SECTION_KEYS.length)
                ? 'Полный доступ'
                : 'Выборочная настройка'}
            </div>
          </div>
        );
      default:
        return null;
    }
  };

  if (!canViewRoles) {
    return (
      <div className="p-8">
        <div className="mx-auto max-w-4xl">
          <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center">
            <Shield className="mx-auto mb-4 h-12 w-12 text-red-600" />
            <h2 className="mb-2 text-xl font-bold text-red-900">Доступ запрещён</h2>
            <p className="text-red-700">
              Недостаточно прав для просмотра и настройки ролей пользователей.
            </p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="p-8">
      <div className="mx-auto max-w-screen-2xl">
        <div className="mb-6 flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="text-3xl font-bold text-slate-900">Управление ролями</h1>
            <p className="mt-2 max-w-2xl text-sm leading-6 text-slate-600">
              В таблице показан компактный список ролей. Подробности по выбранной роли и
              настройка прав открываются отдельно.
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

            {canCreateRoles ? (
              <button
                type="button"
                onClick={() => setDialogState({ mode: 'create', role: null, sourceRole: null })}
                className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-white transition-colors hover:bg-slate-800"
              >
                <ShieldPlus className="h-5 w-5" />
                Добавить роль
              </button>
            ) : null}
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

        <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_360px]">
          <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
            <div className="overflow-x-auto">
              <table
                className="w-full min-w-[900px] table-fixed"
                style={{ width: getTableWidth(170, 900), minWidth: '100%' }}
              >
                <colgroup>
                  {orderedVisibleColumns.map((columnKey) => (
                    <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                  ))}
                  <col style={{ width: 170 }} />
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
                          className="inline-flex min-w-0 cursor-grab select-none items-center gap-2 active:cursor-grabbing"
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
                      <td colSpan={orderedVisibleColumns.length + 1} className="px-4 py-12 text-center text-slate-500">
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
                      <td colSpan={orderedVisibleColumns.length + 1} className="px-4 py-12 text-center text-slate-500">
                        Роли не найдены
                      </td>
                    </tr>
                  ) : (
                    roles.map((role) => {
                      const isDeleteBlocked = role.isSystemRole || (role.assignedUsersCount ?? 0) > 0;
                      const isSelected = selectedRoleId === role.id;

                      return (
                        <tr
                          key={role.id}
                          className={`cursor-pointer transition-colors ${
                            isSelected ? 'bg-blue-50/60' : 'hover:bg-slate-50'
                          }`}
                          onClick={() => setSelectedRoleId(role.id)}
                        >
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
                            <div className="flex items-center justify-end gap-1">
                              <button
                                type="button"
                                onClick={(event) => {
                                  event.stopPropagation();
                                  setSelectedRoleId(role.id);
                                  setPermissionRole(role);
                                }}
                                className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
                                title={canEditRoles && !role.isSystemRole ? 'Настроить права' : 'Просмотреть права'}
                              >
                                <Settings2 className="h-4 w-4" />
                              </button>

                              {canCreateRoles ? (
                                <button
                                  type="button"
                                  onClick={(event) => {
                                    event.stopPropagation();
                                    setDialogState({ mode: 'copy', role: null, sourceRole: role });
                                  }}
                                  className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
                                  title="Скопировать роль"
                                >
                                  <Copy className="h-4 w-4" />
                                </button>
                              ) : null}

                              {canEditRoles ? (
                                <button
                                  type="button"
                                  onClick={(event) => {
                                    event.stopPropagation();
                                    setDialogState({ mode: 'edit', role, sourceRole: null });
                                  }}
                                  className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900 disabled:cursor-not-allowed disabled:opacity-40"
                                  title={role.isSystemRole ? 'Стандартную роль администратора нельзя изменять' : 'Редактировать роль'}
                                  disabled={role.isSystemRole}
                                >
                                  <Pencil className="h-4 w-4" />
                                </button>
                              ) : null}

                              {canDeleteRoles ? (
                                <button
                                  type="button"
                                  onClick={(event) => {
                                    event.stopPropagation();
                                    void handleDelete(role);
                                  }}
                                  className="rounded-lg p-2 text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700 disabled:cursor-not-allowed disabled:opacity-40"
                                  title={
                                    role.isSystemRole
                                      ? 'Стандартную роль администратора нельзя удалять'
                                      : (role.assignedUsersCount ?? 0) > 0
                                        ? 'Нельзя удалить роль, пока она назначена пользователям'
                                        : 'Удалить роль'
                                  }
                                  disabled={isDeleteBlocked}
                                >
                                  <Trash2 className="h-4 w-4" />
                                </button>
                              ) : null}
                            </div>
                          </td>
                        </tr>
                      );
                    })
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

          <aside className="space-y-4">
            <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                    Карточка роли
                  </div>
                  <h2 className="mt-2 text-xl font-semibold text-slate-900">
                    {selectedRole?.name || 'Роль не выбрана'}
                  </h2>
                </div>
                {selectedRole ? (
                  <span
                    className={`inline-flex items-center rounded-full border px-2.5 py-1 text-[11px] font-medium ${
                      selectedRole.isSystemRole
                        ? 'border-amber-200 bg-amber-50 text-amber-700'
                        : 'border-blue-200 bg-blue-50 text-blue-700'
                    }`}
                  >
                    {selectedRole.isSystemRole ? 'Системная' : 'Настраиваемая'}
                  </span>
                ) : null}
              </div>

              <p className="mt-4 text-sm leading-6 text-slate-600">
                {selectedRole?.description?.trim() || 'Выберите роль в таблице, чтобы увидеть подробности и действия.'}
              </p>

              {selectedRole ? (
                <div className="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-1 2xl:grid-cols-2">
                  <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                    <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">Пользователи</div>
                    <div className="mt-2 text-lg font-semibold text-slate-900">
                      {selectedRole.assignedUsersCount ?? 0}
                    </div>
                  </div>
                  <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                    <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">Доступ</div>
                    <div className="mt-2 text-sm font-semibold text-slate-900">
                      {getAccessibleSectionsLabel(selectedRole)}
                    </div>
                  </div>
                  <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                    <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">Создана</div>
                    <div className="mt-2 text-sm font-semibold text-slate-900">
                      {formatDate(selectedRole.createdAt)}
                    </div>
                  </div>
                  <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                    <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">Последнее изменение</div>
                    <div className="mt-2 text-sm font-semibold text-slate-900">
                      {formatDateTime(selectedRole.permissionsUpdatedAt)}
                    </div>
                  </div>
                </div>
              ) : null}
            </div>

            {selectedRole ? (
              <ObjectAuditLauncher
                objectType="role"
                objectId={selectedRole.id}
                createdAt={selectedRole.createdAt}
                createdByUserName={selectedRole.createdByUserName}
                updatedAt={selectedRole.updatedAt}
                updatedByUserName={selectedRole.updatedByUserName}
                triggerTitle="Аудит роли"
                triggerDescription="Паспорт роли и история изменений открываются в отдельном окне."
                title="История роли"
              />
            ) : null}

            {selectedRole ? (
              <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                  Последнее изменение прав
                </div>
                <div className="mt-3 text-sm font-medium text-slate-900">
                  {formatAuditUserName(selectedRole.permissionsUpdatedByUserName)}
                </div>
                <div className="mt-1 text-sm text-slate-500">
                  {formatDateTime(selectedRole.permissionsUpdatedAt)}
                </div>
              </div>
            ) : null}

            {selectedRole ? (
              <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                  Действия
                </div>
                <div className="mt-4 grid gap-2">
                  <button
                    type="button"
                    onClick={() => setPermissionRole(selectedRole)}
                    className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50"
                  >
                    <Settings2 className="h-4 w-4" />
                    {canEditRoles && !selectedRole.isSystemRole ? 'Настроить права' : 'Просмотреть права'}
                  </button>

                  {canEditRoles ? (
                    <button
                      type="button"
                      onClick={() => setDialogState({ mode: 'edit', role: selectedRole, sourceRole: null })}
                      disabled={selectedRole.isSystemRole}
                      className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <Pencil className="h-4 w-4" />
                      Редактировать роль
                    </button>
                  ) : null}

                  {canCreateRoles ? (
                    <button
                      type="button"
                      onClick={() => setDialogState({ mode: 'copy', role: null, sourceRole: selectedRole })}
                      className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50"
                    >
                      <Copy className="h-4 w-4" />
                      Скопировать роль
                    </button>
                  ) : null}

                  {canDeleteRoles ? (
                    <button
                      type="button"
                      onClick={() => void handleDelete(selectedRole)}
                      disabled={selectedRole.isSystemRole || (selectedRole.assignedUsersCount ?? 0) > 0}
                      className="inline-flex items-center justify-center gap-2 rounded-lg border border-rose-200 px-4 py-2 text-sm font-medium text-rose-700 transition-colors hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <Trash2 className="h-4 w-4" />
                      Удалить роль
                    </button>
                  ) : null}
                </div>
              </div>
            ) : null}
          </aside>
        </div>
      </div>

      {dialogState ? (
        <RoleModal
          role={dialogState.mode === 'edit' ? dialogState.role : null}
          copySourceRole={dialogState.mode === 'copy' ? dialogState.sourceRole : null}
          onClose={() => setDialogState(null)}
          onSaved={() => {
            void fetchRoles(page, false);
          }}
        />
      ) : null}

      {permissionRole ? (
        <RolePermissionsModal
          role={permissionRole}
          canEdit={canEditRoles}
          onClose={() => setPermissionRole(null)}
          onSaved={() => {
            void fetchRoles(page, false);
          }}
        />
      ) : null}
    </div>
  );
};
