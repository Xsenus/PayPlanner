import {
  ArrowUp,
  CheckCircle2,
  Cloud,
  Copy,
  Download,
  Edit3,
  File,
  Folder,
  KeyRound,
  Loader2,
  MoveRight,
  Plus,
  RefreshCw,
  Save,
  TestTube2,
  Trash2,
  UploadCloud,
  X,
  XCircle,
} from 'lucide-react';
import { ChangeEvent, FormEvent, useEffect, useMemo, useRef, useState } from 'react';
import {
  apiService,
  type S3StorageConnectionDto,
  type S3StorageConnectionTestDto,
  type S3StorageConnectionsOverviewDto,
  type S3StorageListResponseDto,
  type S3StorageObjectDto,
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

function formatSize(size?: number | null) {
  if (!size) return '0 Б';
  const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let value = size;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }
  return `${value.toLocaleString('ru-RU', { maximumFractionDigits: unitIndex === 0 ? 0 : 1 })} ${units[unitIndex]}`;
}

function joinPath(directory: string, name: string) {
  const cleanName = name.trim().replace(/^\/+/, '');
  if (!cleanName) return directory || '/';
  if (!directory || directory === '/') return `/${cleanName}`;
  return `${directory.replace(/\/+$/, '')}/${cleanName}`;
}

