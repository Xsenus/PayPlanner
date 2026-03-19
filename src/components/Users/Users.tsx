import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  CheckCircle,
  Clock,
  GripVertical,
  Pencil,
  Shield,
  SlidersHorizontal,
  Trash2,
  UserCheck,
  UserPlus,
  UserX,
  XCircle,
} from 'lucide-react';
import { authService, type User } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import { UserModal } from './UserModal';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { TextPromptModal } from '../ui/TextPromptModal';

type FilterTab = 'all' | 'pending' | 'approved';

const PAGE_SIZE_OPTIONS = [10, 20, 50, 100];

const USER_COLUMNS = [
  { key: 'fullName', label: 'Пользователь', defaultVisible: true, defaultWidth: 240 },
  { key: 'email', label: 'Email', defaultVisible: true, defaultWidth: 220 },
  { key: 'phoneNumber', label: 'Телефон', defaultVisible: false, defaultWidth: 170 },
  { key: 'role', label: 'Роль', defaultVisible: true, defaultWidth: 140 },
  { key: 'approval', label: 'Согласование', defaultVisible: true, defaultWidth: 170 },
  { key: 'activity', label: 'Активность', defaultVisible: true, defaultWidth: 140 },
  { key: 'createdAt', label: 'Создан', defaultVisible: true, defaultWidth: 140 },
] as const;

type UserColumnKey = (typeof USER_COLUMNS)[number]['key'];

const USER_COLUMN_LABELS: Record<UserColumnKey, string> = USER_COLUMNS.reduce(
  (acc, column) => ({ ...acc, [column.key]: column.label }),
  {} as Record<UserColumnKey, string>,
);

function formatDate(value?: string | null) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString('ru-RU');
}

function roleBadgeClass(roleName?: string) {
  switch ((roleName ?? '').toLowerCase()) {
    case 'admin':
      return 'bg-red-100 text-red-700';
    case 'manager':
      return 'bg-blue-100 text-blue-700';
    default:
      return 'bg-slate-100 text-slate-700';
  }
}

