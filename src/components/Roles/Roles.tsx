import { useState, useEffect } from 'react';
import { authService } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import { Shield, ShieldPlus, Pencil, Trash2, CheckCircle } from 'lucide-react';
import { RoleModal } from './RoleModal';

export interface Role {
  id: number;
  name: string;
  description: string;
  createdAt: string;
}

export const Roles = () => {
  const { isAdmin } = useAuth();
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showModal, setShowModal] = useState(false);
  const [selectedRole, setSelectedRole] = useState<Role | null>(null);

  useEffect(() => {
    fetchRoles();
  }, []);

  const fetchRoles = async () => {
    try {
      setLoading(true);
      const data = await authService.getRoles();
      setRoles(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить роли');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (roleId: number) => {
    if (!confirm('Удалить роль?')) return;
    try {
      await authService.deleteRole(roleId);
      await fetchRoles();
    } catch (err) {
      alert(err instanceof Error ? err.message : 'Не удалось удалить роль');
    }
  };

  const handleEdit = (role: Role) => {
    setSelectedRole(role);
    setShowModal(true);
  };

  const handleAdd = () => {
    setSelectedRole(null);
    setShowModal(true);
  };

  const handleModalClose = () => {
    setShowModal(false);
    setSelectedRole(null);
    fetchRoles();
  };

  if (!isAdmin()) {
    return (
      <div className="p-8">
        <div className="max-w-4xl mx-auto">
          <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
            <Shield className="w-12 h-12 text-red-600 mx-auto mb-4" />
            <h2 className="text-xl font-bold text-red-900 mb-2">Доступ запрещён</h2>
            <p className="text-red-700">
              Нужны права администратора для доступа к управлению ролями.
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
            <p className="mt-4 text-slate-600">Загрузка ролей…</p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="p-8">
      <div className="max-w-7xl mx-auto">
        <div className="flex items-center justify-between mb-8">
          <div>
            <h1 className="text-3xl font-bold text-slate-900">Управление ролями</h1>
            <p className="text-slate-600 mt-2">Управляйте ролями и их правами доступа</p>
          </div>
          <button
            onClick={handleAdd}
            className="flex items-center gap-2 px-4 py-2 bg-slate-900 text-white rounded-lg hover:bg-slate-800 transition-colors">
            <ShieldPlus className="w-5 h-5" />
            Добавить роль
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
                    Название
                  </th>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">
                    Описание
                  </th>
                  <th className="px-6 py-4 text-left text-sm font-semibold text-slate-700">
                    Создана
                  </th>
                  <th className="px-6 py-4 text-right text-sm font-semibold text-slate-700">
                    Действия
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {roles.map((role) => (
                  <tr key={role.id} className="hover:bg-slate-50 transition-colors">
                    <td className="px-6 py-4">
                      <div className="flex items-center gap-2">
                        <Shield className="w-4 h-4 text-slate-400" />
                        <span className="font-medium text-slate-900">{role.name}</span>
                      </div>
                    </td>
                    <td className="px-6 py-4 text-slate-600">{role.description || '—'}</td>
                    <td className="px-6 py-4 text-slate-600 text-sm">
                      {new Date(role.createdAt).toLocaleDateString()}
                    </td>
                    <td className="px-6 py-4">
                      <div className="flex items-center justify-end gap-2">
                        <button
                          onClick={() => handleEdit(role)}
                          className="p-2 text-slate-600 hover:text-slate-900 hover:bg-slate-100 rounded-lg transition-colors"
                          title="Редактировать роль">
                          <Pencil className="w-4 h-4" />
                        </button>
                        <button
                          onClick={() => handleDelete(role.id)}
                          className="p-2 text-red-600 hover:text-red-700 hover:bg-red-50 rounded-lg transition-colors"
                          title="Удалить роль">
                          <Trash2 className="w-4 h-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {roles.length === 0 && (
            <div className="text-center py-12 text-slate-500">
              <ShieldPlus className="w-12 h-12 mx-auto mb-4 opacity-50" />
              <p>Роли не найдены</p>
            </div>
          )}
        </div>
      </div>

      {showModal && <RoleModal role={selectedRole} onClose={handleModalClose} />}
    </div>
  );
};
