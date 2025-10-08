import { useState, useEffect } from 'react';
import { authService, User } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import {
  UserPlus,
  Pencil,
  Trash2,
  Shield,
  CheckCircle,
  XCircle,
  UserCheck,
  UserX,
  Clock,
} from 'lucide-react';
import { UserModal } from './UserModal';

type FilterTab = 'all' | 'pending' | 'approved';

export const Users = () => {
  const { isAdmin } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showModal, setShowModal] = useState(false);
  const [selectedUser, setSelectedUser] = useState<User | null>(null);
  const [filterTab, setFilterTab] = useState<FilterTab>('all');

  useEffect(() => {
    fetchUsers();
  }, [filterTab]);

  const fetchUsers = async () => {
    try {
      setLoading(true);
      const status = filterTab === 'all' ? undefined : filterTab;
      const data = await authService.getUsers(status);
      setUsers(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить пользователей');
    } finally {
      setLoading(false);
    }
  };

  const handleApprove = async (userId: number) => {
    if (!confirm('Подтвердить одобрение пользователя?')) return;
    try {
      await authService.approveUser(userId);
      await fetchUsers();
    } catch (err) {
      alert(err instanceof Error ? err.message : 'Не удалось одобрить пользователя');
    }
  };

  const handleReject = async (userId: number) => {
    const reason = prompt('Укажите причину отклонения (необязательно):');
    if (reason === null) return;
    try {
      await authService.rejectUser(userId, reason || undefined);
      await fetchUsers();
    } catch (err) {
      alert(err instanceof Error ? err.message : 'Не удалось отклонить пользователя');
    }
  };

  const handleDelete = async (userId: number) => {
    if (!confirm('Удалить пользователя?')) return;
    try {
      await authService.deleteUser(userId);
      await fetchUsers();
    } catch (err) {
      alert(err instanceof Error ? err.message : 'Не удалось удалить пользователя');
    }
  };

  const handleEdit = (user: User) => {
    setSelectedUser(user);
    setShowModal(true);
  };

  const handleAdd = () => {
    setSelectedUser(null);
    setShowModal(true);
  };

  const handleModalClose = () => {
    setShowModal(false);
    setSelectedUser(null);
    fetchUsers();
  };

  if (!isAdmin()) {
    return (
      <div className="p-8">
        <div className="max-w-4xl mx-auto">
          <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
            <Shield className="w-12 h-12 text-red-600 mx-auto mb-4" />
            <h2 className="text-xl font-bold text-red-900 mb-2">Доступ запрещён</h2>
            <p className="text-red-700">
              Нужны права администратора для доступа к управлению пользователями.
            </p>
          </div>
        </div>
      </div>
    );
  }

  if (loading) {
    return (
      <div className="p-8">
        <div className="max-w-6xl mx-auto">
          <div className="text-center py-12">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-4 border-slate-200 border-t-slate-900"></div>
            <p className="mt-4 text-slate-600">Загрузка пользователей…</p>
          </div>
        </div>
      </div>
    );
  }

  const pendingCount = users.filter((u) => !u.isApproved).length;

  return (
    <div className="p-8">
      <div className="max-w-7xl mx-auto">
        <div className="flex items-center justify-between mb-8">
          <div>
            <h1 className="text-3xl font-bold text-slate-900">Управление пользователями</h1>
            <p className="text-slate-600 mt-2">Управляйте пользователями и их ролями</p>
          </div>
          <button
            onClick={handleAdd}
            className="flex items-center gap-2 px-4 py-2 bg-slate-900 text-white rounded-lg hover:bg-slate-800 transition-colors">
            <UserPlus className="w-5 h-5" />
            Добавить пользователя
          </button>
        </div>

        <div className="flex gap-2 mb-6">
          <button
            onClick={() => setFilterTab('all')}
            className={`px-4 py-2 rounded-lg font-medium transition-colors ${
              filterTab === 'all'
                ? 'bg-slate-900 text-white'
                : 'bg-slate-100 text-slate-700 hover:bg-slate-200'
            }`}>
            Все
          </button>
          <button
            onClick={() => setFilterTab('pending')}
            className={`px-4 py-2 rounded-lg font-medium transition-colors flex items-center gap-2 ${
              filterTab === 'pending'
                ? 'bg-amber-600 text-white'
                : 'bg-amber-50 text-amber-700 hover:bg-amber-100'
            }`}>
            <Clock className="w-4 h-4" />
            Ожидают одобрения
            {filterTab !== 'pending' && pendingCount > 0 && (
              <span className="bg-amber-600 text-white text-xs px-2 py-0.5 rounded-full">
                {pendingCount}
              </span>
            )}
          </button>
          <button
            onClick={() => setFilterTab('approved')}
            className={`px-4 py-2 rounded-lg font-medium transition-colors ${
              filterTab === 'approved'
                ? 'bg-green-600 text-white'
                : 'bg-green-50 text-green-700 hover:bg-green-100'
            }`}>
            Одобренные
          </button>
        </div>

        {error && (
          <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-red-700">
            {error}
          </div>
        )}

        <div className="bg-white rounded-xl shadow-sm border border-slate-200 overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full">
              <thead className="bg-slate-50 border-b border-slate-200">
                <tr>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">
                    Пользователь
                  </th>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">
                    Email
                  </th>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">Роль</th>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">
                    Статус
                  </th>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">
                    Создан
                  </th>
                  <th className="px-6 py-4 text-right text-sm font-semibold text-slate-700">
                    Действия
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {users.map((user) => (
                  <tr key={user.id} className="hover:bg-slate-50 transition-colors">
                    <td className="px-6 py-4">
                      <div className="font-medium text-slate-900">{user.fullName}</div>
                    </td>
                    <td className="px-6 py-4 text-slate-600">{user.email}</td>
                    <td className="px-6 py-4">
                      <span
                        className={`inline-flex items-center gap-1 px-3 py-1 rounded-full text-xs font-medium ${
                          user.role.name === 'admin'
                            ? 'bg-red-100 text-red-700'
                            : user.role.name === 'manager'
                            ? 'bg-blue-100 text-blue-700'
                            : 'bg-slate-100 text-slate-700'
                        }`}>
                        {user.role.name}
                      </span>
                    </td>
                    <td className="px-6 py-4">
                      {user.isActive ? (
                        <span className="inline-flex items-center gap-1 text-green-700">
                          <CheckCircle className="w-4 h-4" />
                          <span className="text-sm">Активен</span>
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 text-red-700">
                          <XCircle className="w-4 h-4" />
                          <span className="text-sm">Неактивен</span>
                        </span>
                      )}
                    </td>
                    <td className="px-6 py-4 text-slate-600 text-sm">
                      {new Date(user.createdAt).toLocaleDateString()}
                    </td>
                    <td className="px-6 py-4">
                      <div className="flex items-center justify-end gap-2">
                        {!user.isApproved ? (
                          <>
                            <button
                              onClick={() => handleApprove(user.id)}
                              className="flex items-center gap-1 px-3 py-1.5 text-sm text-green-700 bg-green-50 hover:bg-green-100 rounded-lg transition-colors"
                              title="Одобрить пользователя">
                              <UserCheck className="w-4 h-4" />
                              Одобрить
                            </button>
                            <button
                              onClick={() => handleReject(user.id)}
                              className="flex items-center gap-1 px-3 py-1.5 text-sm text-red-700 bg-red-50 hover:bg-red-100 rounded-lg transition-colors"
                              title="Отклонить пользователя">
                              <UserX className="w-4 h-4" />
                              Отклонить
                            </button>
                          </>
                        ) : (
                          <>
                            <button
                              onClick={() => handleEdit(user)}
                              className="p-2 text-slate-600 hover:text-slate-900 hover:bg-slate-100 rounded-lg transition-colors"
                              title="Редактировать пользователя">
                              <Pencil className="w-4 h-4" />
                            </button>
                            <button
                              onClick={() => handleDelete(user.id)}
                              className="p-2 text-red-600 hover:text-red-700 hover:bg-red-50 rounded-lg transition-colors"
                              title="Удалить пользователя">
                              <Trash2 className="w-4 h-4" />
                            </button>
                          </>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {users.length === 0 && (
            <div className="text-center py-12 text-slate-500">
              <UserPlus className="w-12 h-12 mx-auto mb-4 opacity-50" />
              <p>Пользователи не найдены</p>
            </div>
          )}
        </div>
      </div>

      {showModal && <UserModal user={selectedUser} onClose={handleModalClose} />}
    </div>
  );
};