export const Users = () => {
  const { isAdmin } = useAuth();
  const toast = useToast();
  const { confirm } = useDialog();

  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showModal, setShowModal] = useState(false);
  const [selectedUser, setSelectedUser] = useState<User | null>(null);
  const [filterTab, setFilterTab] = useState<FilterTab>('all');
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [pendingCount, setPendingCount] = useState(0);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [rejectTarget, setRejectTarget] = useState<User | null>(null);
  const [rejectReason, setRejectReason] = useState('');
  const [rejectError, setRejectError] = useState('');
  const [rejectLoading, setRejectLoading] = useState(false);

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
  } = usePersistedTableSettings<UserColumnKey>({
    storageKey: 'pp.users_table_settings.v1',
    settingsKey: 'users_table_settings',
    columns: USER_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 20,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 120,
  });

  const fetchPendingCount = useCallback(async () => {
    try {
      const response = await authService.getUsersPaged({ status: 'pending', page: 1, pageSize: 1 });
      setPendingCount(response.total);
    } catch {
      /** */
    }
  }, []);

  const fetchUsers = useCallback(
    async (targetPage = page, withLoading = true) => {
      try {
        if (withLoading) setLoading(true);
        setError('');

        const response = await authService.getUsersPaged({
          status: filterTab === 'all' ? undefined : filterTab,
          page: targetPage,
          pageSize,
        });

        if (targetPage > 1 && response.items.length === 0 && response.total > 0) {
          setPage(targetPage - 1);
          return;
        }

        setUsers(response.items);
        setTotal(response.total);
      } catch (err) {
        const message = extractDisplayErrorMessage(err, 'Не удалось загрузить пользователей');
        setError(message);
        setUsers([]);
        setTotal(0);
        toast.error(message);
      } finally {
        if (withLoading) setLoading(false);
      }
    },
    [filterTab, page, pageSize, toast],
  );

  useEffect(() => {
    void fetchUsers(page);
  }, [fetchUsers, page]);

  useEffect(() => {
    void fetchPendingCount();
  }, [fetchPendingCount]);

  useEffect(() => {
    setPage(1);
  }, [filterTab, pageSize]);

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
        await fetchUsers(nextPage, false);
      }
      await fetchPendingCount();
    },
    [fetchPendingCount, fetchUsers, page],
  );

  const handleApprove = async (user: User) => {
    const confirmed = await confirm({
      title: 'Подтвердите действие',
      description: `Одобрить пользователя "${user.fullName}"?`,
      confirmLabel: 'Одобрить',
      cancelLabel: 'Отмена',
    });
    if (!confirmed) return;

    try {
      await authService.approveUser(user.id);
      toast.success('Пользователь одобрен');
      await refreshAfterMutation(Math.max(0, users.length - 1));
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось одобрить пользователя'));
    }
  };

  const handleRejectStart = (user: User) => {
    setRejectTarget(user);
    setRejectReason('');
    setRejectError('');
  };

  const handleRejectConfirm = async () => {
    if (!rejectTarget) return;

    try {
      setRejectLoading(true);
      setRejectError('');
      await authService.rejectUser(rejectTarget.id, rejectReason.trim() || undefined);
      toast.success('Пользователь отклонен');
      setRejectTarget(null);
      setRejectReason('');
      await refreshAfterMutation(Math.max(0, users.length - 1));
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось отклонить пользователя');
      setRejectError(message);
      toast.error(message);
    } finally {
      setRejectLoading(false);
    }
  };

  const handleDelete = async (user: User) => {
    const confirmed = await confirm({
      title: 'Подтверждение удаления',
      description: `Удалить пользователя "${user.fullName}"?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!confirmed) return;

    try {
      await authService.deleteUser(user.id);
      toast.success('Пользователь удален');
      await refreshAfterMutation(Math.max(0, users.length - 1));
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить пользователя'));
    }
  };

  const handleModalSaved = async () => {
    await fetchUsers(page, false);
    await fetchPendingCount();
  };

  const renderCell = (user: User, columnKey: UserColumnKey) => {
    switch (columnKey) {
      case 'fullName':
        return (
          <div className="min-w-0">
            <div className="truncate font-medium text-slate-900">{user.fullName}</div>
            {!user.isApproved ? (
              <div className="mt-1 text-xs text-amber-600">Требует подтверждения</div>
            ) : null}
          </div>
        );
      case 'email':
        return <span className="break-all text-slate-600">{user.email}</span>;
      case 'phoneNumber':
        return <span className="text-slate-600">{user.phoneNumber || '—'}</span>;
      case 'role':
        return (
          <span
            className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${roleBadgeClass(
              user.role.name,
            )}`}
          >
            {user.role.name}
          </span>
        );
      case 'approval':
        return user.isApproved ? (
          <span className="inline-flex items-center gap-1 text-sm text-emerald-700">
            <UserCheck className="h-4 w-4" />
            Одобрен
          </span>
        ) : (
          <span className="inline-flex items-center gap-1 text-sm text-amber-700">
            <Clock className="h-4 w-4" />
            Ожидает
          </span>
        );
      case 'activity':
        return user.isActive ? (
          <span className="inline-flex items-center gap-1 text-sm text-emerald-700">
            <CheckCircle className="h-4 w-4" />
            Активен
          </span>
        ) : (
          <span className="inline-flex items-center gap-1 text-sm text-rose-700">
            <XCircle className="h-4 w-4" />
            Неактивен
          </span>
        );
      case 'createdAt':
        return <span className="text-sm text-slate-600">{formatDate(user.createdAt)}</span>;
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
            <p className="text-red-700">
              Нужны права администратора для доступа к управлению пользователями.
            </p>
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
            <h1 className="text-3xl font-bold text-slate-900">Управление пользователями</h1>
            <p className="mt-2 text-slate-600">
              Серверная пагинация, единый стиль таблицы и пользовательские настройки колонок.
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
                setSelectedUser(null);
                setShowModal(true);
              }}
              className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-white transition-colors hover:bg-slate-800"
            >
              <UserPlus className="h-5 w-5" />
              Добавить пользователя
            </button>
          </div>
        </div>

        <div className="mb-6 flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setFilterTab('all')}
            className={`rounded-lg px-4 py-2 font-medium transition-colors ${
              filterTab === 'all'
                ? 'bg-slate-900 text-white'
                : 'bg-slate-100 text-slate-700 hover:bg-slate-200'
            }`}
          >
            Все
          </button>
          <button
            type="button"
            onClick={() => setFilterTab('pending')}
            className={`flex items-center gap-2 rounded-lg px-4 py-2 font-medium transition-colors ${
              filterTab === 'pending'
                ? 'bg-amber-600 text-white'
                : 'bg-amber-50 text-amber-700 hover:bg-amber-100'
            }`}
          >
            <Clock className="h-4 w-4" />
            Ожидают одобрения
            {filterTab !== 'pending' && pendingCount > 0 ? (
              <span className="rounded-full bg-amber-600 px-2 py-0.5 text-xs text-white">
                {pendingCount}
              </span>
            ) : null}
          </button>
          <button
            type="button"
            onClick={() => setFilterTab('approved')}
            className={`rounded-lg px-4 py-2 font-medium transition-colors ${
              filterTab === 'approved'
                ? 'bg-emerald-600 text-white'
                : 'bg-emerald-50 text-emerald-700 hover:bg-emerald-100'
            }`}
          >
            Одобренные
          </button>
        </div>

        {settingsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={USER_COLUMNS.map((column) => ({
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
            <table className="w-full min-w-[980px] table-fixed">
              <colgroup>
                {orderedVisibleColumns.map((columnKey) => (
                  <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                ))}
                <col style={{ width: 210 }} />
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
                        <span className="truncate">{USER_COLUMN_LABELS[columnKey]}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${USER_COLUMN_LABELS[columnKey]}`}
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
                      Загрузка пользователей...
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
                ) : users.length === 0 ? (
                  <tr>
                    <td
                      colSpan={orderedVisibleColumns.length + 1}
                      className="px-4 py-12 text-center text-slate-500"
                    >
                      Пользователи не найдены
                    </td>
                  </tr>
                ) : (
                  users.map((user) => (
                    <tr key={user.id} className="transition-colors hover:bg-slate-50">
                      {orderedVisibleColumns.map((columnKey) => (
                        <td
                          key={columnKey}
                          style={{ width: getColumnWidth(columnKey) }}
                          className="max-w-0 overflow-hidden px-4 py-4 align-top"
                        >
                          {renderCell(user, columnKey)}
                        </td>
                      ))}
                      <td className="px-4 py-4">
                        <div className="flex items-center justify-end gap-2">
                          {!user.isApproved ? (
                            <>
                              <button
                                type="button"
                                onClick={() => void handleApprove(user)}
                                className="inline-flex items-center gap-1 rounded-lg bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 transition-colors hover:bg-emerald-100"
                              >
                                <UserCheck className="h-4 w-4" />
                                Одобрить
                              </button>
                              <button
                                type="button"
                                onClick={() => handleRejectStart(user)}
                                className="inline-flex items-center gap-1 rounded-lg bg-rose-50 px-3 py-1.5 text-sm text-rose-700 transition-colors hover:bg-rose-100"
                              >
                                <UserX className="h-4 w-4" />
                                Отклонить
                              </button>
                            </>
                          ) : (
                            <>
                              <button
                                type="button"
                                onClick={() => {
                                  setSelectedUser(user);
                                  setShowModal(true);
                                }}
                                className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
                              >
                                <Pencil className="h-4 w-4" />
                              </button>
                              <button
                                type="button"
                                onClick={() => void handleDelete(user)}
                                className="rounded-lg p-2 text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700"
                              >
                                <Trash2 className="h-4 w-4" />
                              </button>
                            </>
                          )}
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
        <UserModal
          user={selectedUser}
          onClose={() => {
            setShowModal(false);
            setSelectedUser(null);
          }}
          onSaved={() => {
            void handleModalSaved();
          }}
        />
      ) : null}

      <TextPromptModal
        open={Boolean(rejectTarget)}
        title="Отклонить пользователя"
        description={
          rejectTarget
            ? `При необходимости укажите причину отклонения для "${rejectTarget.fullName}".`
            : undefined
        }
        label="Причина отклонения"
        value={rejectReason}
        onValueChange={setRejectReason}
        onCancel={() => {
          setRejectTarget(null);
          setRejectReason('');
          setRejectError('');
        }}
        onConfirm={handleRejectConfirm}
        confirmLabel="Отклонить"
        cancelLabel="Отмена"
        placeholder="Причина необязательна"
        error={rejectError}
        loading={rejectLoading}
        multiline
      />
    </div>
  );
};
