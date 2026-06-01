import { CheckCircle2, Cloud, Edit3, KeyRound, Loader2, Plus, RefreshCw, Save, TestTube2, Trash2, XCircle } from 'lucide-react';
import { FormEvent, useEffect, useMemo, useState } from 'react';
import {
  apiService,
  type S3StorageConnectionDto,
  type S3StorageConnectionTestDto,
  type S3StorageConnectionsOverviewDto,
  type S3StorageProviderPresetDto,
  type UpsertS3StorageConnectionRequest,
} from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { defaultRestrictedMenuPermissions } from '../../types/permissions';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type S3StorageDraft = UpsertS3StorageConnectionRequest;

const defaultDraft: S3StorageDraft = {
  id: null,
  name: 'HOSTKEY S3',
  provider: 'hostkey',
  endpoint: 'https://s3-nl.hostkey.com',
  region: 'nl',
  bucket: '',
  rootPrefix: 'payplanner',
  accessKeyId: '',
  secretAccessKey: '',
  clearSecret: false,
  isEnabled: true,
};

function createDraftFromConnection(connection: S3StorageConnectionDto): S3StorageDraft {
  return {
    id: connection.id,
    name: connection.name,
    provider: connection.provider,
    endpoint: connection.endpoint,
    region: connection.region,
    bucket: connection.bucket,
    rootPrefix: connection.rootPrefix,
    accessKeyId: '',
    secretAccessKey: '',
    clearSecret: false,
    isEnabled: connection.isEnabled,
  };
}

function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString('ru-RU') : '-';
}

function appendS3Hint(message: string, hint: string) {
  const normalized = message.toLowerCase();
  const hintStart = hint.slice(0, 32).toLowerCase();
  return normalized.includes(hintStart) ? message : `${message} ${hint}`;
}

function extractS3StorageErrorMessage(error: unknown, fallback: string) {
  const message = extractDisplayErrorMessage(error, fallback);
  const raw = error instanceof Error ? error.message : String(error ?? '');
  const normalized = `${message} ${raw}`.toLowerCase();

  if (normalized.includes('accessdenied') || normalized.includes('forbidden') || normalized.includes('403') || normalized.includes('доступ')) {
    return appendS3Hint(message, 'Проверьте Access Key, Secret Key и права пользователя на bucket.');
  }

  if (normalized.includes('nosuchbucket') || normalized.includes('bucket') || normalized.includes('404')) {
    return appendS3Hint(message, 'Проверьте имя bucket в панели провайдера и доступ к нему.');
  }

  if (normalized.includes('signature') || normalized.includes('invalidaccesskey') || normalized.includes('key')) {
    return appendS3Hint(message, 'Проверьте ключи доступа и регион подключения.');
  }

  if (normalized.includes('endpoint') || normalized.includes('timeout') || normalized.includes('network') || normalized.includes('fetch')) {
    return appendS3Hint(message, 'Проверьте endpoint, HTTPS и доступность сети.');
  }

  return message;
}

function normalizeRootPrefix(value: string) {
  return value.trim().replace(/^\/+|\/+$/g, '');
}

function providerLabel(provider: string, presets: S3StorageProviderPresetDto[]) {
  return presets.find((preset) => preset.key === provider)?.name ?? (provider === 'custom' ? 'S3-совместимое' : provider);
}