function parentPath(path: string) {
  const normalized = path === '/' ? '/' : path.replace(/\/+$/, '');
  const index = normalized.lastIndexOf('/');
  return index <= 0 ? '/' : normalized.slice(0, index);
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
  const canExport = isAdminRole || permissions.canExport;
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const [overview, setOverview] = useState<S3StorageConnectionsOverviewDto | null>(null);
  const [draft, setDraft] = useState<S3StorageDraft>(defaultDraft);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [selectedConnectionId, setSelectedConnectionId] = useState<string | null>(null);
  const [currentPath, setCurrentPath] = useState('/');
  const [listing, setListing] = useState<S3StorageListResponseDto | null>(null);
  const [listingLoading, setListingLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testingId, setTestingId] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [objectActionKey, setObjectActionKey] = useState<string | null>(null);
  const [connectionTest, setConnectionTest] = useState<S3StorageConnectionTestDto | null>(null);
  const [editorPath, setEditorPath] = useState<string | null>(null);
  const [editorContent, setEditorContent] = useState('');
  const [editorSaving, setEditorSaving] = useState(false);

  const presets = overview?.providerPresets ?? [];
  const connections = overview?.connections ?? [];
  const selectedConnection = useMemo(
    () => connections.find((connection) => connection.id === selectedConnectionId) ?? null,
    [connections, selectedConnectionId],
  );
  const editingConnection = useMemo(
    () => connections.find((connection) => connection.id === draft.id) ?? null,
    [connections, draft.id],
  );
  const canSaveDraft = draft.id ? canEdit : canCreate;
  const canTestConnections = canView && canEdit && canCreate && canDelete && canExport;

  const loadConnections = async () => {
    setLoading(true);
    setError(null);
    try {
      const loaded = await apiService.getS3StorageConnections();
      setOverview(loaded);
      setSelectedConnectionId((current) => {
        if (current && loaded.connections.some((connection) => connection.id === current)) {
          return current;
        }

        return loaded.connections.find((connection) => connection.isEnabled && connection.isConfigured)?.id
          ?? loaded.connections[0]?.id
          ?? null;
      });
    } catch (loadError) {
      setError(extractS3StorageErrorMessage(loadError, 'Не удалось загрузить S3-хранилища.'));
    } finally {
      setLoading(false);
    }
  };

  const loadObjects = async (path = currentPath, connectionId = selectedConnectionId) => {
    if (!connectionId) {
      setListing(null);
      return;
    }

    setListingLoading(true);
    setError(null);
    try {
      const loaded = await apiService.listS3StorageObjects(connectionId, path);
      setListing(loaded);
      setCurrentPath(loaded.current.path || '/');
    } catch (loadError) {
      setError(extractS3StorageErrorMessage(loadError, 'Не удалось загрузить файлы S3-хранилища.'));
    } finally {
      setListingLoading(false);
    }
  };

  useEffect(() => {
    if (canView) {
      void loadConnections();
    }
  }, [canView]);

  useEffect(() => {
    if (selectedConnectionId) {
      setCurrentPath('/');
      setEditorPath(null);
      void loadObjects('/', selectedConnectionId);
    } else {
      setListing(null);
    }
  }, [selectedConnectionId]);

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

  const openObject = (item: S3StorageObjectDto) => {
    if (item.type === 'folder') {
      void loadObjects(item.path);
    }
  };

  const createFolder = async () => {
    if (!selectedConnectionId || !canCreate) {
      setError('Недостаточно прав для создания папки.');
      return;
    }

    const name = window.prompt('Название новой папки');
    if (!name?.trim()) return;

    setObjectActionKey('create-folder');
    setError(null);
    try {
      await apiService.createS3StorageFolder(selectedConnectionId, joinPath(currentPath, name));
      toast.success('Папка создана.', 'S3');
      await loadObjects();
    } catch (createError) {
      setError(extractS3StorageErrorMessage(createError, 'Не удалось создать папку S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const uploadFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file || !selectedConnectionId) return;
    if (!canCreate) {
      setError('Недостаточно прав для загрузки файла.');
      return;
    }

    setObjectActionKey('upload');
    setError(null);
    try {
      await apiService.uploadS3StorageFile(selectedConnectionId, joinPath(currentPath, file.name), file, true);
      toast.success('Файл загружен.', 'S3');
      await loadObjects();
    } catch (uploadError) {
      setError(extractS3StorageErrorMessage(uploadError, 'Не удалось загрузить файл в S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const downloadObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canExport || item.type !== 'file') {
      return;
    }

    setObjectActionKey(`download:${item.path}`);
    setError(null);
    try {
      const result = await apiService.downloadS3StorageFile(selectedConnectionId, item.path);
      const url = URL.createObjectURL(result.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = result.fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (downloadError) {
      setError(extractS3StorageErrorMessage(downloadError, 'Не удалось скачать файл из S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const deleteObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canDelete) {
      setError('Недостаточно прав для удаления объекта.');
      return;
    }

    if (!window.confirm(`Удалить "${item.name}" из S3-хранилища?`)) return;

    setObjectActionKey(`delete:${item.path}`);
    setError(null);
    try {
      await apiService.deleteS3StorageObject(selectedConnectionId, item.path, item.type === 'folder');
      toast.success('Объект удален.', 'S3');
      await loadObjects();
    } catch (deleteError) {
      setError(extractS3StorageErrorMessage(deleteError, 'Не удалось удалить объект S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const copyObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canCreate) {
      setError('Недостаточно прав для копирования объекта.');
      return;
    }

    const target = window.prompt('Куда скопировать объект?', item.path);
    if (!target?.trim() || target.trim() === item.path) return;

    setObjectActionKey(`copy:${item.path}`);
    setError(null);
    try {
      await apiService.copyS3StorageObject(selectedConnectionId, item.path, target, false);
      toast.success('Объект скопирован.', 'S3');
      await loadObjects();
    } catch (copyError) {
      setError(extractS3StorageErrorMessage(copyError, 'Не удалось скопировать объект S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const moveObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canEdit) {
      setError('Недостаточно прав для перемещения объекта.');
      return;
    }

    const target = window.prompt('Куда переместить объект?', item.path);
    if (!target?.trim() || target.trim() === item.path) return;

    setObjectActionKey(`move:${item.path}`);
    setError(null);
    try {
      await apiService.moveS3StorageObject(selectedConnectionId, item.path, target, false);
      toast.success('Объект перемещен.', 'S3');
      await loadObjects();
    } catch (moveError) {
      setError(extractS3StorageErrorMessage(moveError, 'Не удалось переместить объект S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const openTextEditor = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canEdit || item.type !== 'file') {
      return;
    }

    setObjectActionKey(`edit:${item.path}`);
    setError(null);
    try {
      const file = await apiService.getS3StorageTextFile(selectedConnectionId, item.path);
      setEditorPath(file.path);
      setEditorContent(file.content);
    } catch (editorError) {
      setError(extractS3StorageErrorMessage(editorError, 'Не удалось открыть текстовый файл S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const saveTextEditor = async () => {
    if (!selectedConnectionId || !editorPath || !canEdit) {
      return;
    }

    setEditorSaving(true);
    setError(null);
    try {
      await apiService.saveS3StorageTextFile(selectedConnectionId, editorPath, editorContent, true);
      toast.success('Файл сохранен.', 'S3');
      setEditorPath(null);
      setEditorContent('');
      await loadObjects();
    } catch (editorError) {
      setError(extractS3StorageErrorMessage(editorError, 'Не удалось сохранить текстовый файл S3.'));
    } finally {
      setEditorSaving(false);
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
                          onClick={() => setSelectedConnectionId(connection.id)}
                          className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition ${
                            selectedConnectionId === connection.id
                              ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                              : 'border-slate-300 bg-white text-slate-700 hover:bg-slate-100'
                          }`}>
                          <Folder className="h-4 w-4" />
                          Файлы
                        </button>
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

        <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="flex flex-col gap-3 border-b border-slate-200 px-4 py-3 lg:flex-row lg:items-center lg:justify-between">
            <div className="min-w-0">
              <h3 className="text-sm font-semibold text-slate-900">Файлы S3</h3>
              <p className="mt-1 truncate text-xs text-slate-500">
                {selectedConnection ? `${selectedConnection.name}: ${currentPath}` : 'Выберите подключение для просмотра файлов'}
              </p>
            </div>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => void loadObjects(parentPath(currentPath))}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100"
                disabled={!selectedConnectionId || currentPath === '/' || listingLoading}>
                <ArrowUp className="h-4 w-4" />
                Вверх
              </button>
              <button
                type="button"
                onClick={() => void createFolder()}
                className="inline-flex items-center gap-2 rounded-lg border border-sky-200 bg-sky-50 px-3 py-2 text-sm font-medium text-sky-700 transition hover:bg-sky-100"
                disabled={!selectedConnectionId || !canCreate || objectActionKey === 'create-folder'}>
                {objectActionKey === 'create-folder' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Folder className="h-4 w-4" />}
                Папка
              </button>
              <input ref={fileInputRef} type="file" className="hidden" onChange={(event) => void uploadFile(event)} />
              <button
                type="button"
                onClick={() => fileInputRef.current?.click()}
                className="inline-flex items-center gap-2 rounded-lg border border-sky-200 bg-sky-50 px-3 py-2 text-sm font-medium text-sky-700 transition hover:bg-sky-100"
                disabled={!selectedConnectionId || !canCreate || objectActionKey === 'upload'}>
                {objectActionKey === 'upload' ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
                Загрузить
              </button>
              <button
                type="button"
                onClick={() => void loadObjects()}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100"
                disabled={!selectedConnectionId || listingLoading}>
                {listingLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Обновить
              </button>
            </div>
          </div>

          {!selectedConnectionId ? (
            <div className="px-4 py-8 text-sm text-slate-500">
              Выберите S3-подключение в списке выше.
            </div>
          ) : listingLoading ? (
            <div className="flex items-center gap-2 px-4 py-8 text-sm text-slate-500">
              <Loader2 className="h-4 w-4 animate-spin" />
              Загружаем файлы S3
            </div>
          ) : listing?.items.length ? (
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200 text-sm">
                <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="px-4 py-3">Название</th>
                    <th className="px-4 py-3">Размер</th>
                    <th className="px-4 py-3">Изменено</th>
                    <th className="px-4 py-3 text-right">Действия</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 bg-white">
                  {listing.items.map((item) => {
                    const actionInProgress = objectActionKey?.endsWith(`:${item.path}`);
                    return (
                      <tr key={`${item.type}:${item.path}`} className="hover:bg-slate-50">
                        <td className="max-w-[28rem] px-4 py-3">
                          <button
                            type="button"
                            onClick={() => openObject(item)}
                            className={`flex min-w-0 items-center gap-2 text-left ${item.type === 'folder' ? 'text-sky-700 hover:text-sky-800' : 'text-slate-800'}`}>
                            {item.type === 'folder' ? <Folder className="h-4 w-4 shrink-0" /> : <File className="h-4 w-4 shrink-0" />}
                            <span className="truncate font-medium">{item.name}</span>
                          </button>
                          <div className="mt-1 truncate font-mono text-xs text-slate-400">{item.path}</div>
                        </td>
                        <td className="whitespace-nowrap px-4 py-3 text-slate-600">
                          {item.type === 'folder' ? '-' : formatSize(item.size)}
                        </td>
                        <td className="whitespace-nowrap px-4 py-3 text-slate-600">{formatDate(item.lastModified)}</td>
                        <td className="px-4 py-3">
                          <div className="flex flex-wrap justify-end gap-2">
                            {item.type === 'file' ? (
                              <>
                                <button
                                  type="button"
                                  onClick={() => void downloadObject(item)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 bg-white text-slate-700 transition hover:bg-slate-100"
                                  title="Скачать"
                                  aria-label={`Скачать ${item.name}`}
                                  disabled={!canExport || actionInProgress}>
                                  <Download className="h-4 w-4" />
                                </button>
                                <button
                                  type="button"
                                  onClick={() => void openTextEditor(item)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 bg-white text-slate-700 transition hover:bg-slate-100"
                                  title="Редактировать текст"
                                  aria-label={`Редактировать ${item.name}`}
                                  disabled={!canEdit || actionInProgress}>
                                  <Edit3 className="h-4 w-4" />
                                </button>
                              </>
                            ) : null}
                            <button
                              type="button"
                              onClick={() => void copyObject(item)}
                              className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 bg-white text-slate-700 transition hover:bg-slate-100"
                              title="Копировать"
                              aria-label={`Копировать ${item.name}`}
                              disabled={!canCreate || actionInProgress}>
                              <Copy className="h-4 w-4" />
                            </button>
                            <button
                              type="button"
                              onClick={() => void moveObject(item)}
                              className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 bg-white text-slate-700 transition hover:bg-slate-100"
                              title="Переместить"
                              aria-label={`Переместить ${item.name}`}
                              disabled={!canEdit || actionInProgress}>
                              <MoveRight className="h-4 w-4" />
                            </button>
                            <button
                              type="button"
                              onClick={() => void deleteObject(item)}
                              className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-rose-200 bg-rose-50 text-rose-700 transition hover:bg-rose-100"
                              title="Удалить"
                              aria-label={`Удалить ${item.name}`}
                              disabled={!canDelete || actionInProgress}>
                              {actionInProgress ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                            </button>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="px-4 py-8 text-sm text-slate-500">
              В этой папке S3 пока нет объектов.
            </div>
          )}
        </section>

        {editorPath ? (
          <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
            <div className="flex flex-col gap-3 border-b border-slate-200 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="min-w-0">
                <h3 className="text-sm font-semibold text-slate-900">Текстовый редактор S3</h3>
                <p className="mt-1 truncate font-mono text-xs text-slate-500">{editorPath}</p>
              </div>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => {
                    setEditorPath(null);
                    setEditorContent('');
                  }}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100">
                  <X className="h-4 w-4" />
                  Закрыть
                </button>
                <button
                  type="button"
                  onClick={() => void saveTextEditor()}
                  className="inline-flex items-center gap-2 rounded-lg bg-sky-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-sky-700 disabled:bg-slate-300"
                  disabled={editorSaving || !canEdit}>
                  {editorSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                  Сохранить
                </button>
              </div>
            </div>
            <div className="p-4">
              <textarea
                value={editorContent}
                onChange={(event) => setEditorContent(event.target.value)}
                className="min-h-64 w-full rounded-lg border border-slate-300 p-3 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              />
            </div>
          </section>
        ) : null}

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
