import { useEffect, useState } from 'react';
import { Save } from 'lucide-react';
import { apiService, type TaskJournalSettingsDto } from '../../services/api';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

const DEFAULT_SETTINGS: TaskJournalSettingsDto = {
  restrictExecutorEditing: true,
  allowExecutorDeadlineChangeRequests: true,
};

export function TaskJournalSettings() {
  const toast = useToast();
  const [settings, setSettings] = useState<TaskJournalSettingsDto>(DEFAULT_SETTINGS);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    apiService.getTaskJournalSettings()
      .then((response) => {
        if (active) {
          setSettings(response);
        }
      })
      .catch((error) => {
        if (active) {
          toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить настройки журнала задач.'), 'Журнал задач');
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [toast]);

  const save = async () => {
    setSaving(true);
    setMessage(null);
    try {
      const response = await apiService.updateTaskJournalSettings(settings);
      setSettings(response);
      setMessage('Настройки сохранены.');
      toast.success('Настройки журнала задач сохранены.', 'Журнал задач');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить настройки журнала задач.'), 'Журнал задач');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="mb-6">
        <h4 className="text-xl font-semibold text-gray-900">Журнал задач</h4>
      </div>

      <div className="space-y-3">
        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={settings.restrictExecutorEditing}
            onChange={(event) => setSettings((prev) => ({ ...prev, restrictExecutorEditing: event.target.checked }))}
            disabled={loading || saving}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span>
            <span className="block font-semibold text-gray-900">Исполнитель открывает задачу только на просмотр</span>
            <span className="mt-1 block text-gray-500">Поля задачи, срок, участники, файлы и удаление доступны постановщику или администратору.</span>
          </span>
        </label>

        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={settings.allowExecutorDeadlineChangeRequests}
            onChange={(event) => setSettings((prev) => ({ ...prev, allowExecutorDeadlineChangeRequests: event.target.checked }))}
            disabled={loading || saving || !settings.restrictExecutorEditing}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-60"
          />
          <span>
            <span className="block font-semibold text-gray-900">Разрешить запрос переноса срока</span>
            <span className="mt-1 block text-gray-500">Запрос фиксируется в истории задачи для постановщика без прямого изменения срока.</span>
          </span>
        </label>
      </div>

      <div className="mt-6 flex min-h-10 items-center justify-between gap-3">
        <span className="text-sm text-gray-600">{message}</span>
        <button
          type="button"
          onClick={() => void save()}
          disabled={loading || saving}
          className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <Save className="h-4 w-4" />
          {saving ? 'Сохранение...' : 'Сохранить'}
        </button>
      </div>
    </div>
  );
}
