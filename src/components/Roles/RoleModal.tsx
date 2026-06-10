import { useEffect, useState, type FormEvent } from 'react';
import { Copy, Pencil, ShieldPlus, X } from 'lucide-react';
import { authService } from '../../services/authService';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { Role } from './types';

interface RoleModalProps {
  role: Role | null;
  copySourceRole?: Role | null;
  onClose: () => void;
  onSaved?: () => void;
}

export const RoleModal = ({ role, copySourceRole = null, onClose, onSaved }: RoleModalProps) => {
  const toast = useToast();
  const isEditMode = Boolean(role);
  const isCopyMode = Boolean(copySourceRole) && !role;

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (role) {
      setName(role.name);
      setDescription(role.description || '');
    } else if (copySourceRole) {
      setName('');
      setDescription(copySourceRole.description || '');
    } else {
      setName('');
      setDescription('');
    }

    setError('');
  }, [copySourceRole, role]);

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
        await authService.updateRole(role.id, {
          name: name.trim(),
          description: description.trim(),
        });
        toast.success('Роль обновлена');
      } else if (copySourceRole) {
        await authService.copyRole(copySourceRole.id, {
          name: name.trim(),
          description: description.trim(),
        });
        toast.success('Роль скопирована');
      } else {
        await authService.createRole({
          name: name.trim(),
          description: description.trim(),
        });
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

  const title = isEditMode ? 'Редактировать роль' : isCopyMode ? 'Скопировать роль' : 'Добавить роль';
  const submitLabel = loading ? 'Сохранение...' : isEditMode ? 'Сохранить' : isCopyMode ? 'Скопировать' : 'Создать';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div className="w-full max-w-2xl overflow-hidden rounded-[28px] bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 px-6 py-5">
          <div className="flex items-start gap-3">
            <div className="rounded-2xl bg-slate-100 p-3 text-slate-700">
              {isEditMode ? (
                <Pencil className="h-5 w-5" />
              ) : isCopyMode ? (
                <Copy className="h-5 w-5" />
              ) : (
                <ShieldPlus className="h-5 w-5" />
              )}
            </div>
            <div>
              <h2 className="text-2xl font-semibold text-slate-900">{title}</h2>
              <p className="mt-1 text-sm text-slate-500">
                {isCopyMode
                  ? 'Новая роль создаётся на основе выбранной. Имя нужно задать заново.'
                  : 'Укажите название и описание. Права доступа настраиваются отдельно.'}
              </p>
            </div>
          </div>

          <button
            type="button"
            onClick={onClose}
            className="rounded-lg p-2 text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-600"
            aria-label="Закрыть"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="space-y-6 px-6 py-6">
          {error ? (
            <div className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
              {error}
            </div>
          ) : null}

          {isCopyMode && copySourceRole ? (
            <div className="rounded-2xl border border-blue-200 bg-blue-50/70 p-4 text-sm text-slate-700">
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-blue-700">
                Источник копирования
              </div>
              <div className="mt-2 text-base font-semibold text-slate-900">{copySourceRole.name}</div>
              <div className="mt-2 text-sm text-slate-600">
                Базовые права и описание будут взяты из выбранной роли. При необходимости описание можно поменять сразу.
              </div>
            </div>
          ) : null}

          <div className="grid gap-4">
            <div className="rounded-2xl border border-slate-200 bg-slate-50 p-5">
              <label className="mb-2 block text-sm font-medium text-slate-700">
                Название роли <span className="text-red-500">*</span>
              </label>
              <input
                type="text"
                value={name}
                onChange={(event) => setName(event.target.value)}
                className="w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 focus:border-transparent focus:ring-2 focus:ring-slate-900"
                placeholder="Например: Менеджер по клиентам"
                required
              />
              <p className="mt-2 text-xs text-slate-500">
                Имя роли должно быть уникальным и понятно описывать зону ответственности.
              </p>
            </div>

            <div className="rounded-2xl border border-slate-200 bg-slate-50 p-5">
              <label className="mb-2 block text-sm font-medium text-slate-700">Описание</label>
              <textarea
                value={description}
                onChange={(event) => setDescription(event.target.value)}
                className="w-full resize-none rounded-xl border border-slate-300 bg-white px-3 py-2.5 focus:border-transparent focus:ring-2 focus:ring-slate-900"
                placeholder="Коротко опишите, кому и для чего назначается эта роль"
                rows={4}
              />
            </div>
          </div>

          <div className="flex flex-col gap-3 border-t border-slate-200 pt-5 sm:flex-row sm:justify-end">
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg border border-slate-300 px-4 py-2.5 text-slate-700 transition-colors hover:bg-slate-50"
            >
              Отмена
            </button>
            <button
              type="submit"
              disabled={loading}
              className="rounded-lg bg-slate-900 px-4 py-2.5 text-white transition-colors hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {submitLabel}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
