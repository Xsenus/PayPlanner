import { useEffect, useState, type FormEvent } from 'react';
import { X } from 'lucide-react';
import { authService } from '../../services/authService';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { Role } from './Roles';

interface RoleModalProps {
  role: Role | null;
  onClose: () => void;
  onSaved?: () => void;
}

export const RoleModal = ({ role, onClose, onSaved }: RoleModalProps) => {
  const toast = useToast();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (role) {
      setName(role.name);
      setDescription(role.description || '');
    } else {
      setName('');
      setDescription('');
    }
    setError('');
  }, [role]);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setError('');

    if (!name.trim()) {
      const message = 'Название роли обязательно';
      setError(message);
      return;
    }

    try {
      setLoading(true);
      if (role) {
        await authService.updateRole(role.id, { name: name.trim(), description: description.trim() });
        toast.success('Роль обновлена');
      } else {
        await authService.createRole({ name: name.trim(), description: description.trim() });
        toast.success('Роль создана');
      }
      onSaved?.();
      onClose();
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить роль');
      setError(message);
      toast.error(message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div className="w-full max-w-md rounded-xl bg-white shadow-xl">
        <div className="flex items-center justify-between border-b border-slate-200 p-6">
          <h2 className="text-xl font-bold text-slate-900">
            {role ? 'Редактировать роль' : 'Добавить роль'}
          </h2>
          <button
            type="button"
            onClick={onClose}
            className="p-1 text-slate-400 transition-colors hover:text-slate-600"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4 p-6">
          {error ? (
            <div className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
              {error}
            </div>
          ) : null}

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700">
              Название роли <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={name}
              onChange={(event) => setName(event.target.value)}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 focus:border-transparent focus:ring-2 focus:ring-slate-900"
              placeholder="Введите название роли"
              required
            />
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700">Описание</label>
            <textarea
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              className="w-full resize-none rounded-lg border border-slate-300 px-3 py-2 focus:border-transparent focus:ring-2 focus:ring-slate-900"
              placeholder="Введите описание роли"
              rows={3}
            />
          </div>

          <div className="flex gap-3 pt-4">
            <button
              type="button"
              onClick={onClose}
              className="flex-1 rounded-lg border border-slate-300 px-4 py-2 text-slate-700 transition-colors hover:bg-slate-50"
            >
              Отмена
            </button>
            <button
              type="submit"
              disabled={loading}
              className="flex-1 rounded-lg bg-slate-900 px-4 py-2 text-white transition-colors hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {loading ? 'Сохранение...' : role ? 'Сохранить' : 'Создать'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
