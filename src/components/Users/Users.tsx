import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  CheckCircle,
  Clock,
  Pencil,
  Shield,
  Trash2,
  UserCheck,
  UserPlus,
  UserX,
  XCircle,
} from 'lucide-react';
import { authService, type User } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateInTimeZone } from '../../utils/timeZoneDate';
import { TablePagination } from '../ui/TablePagination';
import { TextPromptModal } from '../ui/TextPromptModal';

type FilterTab = 'all' | 'pending' | 'approved';

const PAGE_SIZE_OPTIONS = [10, 20, 50, 100];

type UsersProps = {
  onCreateUser: () => void;
  onEditUser: (userId: number) => void;
};

function formatDate(value?: string | null, timeZoneId?: string | null) {
  if (!value) {
    return '—';
  }

  return formatDateInTimeZone(value, timeZoneId) || value;
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

function displayUserName(user: User) {
  return user.fullName || user.email || `Пользователь #${user.id}`;
}

export function Users({ onCreateUser, onEditUser }: UsersProps) {
  const { user: currentUser, isAdmin } = useAuth();
  const timeZoneId = useUserTimeZone();
  const toast = useToast();
  const { confirm } = useDialog();

  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [filterTab, setFilterTab] = useState<FilterTab>('all');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [total, setTotal] = useState(0);
  const [pendingCount, setPendingCount] = useState(0);
  const [rejectTarget, setRejectTarget] = useState<User | null>(null);
  const [rejectReason, setRejectReason] = useState('');
  const [rejectLoading, setRejectLoading] = useState(false);
  const [rejectError, setRejectError] = useState('');

  const fetchPendingCount = useCallback(async () => {
    try {
      const response = await authService.getUsersPaged({ status: 'pending', page: 1, pageSize: 1 });
      setPendingCount(response.total);
    } catch {
      // ignore counter refresh issues
    }
  }, []);

  const fetchUsers = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const response = await authService.getUsersPaged({
        status: filterTab === 'all' ? undefined : filterTab,
        page,
        pageSize,
      });

      if (page > 1 && response.items.length === 0 && response.total > 0) {
        setPage((current) => Math.max(1, current - 1));
        return;
      }

      setUsers(response.items);
      setTotal(response.total);
    } catch (nextError) {
      const message = extractDisplayErrorMessage(nextError, 'Не удалось загрузить пользователей.');
      setError(message);
      setUsers([]);
      setTotal(0);
      toast.error(message);
    } finally {
      setLoading(false);
    }
  }, [filterTab, page, pageSize, toast]);

  useEffect(() => {
    void fetchUsers();
  }, [fetchUsers]);

  useEffect(() => {
    void fetchPendingCount();
  }, [fetchPendingCount]);

  useEffect(() => {
    setPage(1);
  }, [filterTab, pageSize]);

  const totalPages = useMemo(() => Math.max(1, Math.ceil(total / pageSize || 1)), [pageSize, total]);

  const refreshAfterMutation = async (remainingRows: number) => {
    const nextPage = remainingRows === 0 && page > 1 ? page - 1 : page;
    if (nextPage !== page) {
      setPage(nextPage);
      return;
    }

    await fetchUsers();
    await fetchPendingCount();
  };

  const handleApprove = async (user: User) => {
    const approved = await confirm({
      title: 'Одобрить пользователя?',
      description: `Пользователь "${displayUserName(user)}" получит доступ к системе.`,
      confirmLabel: 'Одобрить',
      cancelLabel: 'Отмена',
    });
    if (!approved) {
      return;
    }

    try {
      await authService.approveUser(user.id);
      toast.success('Пользователь одобрен.');
      await refreshAfterMutation(Math.max(0, users.length - 1));
    } catch (nextError) {
      toast.error(extractDisplayErrorMessage(nextError, 'Не удалось одобрить пользователя.'));
    }
  };

  const handleRejectConfirm = async () => {
    if (!rejectTarget) {
      return;
    }

    setRejectLoading(true);
    setRejectError('');
    try {
      await authService.rejectUser(rejectTarget.id, rejectReason.trim() || undefined);
      toast.success('Пользователь отклонен.');
      setRejectTarget(null);
      setRejectReason('');
      await refreshAfterMutation(Math.max(0, users.length - 1));
    } catch (nextError) {
      const message = extractDisplayErrorMessage(nextError, 'Не удалось отклонить пользователя.');
      setRejectError(message);
      toast.error(message);
    } finally {
      setRejectLoading(false);
    }
  };

  const handleDelete = async (user: User) => {
    const approved = await confirm({
      title: 'Удалить пользователя?',
      description: `Карточка "${displayUserName(user)}" будет удалена без возможности восстановления.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) {
      return;
    }

    try {
      await authService.deleteUser(user.id);
      toast.success('Пользователь удален.');
      await refreshAfterMutation(Math.max(0, users.length - 1));
    } catch (nextError) {
      toast.error(extractDisplayErrorMessage(nextError, 'Не удалось удалить пользователя.'));
    }
  };

  if (!isAdmin()) {
    return (
      <div className="p-8">
        <div className="mx-auto max-w-4xl rounded-3xl border border-red-200 bg-red-50 p-6 text-center">
          <Shield className="mx-auto mb-4 h-12 w-12 text-red-600" />
          <h2 className="mb-2 text-xl font-bold text-red-900">Доступ запрещен</h2>
          <p className="text-red-700">Для управления пользователями нужны права администратора.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6 lg:p-8">
      <div className="space-y-6">
        <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <h1 className="text-3xl font-semibold text-slate-900">Пользователи</h1>
              <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-600">
                В таблице показана короткая сводка по пользователям. Подробное редактирование
                открывается отдельной страницей без модального окна.
              </p>
            </div>

            <button
              type="button"
              onClick={onCreateUser}
              className="inline-flex items-center gap-2 rounded-2xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800"
            >
              <UserPlus className="h-4 w-4" />
              Добавить пользователя
            </button>
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setFilterTab('all')}
            className={`rounded-2xl px-4 py-2 text-sm font-semibold transition ${
              filterTab === 'all' ? 'bg-slate-900 text-white' : 'bg-slate-100 text-slate-700 hover:bg-slate-200'
            }`}
          >
            Все
          </button>
          <button
            type="button"
            onClick={() => setFilterTab('pending')}
            className={`inline-flex items-center gap-2 rounded-2xl px-4 py-2 text-sm font-semibold transition ${
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
            className={`rounded-2xl px-4 py-2 text-sm font-semibold transition ${
              filterTab === 'approved'
                ? 'bg-emerald-600 text-white'
                : 'bg-emerald-50 text-emerald-700 hover:bg-emerald-100'
            }`}
          >
            Одобренные
          </button>
        </div>

        <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="min-w-full">
              <thead className="border-b border-slate-200 bg-slate-50">
                <tr className="text-left text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                  <th className="px-5 py-4">Пользователь</th>
                  <th className="px-5 py-4">Email</th>
                  <th className="px-5 py-4">Роль</th>
                  <th className="px-5 py-4">Одобрение</th>
                  <th className="px-5 py-4">Активность</th>
                  <th className="px-5 py-4">Создан</th>
                  <th className="px-5 py-4 text-right">Действия</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {loading ? (
                  <tr>
                    <td colSpan={7} className="px-5 py-10 text-center text-sm text-slate-500">
                      Загружаем пользователей...
                    </td>
                  </tr>
                ) : error ? (
                  <tr>
                    <td colSpan={7} className="px-5 py-10 text-center text-sm text-rose-600">
                      {error}
                    </td>
                  </tr>
                ) : users.length === 0 ? (
                  <tr>
                    <td colSpan={7} className="px-5 py-10 text-center text-sm text-slate-500">
                      Пользователи не найдены.
                    </td>
                  </tr>
                ) : (
                  users.map((user) => (
                    <tr key={user.id} className="transition hover:bg-slate-50">
                      <td className="px-5 py-4 align-top">
                        <div className="font-semibold text-slate-900">{displayUserName(user)}</div>
                        {user.isProtectedUser ? (
                          <div className="mt-2">
                            <span className="inline-flex rounded-full bg-blue-50 px-2.5 py-1 text-[11px] font-semibold text-blue-700">
                              Системный пользователь
                            </span>
                          </div>
                        ) : null}
                        {!user.isApproved ? (
                          <div className="mt-1 text-xs text-amber-600">Требует подтверждения</div>
                        ) : null}
                      </td>
                      <td className="px-5 py-4 align-top text-sm text-slate-600">{user.email || '—'}</td>
                      <td className="px-5 py-4 align-top">
                        <span className={`inline-flex rounded-full px-3 py-1 text-xs font-semibold ${roleBadgeClass(user.role.name)}`}>
                          {user.role.name}
                        </span>
                      </td>
                      <td className="px-5 py-4 align-top">
                        {user.isApproved ? (
                          <span className="inline-flex items-center gap-1 text-sm text-emerald-700">
                            <UserCheck className="h-4 w-4" />
                            Одобрен
                          </span>
                        ) : (
                          <span className="inline-flex items-center gap-1 text-sm text-amber-700">
                            <Clock className="h-4 w-4" />
                            Ожидает
                          </span>
                        )}
                      </td>
                      <td className="px-5 py-4 align-top">
                        {user.isActive ? (
                          <span className="inline-flex items-center gap-1 text-sm text-emerald-700">
                            <CheckCircle className="h-4 w-4" />
                            Активен
                          </span>
                        ) : (
                          <span className="inline-flex items-center gap-1 text-sm text-rose-700">
                            <XCircle className="h-4 w-4" />
                            Отключен
                          </span>
                        )}
                      </td>
                      <td className="px-5 py-4 align-top text-sm text-slate-600">
                        {formatDate(user.createdAt, timeZoneId)}
                      </td>
                      <td className="px-5 py-4 align-top">
                        <div className="flex items-center justify-end gap-2">
                          {!user.isApproved ? (
                            <>
                              <button
                                type="button"
                                onClick={() => void handleApprove(user)}
                                className="inline-flex items-center gap-1 rounded-2xl bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-700 transition hover:bg-emerald-100"
                              >
                                <UserCheck className="h-4 w-4" />
                                Одобрить
                              </button>
                              <button
                                type="button"
                                onClick={() => {
                                  setRejectTarget(user);
                                  setRejectReason('');
                                  setRejectError('');
                                }}
                                className="inline-flex items-center gap-1 rounded-2xl bg-rose-50 px-3 py-2 text-sm font-medium text-rose-700 transition hover:bg-rose-100"
                              >
                                <UserX className="h-4 w-4" />
                                Отклонить
                              </button>
                            </>
                          ) : (
                            <>
                              {!user.isProtectedUser || currentUser?.id === user.id ? (
                                <button
                                  type="button"
                                  onClick={() => onEditUser(user.id)}
                                  className="rounded-2xl p-2 text-slate-600 transition hover:bg-slate-100 hover:text-slate-900"
                                  title="Редактировать пользователя"
                                >
                                  <Pencil className="h-4 w-4" />
                                </button>
                              ) : null}
                              {!user.isProtectedUser ? (
                                <button
                                  type="button"
                                  onClick={() => void handleDelete(user)}
                                  className="rounded-2xl p-2 text-rose-600 transition hover:bg-rose-50 hover:text-rose-700"
                                  title="Удалить пользователя"
                                >
                                  <Trash2 className="h-4 w-4" />
                                </button>
                              ) : null}
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
            onPageSizeChange={setPageSize}
          />
        </div>
      </div>

      <TextPromptModal
        open={Boolean(rejectTarget)}
        title="Отклонить пользователя"
        description={
          rejectTarget
            ? `При необходимости укажите причину отклонения для "${displayUserName(rejectTarget)}".`
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
        placeholder="Причина не обязательна"
        error={rejectError}
        loading={rejectLoading}
        multiline
      />
    </div>
  );
}