export function S3Storage() {
  const toast = useToast();
  const { user } = useAuth();
  const rolePermissions = useRolePermissions(user?.role?.id);
  const isAdminRole = (user?.role?.name ?? '').toLowerCase() === 'admin';
  const permissions = rolePermissions.s3Storage ?? defaultRestrictedMenuPermissions;
  const canView = isAdminRole || permissions.canView;
  const canCreate = isAdminRole || permissions.canCreate;
  const canEdit = isAdminRole || permissions.canEdit;
  const canDelete = isAdminRole || permissions.canDelete;
  const [overview, setOverview] = useState<S3StorageConnectionsOverviewDto | null>(null);
  const [draft, setDraft] = useState<S3StorageDraft>(defaultDraft);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [testingId, setTestingId] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [connectionTest, setConnectionTest] = useState<S3StorageConnectionTestDto | null>(null);

  const presets = overview?.providerPresets ?? [];
  const connections = overview?.connections ?? [];
  const editingConnection = useMemo(
    () => connections.find((connection) => connection.id === draft.id) ?? null,
    [connections, draft.id],
  );
  const canSaveDraft = draft.id ? canEdit : canCreate;
  const canTestConnections = canView && canEdit;

  const loadConnections = async () => {
    setLoading(true);
    setError(null);
    try {
      setOverview(await apiService.getS3StorageConnections());
    } catch (loadError) {
      setError(extractS3StorageErrorMessage(loadError, 'Не удалось загрузить S3-хранилища.'));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (canView) {
      void loadConnections();
    }
  }, [canView]);

  const applyPreset = (presetKey: string) => {
    const preset = presets.find((item) => item.key === presetKey);
    setDraft((current) => ({
      ...current,
      provider: presetKey,
      endpoint: preset?.endpoint ?? current.endpoint,
      region: preset?.region ?? current.region,
      name: current.id ? current.name : preset?.name ?? current.name,
    }));
  };

  const resetDraft = () => {
    setDraft(defaultDraft);
    setConnectionTest(null);
  };

  const submitConnection = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSaveDraft) {
      setError('Недостаточно прав для сохранения S3-хранилища.');
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const saved = await apiService.upsertS3StorageConnection({
        ...draft,
        endpoint: draft.endpoint.trim(),
        region: draft.region.trim(),
        bucket: draft.bucket.trim(),
        rootPrefix: normalizeRootPrefix(draft.rootPrefix),
        accessKeyId: draft.accessKeyId.trim(),
        secretAccessKey: draft.secretAccessKey.trim(),
      });
      toast.success('S3-хранилище сохранено.', 'S3');
      setDraft(createDraftFromConnection(saved));
      await loadConnections();
    } catch (saveError) {
      setError(extractS3StorageErrorMessage(saveError, 'Не удалось сохранить S3-хранилище.'));
    } finally {
      setSaving(false);
    }
  };

  const testConnection = async (connection: S3StorageConnectionDto) => {
    if (!canTestConnections) {
      setError('Недостаточно прав для проверки подключения.');
      return;
    }

    setTestingId(connection.id);
    setError(null);
    setConnectionTest(null);
    try {
      const result = await apiService.testS3StorageConnection(connection.id);
      setConnectionTest(result);
      if (result.success) {
        toast.success('Подключение к S3-хранилищу проверено.', 'S3');
      } else {
        setError('Проверка S3-хранилища завершилась с ошибками.');
      }
    } catch (testError) {
      setError(extractS3StorageErrorMessage(testError, 'Не удалось проверить подключение к S3-хранилищу.'));
    } finally {
      setTestingId(null);
    }
  };

  const deleteConnection = async (connection: S3StorageConnectionDto) => {
    if (!canDelete) {
      setError('Недостаточно прав для удаления S3-хранилища.');
      return;
    }

    if (!window.confirm(`Удалить подключение "${connection.name}"?`)) {
      return;
    }

    setDeletingId(connection.id);
    setError(null);
    try {
      await apiService.deleteS3StorageConnection(connection.id);
      toast.success('S3-хранилище удалено.', 'S3');
      if (draft.id === connection.id) {
        resetDraft();
      }
      await loadConnections();
    } catch (deleteError) {
      setError(extractS3StorageErrorMessage(deleteError, 'Не удалось удалить S3-хранилище.'));
    } finally {
      setDeletingId(null);
    }
  };

  if (!canView) {
    return (
      <div className="p-6">
        <div className="rounded-lg border border-slate-200 bg-white p-5 text-sm text-slate-700">
          Недостаточно прав для просмотра S3-хранилищ.
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-slate-50 p-4 sm:p-6">
      <div className="mx-auto flex max-w-7xl flex-col gap-5">
        <header className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-700">
              <Cloud className="h-4 w-4" />
              <span>S3-хранилища</span>
            </div>
            <h2 className="mt-1 text-xl font-semibold text-slate-900">Подключения S3</h2>
          </div>
          <button
            type="button"
            onClick={() => void loadConnections()}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-medium text-slate-700 shadow-sm transition hover:bg-slate-100"
            disabled={loading}>
            {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Обновить
          </button>
        </header>

        {error ? (
          <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800">
            {error}
          </div>
        ) : null}

        <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_25rem]">
          <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
            <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
              <h3 className="text-sm font-semibold text-slate-900">Список подключений</h3>
              <span className="text-xs text-slate-500">{connections.length} шт.</span>
            </div>

            {loading ? (
              <div className="flex items-center gap-2 px-4 py-8 text-sm text-slate-500">
                <Loader2 className="h-4 w-4 animate-spin" />
                Загружаем подключения
              </div>
            ) : connections.length ? (
              <div className="divide-y divide-slate-200">
                {connections.map((connection) => (
                  <article key={connection.id} className="px-4 py-4">
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <h4 className="truncate text-base font-semibold text-slate-900">{connection.name}</h4>
                          <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${connection.isEnabled ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-600'}`}>
                            {connection.isEnabled ? 'Включено' : 'Отключено'}
                          </span>
                          <span className="rounded-full bg-sky-50 px-2 py-0.5 text-xs font-medium text-sky-700">
                            {connection.providerName || providerLabel(connection.provider, presets)}
                          </span>
                        </div>
                        <div className="mt-2 grid gap-2 text-sm text-slate-600 md:grid-cols-2">
                          <div className="min-w-0">
                            <span className="text-slate-400">Endpoint: </span>
                            <span className="break-all font-mono text-xs text-slate-700">{connection.endpoint}</span>
                          </div>
                          <div>
                            <span className="text-slate-400">Region: </span>
                            <span className="font-mono text-xs text-slate-700">{connection.region || '-'}</span>
                          </div>
                          <div>
                            <span className="text-slate-400">Bucket: </span>
                            <span className="font-mono text-xs text-slate-700">{connection.bucket}</span>
                          </div>
                          <div>
                            <span className="text-slate-400">Папка: </span>
                            <span className="font-mono text-xs text-slate-700">{connection.rootPrefix || '/'}</span>
                          </div>
                          <div>
                            <span className="text-slate-400">Access Key: </span>
                            <span className="font-mono text-xs text-slate-700">{connection.accessKeyPreview || '-'}</span>
                          </div>
                          <div>
                            <span className="text-slate-400">Secret Key: </span>
                            <span className="font-mono text-xs text-slate-700">{connection.secretKeyPreview || '-'}</span>
                          </div>
                        </div>
                        <p className="mt-2 text-xs text-slate-500">Обновлено: {formatDate(connection.updatedAt)}</p>
                      </div>

                      <div className="flex flex-wrap gap-2 lg:justify-end">
                        <button
                          type="button"
                          onClick={() => setDraft(createDraftFromConnection(connection))}
                          className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100"
                          disabled={!canEdit}>
                          <Edit3 className="h-4 w-4" />
                          Изменить
                        </button>
                        <button
                          type="button"
                          onClick={() => void testConnection(connection)}
                          className="inline-flex items-center gap-2 rounded-lg border border-sky-200 bg-sky-50 px-3 py-2 text-sm font-medium text-sky-700 transition hover:bg-sky-100"
                          disabled={!canTestConnections || testingId === connection.id || !connection.isConfigured}>
                          {testingId === connection.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <TestTube2 className="h-4 w-4" />}
                          Проверить
                        </button>
                        <button
                          type="button"
                          onClick={() => void deleteConnection(connection)}
                          className="inline-flex items-center gap-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm font-medium text-rose-700 transition hover:bg-rose-100"
                          disabled={!canDelete || deletingId === connection.id}>
                          {deletingId === connection.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                          Удалить
                        </button>
                      </div>
                    </div>
                  </article>
                ))}
              </div>
            ) : (
              <div className="px-4 py-8 text-sm text-slate-500">
                Подключений пока нет. Создайте первое S3-хранилище справа.
              </div>
            )}
          </section>

          <aside className="rounded-lg border border-slate-200 bg-white shadow-sm">
            <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
              <h3 className="text-sm font-semibold text-slate-900">
                {draft.id ? 'Редактирование' : 'Новое подключение'}
              </h3>
              <button
                type="button"
                onClick={resetDraft}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-2.5 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-100">
                <Plus className="h-3.5 w-3.5" />
                Новое
              </button>
            </div>

            <form className="flex flex-col gap-4 p-4" onSubmit={(event) => void submitConnection(event)}>
              <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                Провайдер
                <select
                  value={draft.provider}
                  onChange={(event) => applyPreset(event.target.value)}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100">
                  {presets.map((preset) => (
                    <option key={preset.key} value={preset.key}>{preset.name}</option>
                  ))}
                  <option value="custom">S3-совместимое</option>
                </select>
              </label>

              <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                Название
                <input
                  value={draft.name}
                  onChange={(event) => setDraft((current) => ({ ...current, name: event.target.value }))}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                  required
                />
              </label>

              <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                Endpoint
                <input
                  value={draft.endpoint}
                  onChange={(event) => setDraft((current) => ({ ...current, endpoint: event.target.value }))}
                  className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                  placeholder="https://s3-nl.hostkey.com"
                  required
                />
              </label>

              <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-1">
                <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                  Region
                  <input
                    value={draft.region}
                    onChange={(event) => setDraft((current) => ({ ...current, region: event.target.value }))}
                    className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                    placeholder="nl"
                    required
                  />
                </label>

                <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                  Bucket
                  <input
                    value={draft.bucket}
                    onChange={(event) => setDraft((current) => ({ ...current, bucket: event.target.value }))}
                    className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                    required
                  />
                </label>
              </div>

              <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                Рабочая папка
                <input
                  value={draft.rootPrefix}
                  onChange={(event) => setDraft((current) => ({ ...current, rootPrefix: event.target.value }))}
                  className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                  placeholder="payplanner"
                />
              </label>

              <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                Access Key
                <input
                  value={draft.accessKeyId}
                  onChange={(event) => setDraft((current) => ({ ...current, accessKeyId: event.target.value }))}
                  className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                  placeholder={editingConnection?.accessKeyPreview ?? ''}
                  required={!draft.id}
                />
              </label>

              <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                Secret Key
                <input
                  type="password"
                  value={draft.secretAccessKey}
                  onChange={(event) => setDraft((current) => ({ ...current, secretAccessKey: event.target.value, clearSecret: false }))}
                  className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                  placeholder={editingConnection?.secretKeyPreview ?? ''}
                  required={!draft.id}
                />
              </label>

              {draft.id ? (
                <label className="flex items-start gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
                  <input
                    type="checkbox"
                    checked={draft.clearSecret}
                    onChange={(event) => setDraft((current) => ({ ...current, clearSecret: event.target.checked, secretAccessKey: event.target.checked ? '' : current.secretAccessKey }))}
                    className="mt-1 h-4 w-4 rounded border-slate-300 text-sky-600 focus:ring-sky-500"
                  />
                  <span>Очистить сохраненный Secret Key</span>
                </label>
              ) : null}

              <label className="flex items-start gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
                <input
                  type="checkbox"
                  checked={draft.isEnabled}
                  onChange={(event) => setDraft((current) => ({ ...current, isEnabled: event.target.checked }))}
                  className="mt-1 h-4 w-4 rounded border-slate-300 text-sky-600 focus:ring-sky-500"
                />
                <span>Включить подключение</span>
              </label>

              <button
                type="submit"
                className="inline-flex items-center justify-center gap-2 rounded-lg bg-sky-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-sky-700 disabled:cursor-not-allowed disabled:bg-slate-300"
                disabled={saving || !canSaveDraft}>
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Сохранить
              </button>

              <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-600">
                <div className="mb-1 flex items-center gap-2 font-semibold text-slate-800">
                  <KeyRound className="h-3.5 w-3.5" />
                  HOSTKEY
                </div>
                Для HOSTKEY используются endpoint https://s3-nl.hostkey.com, регион nl и HTTPS. Access Key и Secret Key берутся в панели HOSTKEY.
              </div>
            </form>
          </aside>
        </div>

        {connectionTest ? (
          <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
            <div className="flex flex-col gap-2 border-b border-slate-200 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <h3 className="text-sm font-semibold text-slate-900">Результат проверки</h3>
                <p className="mt-1 text-xs text-slate-500">
                  {connectionTest.bucket}, {connectionTest.region}, {formatDate(connectionTest.checkedAt)}
                </p>
              </div>
              <span className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs font-semibold ${connectionTest.success ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'}`}>
                {connectionTest.success ? <CheckCircle2 className="h-4 w-4" /> : <XCircle className="h-4 w-4" />}
                {connectionTest.success ? 'Успешно' : 'Есть ошибки'}
              </span>
            </div>
            <div className="divide-y divide-slate-200">
              {connectionTest.steps.map((step) => (
                <div key={step.key} className="flex flex-col gap-1 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
                  <div>
                    <div className="text-sm font-medium text-slate-900">{step.name}</div>
                    <div className="text-xs text-slate-500">{step.message}</div>
                  </div>
                  <span className={`inline-flex w-fit items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium ${step.success ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'}`}>
                    {step.success ? <CheckCircle2 className="h-3.5 w-3.5" /> : <XCircle className="h-3.5 w-3.5" />}
                    {step.success ? 'OK' : 'Ошибка'}
                  </span>
                </div>
              ))}
            </div>
          </section>
        ) : null}
      </div>
    </div>
  );
}
