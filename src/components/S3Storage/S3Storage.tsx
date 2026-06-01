import {
  ArrowLeft,
  ArrowUp,
  CheckCircle2,
  Cloud,
  Copy,
  Download,
  Edit3,
  File,
  Files,
  Folder,
  History,
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
type S3StorageTab = 'connections' | 'files' | 'logs';
type S3StorageMode = 'overview' | 'form';

type S3StorageLogEntry = {
  id: string;
  title: string;
  description: string;
  createdAt: string;
  tone: 'success' | 'error' | 'info';
};

const defaultDraft: S3StorageDraft = {
  id: null,
  name: 'HOSTKEY S3',
  provider: 'hostkey',
  endpoint: 'https://s3-nl.hostkey.com',
  region: 'nl',
  bucket: '',
  rootPrefix: '',
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

function iconButtonClass(tone: 'default' | 'primary' | 'success' | 'danger' = 'default') {
  const base = 'inline-flex min-h-9 items-center justify-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition disabled:cursor-not-allowed disabled:opacity-55';
  switch (tone) {
    case 'primary':
      return `${base} border-sky-600 bg-sky-600 text-white shadow-sm hover:bg-sky-700`;
    case 'success':
      return `${base} border-emerald-200 bg-emerald-50 text-emerald-700 hover:bg-emerald-100`;
    case 'danger':
      return `${base} border-rose-200 bg-rose-50 text-rose-700 hover:bg-rose-100`;
    default:
      return `${base} border-slate-300 bg-white text-slate-700 shadow-sm hover:bg-slate-100`;
  }
}

function cardIconButtonClass(tone: 'default' | 'success' | 'danger' = 'default') {
  const base = 'inline-flex h-9 w-9 items-center justify-center rounded-lg border text-sm transition disabled:cursor-not-allowed disabled:opacity-55';
  switch (tone) {
    case 'success':
      return `${base} border-emerald-200 bg-emerald-50 text-emerald-700 hover:bg-emerald-100`;
    case 'danger':
      return `${base} border-rose-200 bg-rose-50 text-rose-700 hover:bg-rose-100`;
    default:
      return `${base} border-slate-300 bg-white text-slate-700 shadow-sm hover:bg-slate-100`;
  }
}

function statusBadge(connection: S3StorageConnectionDto) {
  if (!connection.isEnabled) {
    return <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">Выключено</span>;
  }

  if (!connection.isConfigured) {
    return <span className="rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700">Не настроено</span>;
  }

  return <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">Включено</span>;
}

function logToneClass(tone: S3StorageLogEntry['tone']) {
  if (tone === 'success') return 'border-emerald-200 bg-emerald-50 text-emerald-800';
  if (tone === 'error') return 'border-rose-200 bg-rose-50 text-rose-800';
  return 'border-sky-200 bg-sky-50 text-sky-800';
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
  const [mode, setMode] = useState<S3StorageMode>('overview');
  const [activeTab, setActiveTab] = useState<S3StorageTab>('connections');
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
  const [logs, setLogs] = useState<S3StorageLogEntry[]>([]);

  const presets = useMemo(() => overview?.providerPresets ?? [], [overview]);
  const connections = useMemo(() => overview?.connections ?? [], [overview]);
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

  const addLog = (entry: Omit<S3StorageLogEntry, 'id' | 'createdAt'>) => {
    setLogs((current) => [
      {
        ...entry,
        id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        createdAt: new Date().toISOString(),
      },
      ...current,
    ].slice(0, 50));
  };

  const showError = (message: string) => {
    setError(message);
    toast.error(message, 'S3');
    addLog({ title: 'Ошибка', description: message, tone: 'error' });
  };

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
      return loaded;
    } catch (loadError) {
      showError(extractS3StorageErrorMessage(loadError, 'Не удалось загрузить S3-хранилища.'));
      return null;
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
      showError(extractS3StorageErrorMessage(loadError, 'Не удалось загрузить файлы S3-хранилища.'));
    } finally {
      setListingLoading(false);
    }
  };

  useEffect(() => {
    if (canView) {
      void loadConnections();
    }
    // Initial loading is tied to permission state; loadConnections is intentionally not a dependency.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [canView]);

  useEffect(() => {
    if (selectedConnectionId) {
      setCurrentPath('/');
      setEditorPath(null);
      void loadObjects('/', selectedConnectionId);
    } else {
      setListing(null);
    }
    // Changing the selected connection resets the browser to its root folder.
    // eslint-disable-next-line react-hooks/exhaustive-deps
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

  const openCreatePage = () => {
    setDraft(defaultDraft);
    setConnectionTest(null);
    setError(null);
    setMode('form');
  };

  const openEditPage = (connection: S3StorageConnectionDto) => {
    setDraft(createDraftFromConnection(connection));
    setConnectionTest(null);
    setError(null);
    setMode('form');
  };

  const submitConnection = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSaveDraft) {
      showError('Недостаточно прав для сохранения S3-хранилища.');
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
      const message = draft.id ? 'Подключение S3 обновлено.' : 'Подключение S3 создано.';
      toast.success(message, 'S3');
      addLog({ title: message, description: saved.name, tone: 'success' });
      setDraft(createDraftFromConnection(saved));
      setSelectedConnectionId(saved.id);
      const loaded = await loadConnections();
      if (loaded?.connections.some((connection) => connection.id === saved.id)) {
        setActiveTab('connections');
      }
    } catch (saveError) {
      showError(extractS3StorageErrorMessage(saveError, 'Не удалось сохранить S3-хранилище.'));
    } finally {
      setSaving(false);
    }
  };

  const testConnection = async (connection: S3StorageConnectionDto) => {
    if (!canTestConnections) {
      showError('Недостаточно прав для проверки подключения.');
      return;
    }

    setTestingId(connection.id);
    setError(null);
    setConnectionTest(null);
    try {
      const result = await apiService.testS3StorageConnection(connection.id);
      setConnectionTest(result);
      setActiveTab('logs');
      if (result.success) {
        toast.success('Подключение к S3-хранилищу проверено.', 'S3');
        addLog({ title: 'Проверка подключения успешна', description: connection.name, tone: 'success' });
      } else {
        showError('Проверка S3-хранилища завершилась с ошибками.');
      }
    } catch (testError) {
      showError(extractS3StorageErrorMessage(testError, 'Не удалось проверить подключение к S3-хранилищу.'));
    } finally {
      setTestingId(null);
    }
  };

  const testDraftConnection = async () => {
    if (!draft.id) {
      showError('Сначала сохраните подключение, затем запустите проверку.');
      return;
    }

    const connection = connections.find((item) => item.id === draft.id);
    if (!connection) {
      showError('Сохраненное подключение не найдено. Обновите список и повторите проверку.');
      return;
    }

    await testConnection(connection);
  };

  const deleteConnection = async (connection: S3StorageConnectionDto) => {
    if (!canDelete) {
      showError('Недостаточно прав для удаления S3-хранилища.');
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
      addLog({ title: 'Подключение удалено', description: connection.name, tone: 'success' });
      if (draft.id === connection.id) {
        setDraft(defaultDraft);
        setMode('overview');
      }
      await loadConnections();
    } catch (deleteError) {
      showError(extractS3StorageErrorMessage(deleteError, 'Не удалось удалить S3-хранилище.'));
    } finally {
      setDeletingId(null);
    }
  };

  const openConnectionFiles = (connection: S3StorageConnectionDto) => {
    setSelectedConnectionId(connection.id);
    setActiveTab('files');
  };

  const openObject = (item: S3StorageObjectDto) => {
    if (item.type === 'folder') {
      void loadObjects(item.path);
    }
  };

  const createFolder = async () => {
    if (!selectedConnectionId || !canCreate) {
      showError('Недостаточно прав для создания папки.');
      return;
    }

    const name = window.prompt('Название новой папки');
    if (!name?.trim()) return;

    setObjectActionKey('create-folder');
    setError(null);
    try {
      await apiService.createS3StorageFolder(selectedConnectionId, joinPath(currentPath, name));
      toast.success('Папка создана.', 'S3');
      addLog({ title: 'Папка создана', description: joinPath(currentPath, name), tone: 'success' });
      await loadObjects();
    } catch (createError) {
      showError(extractS3StorageErrorMessage(createError, 'Не удалось создать папку S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const uploadFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file || !selectedConnectionId) return;
    if (!canCreate) {
      showError('Недостаточно прав для загрузки файла.');
      return;
    }

    setObjectActionKey('upload');
    setError(null);
    try {
      await apiService.uploadS3StorageFile(selectedConnectionId, joinPath(currentPath, file.name), file, true);
      toast.success('Файл загружен.', 'S3');
      addLog({ title: 'Файл загружен', description: joinPath(currentPath, file.name), tone: 'success' });
      await loadObjects();
    } catch (uploadError) {
      showError(extractS3StorageErrorMessage(uploadError, 'Не удалось загрузить файл в S3.'));
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
      addLog({ title: 'Файл скачан', description: item.path, tone: 'success' });
    } catch (downloadError) {
      showError(extractS3StorageErrorMessage(downloadError, 'Не удалось скачать файл из S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const deleteObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canDelete) {
      showError('Недостаточно прав для удаления объекта.');
      return;
    }

    if (!window.confirm(`Удалить "${item.name}" из S3-хранилища?`)) return;

    setObjectActionKey(`delete:${item.path}`);
    setError(null);
    try {
      await apiService.deleteS3StorageObject(selectedConnectionId, item.path, item.type === 'folder');
      toast.success('Объект удален.', 'S3');
      addLog({ title: 'Объект удален', description: item.path, tone: 'success' });
      await loadObjects();
    } catch (deleteError) {
      showError(extractS3StorageErrorMessage(deleteError, 'Не удалось удалить объект S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const copyObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canCreate) {
      showError('Недостаточно прав для копирования объекта.');
      return;
    }

    const target = window.prompt('Куда скопировать объект?', item.path);
    if (!target?.trim() || target.trim() === item.path) return;

    setObjectActionKey(`copy:${item.path}`);
    setError(null);
    try {
      await apiService.copyS3StorageObject(selectedConnectionId, item.path, target, false);
      toast.success('Объект скопирован.', 'S3');
      addLog({ title: 'Объект скопирован', description: `${item.path} -> ${target}`, tone: 'success' });
      await loadObjects();
    } catch (copyError) {
      showError(extractS3StorageErrorMessage(copyError, 'Не удалось скопировать объект S3.'));
    } finally {
      setObjectActionKey(null);
    }
  };

  const moveObject = async (item: S3StorageObjectDto) => {
    if (!selectedConnectionId || !canEdit) {
      showError('Недостаточно прав для перемещения объекта.');
      return;
    }

    const target = window.prompt('Куда переместить объект?', item.path);
    if (!target?.trim() || target.trim() === item.path) return;

    setObjectActionKey(`move:${item.path}`);
    setError(null);
    try {
      await apiService.moveS3StorageObject(selectedConnectionId, item.path, target, false);
      toast.success('Объект перемещен.', 'S3');
      addLog({ title: 'Объект перемещен', description: `${item.path} -> ${target}`, tone: 'success' });
      await loadObjects();
    } catch (moveError) {
      showError(extractS3StorageErrorMessage(moveError, 'Не удалось переместить объект S3.'));
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
      showError(extractS3StorageErrorMessage(editorError, 'Не удалось открыть текстовый файл S3.'));
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
      addLog({ title: 'Текстовый файл сохранен', description: editorPath, tone: 'success' });
      setEditorPath(null);
      setEditorContent('');
      await loadObjects();
    } catch (editorError) {
      showError(extractS3StorageErrorMessage(editorError, 'Не удалось сохранить текстовый файл S3.'));
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

  if (mode === 'form') {
    return (
      <div className="min-h-screen w-full bg-slate-50 px-4 py-5 sm:px-6 lg:px-8">
        <div className="flex w-full flex-col gap-5">
          <header className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div>
              <button
                type="button"
                onClick={() => setMode('overview')}
                className={iconButtonClass()}
                title="Вернуться к S3-хранилищам">
                <ArrowLeft className="h-4 w-4" />
                К S3-хранилищам
              </button>
              <div className="mt-4 flex items-center gap-2 text-sm font-semibold text-sky-700">
                <Cloud className="h-4 w-4" />
                <span>{draft.id ? 'Редактирование подключения' : 'Новое подключение'}</span>
              </div>
              <h2 className="mt-1 text-2xl font-semibold text-slate-900">
                {draft.id ? draft.name || 'S3-подключение' : 'Создание S3-подключения'}
              </h2>
            </div>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => void testDraftConnection()}
                className={iconButtonClass('success')}
                disabled={!draft.id || testingId === draft.id || !canTestConnections}
                title="Проверить сохраненное подключение">
                {testingId === draft.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <TestTube2 className="h-4 w-4" />}
                Проверить
              </button>
              <button
                type="submit"
                form="s3-storage-form"
                className={iconButtonClass('primary')}
                disabled={saving || !canSaveDraft}
                title="Сохранить подключение">
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Сохранить
              </button>
            </div>
          </header>

          {error ? (
            <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800">
              {error}
            </div>
          ) : null}

          <div className="grid w-full gap-5 xl:grid-cols-[minmax(0,1fr)_28rem]">
            <form
              id="s3-storage-form"
              className="rounded-lg border border-slate-200 bg-white p-5 shadow-sm"
              autoComplete="off"
              onSubmit={(event) => void submitConnection(event)}>
              <div className="grid gap-4 lg:grid-cols-2">
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

                <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700 lg:col-span-2">
                  Endpoint
                  <input
                    value={draft.endpoint}
                    onChange={(event) => setDraft((current) => ({ ...current, endpoint: event.target.value }))}
                    className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                    placeholder="https://s3-nl.hostkey.com"
                    required
                  />
                </label>

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

                <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700 lg:col-span-2">
                  Рабочая папка <span className="font-normal text-slate-500">(необязательно)</span>
                  <input
                    value={draft.rootPrefix}
                    onChange={(event) => setDraft((current) => ({ ...current, rootPrefix: event.target.value }))}
                    className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                    placeholder="Например payplanner/backups или оставьте пустым для корня bucket"
                  />
                </label>

                <label className="flex flex-col gap-1.5 text-sm font-medium text-slate-700">
                  Access Key
                  <input
                    name="s3-storage-access-key-id"
                    autoComplete="new-password"
                    autoCorrect="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    data-lpignore="true"
                    data-1p-ignore="true"
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
                    name="s3-storage-secret-access-key"
                    autoComplete="new-password"
                    autoCorrect="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    data-lpignore="true"
                    data-1p-ignore="true"
                    value={draft.secretAccessKey}
                    onChange={(event) => setDraft((current) => ({ ...current, secretAccessKey: event.target.value, clearSecret: false }))}
                    className="rounded-lg border border-slate-300 px-3 py-2 font-mono text-sm text-slate-900 shadow-sm focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                    placeholder={editingConnection?.secretKeyPreview ?? ''}
                    required={!draft.id}
                  />
                </label>
              </div>

              <div className="mt-5 grid gap-3 lg:grid-cols-2">
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
              </div>
            </form>

            <aside className="flex flex-col gap-4">
              <div className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
                <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                  <KeyRound className="h-4 w-4 text-sky-600" />
                  HOSTKEY
                </div>
                <p className="mt-2 text-sm leading-6 text-slate-600">
                  Для HOSTKEY используются endpoint https://s3-nl.hostkey.com, регион nl и HTTPS. Access Key и Secret Key берутся в панели HOSTKEY.
                </p>
              </div>

              <div className="rounded-lg border border-slate-200 bg-white shadow-sm">
                <div className="border-b border-slate-200 px-4 py-3">
                  <h3 className="text-sm font-semibold text-slate-900">Результат проверки</h3>
                </div>
                {connectionTest ? renderConnectionTest(connectionTest) : (
                  <div className="px-4 py-6 text-sm text-slate-500">
                    Проверка появится здесь после запуска кнопки «Проверить».
                  </div>
                )}
              </div>
            </aside>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen w-full bg-slate-50 px-4 py-5 sm:px-6 lg:px-8">
      <div className="flex w-full flex-col gap-5">
        <header className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div>
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-700">
              <Cloud className="h-4 w-4" />
              <span>S3-хранилища</span>
            </div>
            <h2 className="mt-1 text-2xl font-semibold text-slate-900">Управление S3</h2>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void loadConnections()}
              className={iconButtonClass()}
              disabled={loading}
              title="Обновить список подключений">
              {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
              Обновить
            </button>
            <button
              type="button"
              onClick={openCreatePage}
              className={iconButtonClass('primary')}
              disabled={!canCreate}
              title="Добавить S3-подключение">
              <Plus className="h-4 w-4" />
              Новое подключение
            </button>
          </div>
        </header>

        {error ? (
          <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800">
            {error}
          </div>
        ) : null}

        <nav className="flex flex-wrap gap-2 rounded-lg border border-slate-200 bg-white p-2 shadow-sm" aria-label="Разделы S3">
          {[
            { id: 'connections' as const, label: 'Подключения', icon: Cloud },
            { id: 'files' as const, label: 'Файлы', icon: Files },
            { id: 'logs' as const, label: 'Логи', icon: History },
          ].map((tab) => {
            const Icon = tab.icon;
            const active = activeTab === tab.id;
            return (
              <button
                key={tab.id}
                type="button"
                onClick={() => setActiveTab(tab.id)}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${
                  active ? 'bg-sky-600 text-white shadow-sm' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'
                }`}
                title={tab.label}>
                <Icon className="h-4 w-4" />
                {tab.label}
              </button>
            );
          })}
        </nav>

        {activeTab === 'connections' ? renderConnectionsTab() : null}
        {activeTab === 'files' ? renderFilesTab() : null}
        {activeTab === 'logs' ? renderLogsTab() : null}
      </div>
    </div>
  );

  function renderConnectionsTab() {
    if (loading) {
      return (
        <div className="flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-8 text-sm text-slate-500 shadow-sm">
          <Loader2 className="h-4 w-4 animate-spin" />
          Загружаем подключения
        </div>
      );
    }

    if (!connections.length) {
      return (
        <div className="rounded-lg border border-slate-200 bg-white px-4 py-8 text-sm text-slate-500 shadow-sm">
          Подключений пока нет. Создайте первое S3-хранилище.
        </div>
      );
    }

    return (
      <section className="grid w-full gap-4 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
        {connections.map((connection) => (
          <article key={connection.id} className="flex min-h-[18rem] flex-col rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <h3 className="truncate text-base font-semibold text-slate-900">{connection.name}</h3>
                <div className="mt-2 flex flex-wrap gap-2">
                  {statusBadge(connection)}
                  <span className="rounded-full bg-sky-50 px-2 py-0.5 text-xs font-medium text-sky-700">
                    {connection.providerName || providerLabel(connection.provider, presets)}
                  </span>
                </div>
              </div>
              <Cloud className="h-5 w-5 shrink-0 text-sky-600" />
            </div>

            <dl className="mt-4 grid gap-3 text-sm">
              <div>
                <dt className="text-xs font-medium uppercase text-slate-400">Endpoint</dt>
                <dd className="mt-1 break-all font-mono text-xs text-slate-700">{connection.endpoint}</dd>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <dt className="text-xs font-medium uppercase text-slate-400">Region</dt>
                  <dd className="mt-1 font-mono text-xs text-slate-700">{connection.region || '-'}</dd>
                </div>
                <div>
                  <dt className="text-xs font-medium uppercase text-slate-400">Bucket</dt>
                  <dd className="mt-1 truncate font-mono text-xs text-slate-700" title={connection.bucket}>{connection.bucket}</dd>
                </div>
              </div>
              <div>
                <dt className="text-xs font-medium uppercase text-slate-400">Рабочая папка</dt>
                <dd className="mt-1 font-mono text-xs text-slate-700">{connection.rootPrefix || 'Корень bucket'}</dd>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <dt className="text-xs font-medium uppercase text-slate-400">Access Key</dt>
                  <dd className="mt-1 font-mono text-xs text-slate-700">{connection.accessKeyPreview || '-'}</dd>
                </div>
                <div>
                  <dt className="text-xs font-medium uppercase text-slate-400">Secret Key</dt>
                  <dd className="mt-1 font-mono text-xs text-slate-700">{connection.secretKeyPreview || '-'}</dd>
                </div>
              </div>
            </dl>

            <p className="mt-4 text-xs text-slate-500">Обновлено: {formatDate(connection.updatedAt)}</p>

            <div className="mt-auto flex items-center justify-end gap-2 pt-4">
              <button
                type="button"
                onClick={() => openConnectionFiles(connection)}
                className={cardIconButtonClass('success')}
                title="Открыть файлы подключения"
                aria-label="Файлы">
                <Folder className="h-4 w-4" />
              </button>
              <button
                type="button"
                onClick={() => openEditPage(connection)}
                className={cardIconButtonClass()}
                disabled={!canEdit}
                title="Редактировать подключение"
                aria-label="Изменить">
                <Edit3 className="h-4 w-4" />
              </button>
              <button
                type="button"
                onClick={() => void testConnection(connection)}
                className={cardIconButtonClass()}
                disabled={!canTestConnections || testingId === connection.id || !connection.isConfigured}
                title="Проверить подключение"
                aria-label="Проверить">
                {testingId === connection.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <TestTube2 className="h-4 w-4" />}
              </button>
              <button
                type="button"
                onClick={() => void deleteConnection(connection)}
                className={cardIconButtonClass('danger')}
                disabled={!canDelete || deletingId === connection.id}
                title="Удалить подключение"
                aria-label="Удалить">
                {deletingId === connection.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
              </button>
            </div>
          </article>
        ))}
      </section>
    );
  }

  function renderFilesTab() {
    return (
      <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-col gap-3 border-b border-slate-200 px-4 py-3 xl:flex-row xl:items-center xl:justify-between">
          <div className="min-w-0">
            <h3 className="text-sm font-semibold text-slate-900">Файлы S3</h3>
            <p className="mt-1 truncate text-xs text-slate-500">
              {selectedConnection ? `${selectedConnection.name}: ${currentPath}` : 'Выберите подключение для просмотра файлов'}
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <select
              value={selectedConnectionId ?? ''}
              onChange={(event) => setSelectedConnectionId(event.target.value || null)}
              className="min-h-9 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm">
              <option value="">Выберите подключение</option>
              {connections.map((connection) => (
                <option key={connection.id} value={connection.id}>{connection.name}</option>
              ))}
            </select>
            <button type="button" onClick={() => void loadObjects(parentPath(currentPath))} className={iconButtonClass()} disabled={!selectedConnectionId || currentPath === '/' || listingLoading} title="Перейти на уровень выше">
              <ArrowUp className="h-4 w-4" />
              Вверх
            </button>
            <button type="button" onClick={() => void createFolder()} className={iconButtonClass()} disabled={!selectedConnectionId || !canCreate || objectActionKey === 'create-folder'} title="Создать папку">
              {objectActionKey === 'create-folder' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Folder className="h-4 w-4" />}
              Папка
            </button>
            <input ref={fileInputRef} type="file" className="hidden" onChange={(event) => void uploadFile(event)} />
            <button type="button" onClick={() => fileInputRef.current?.click()} className={iconButtonClass()} disabled={!selectedConnectionId || !canCreate || objectActionKey === 'upload'} title="Загрузить файл">
              {objectActionKey === 'upload' ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
              Загрузить
            </button>
            <button type="button" onClick={() => void loadObjects()} className={iconButtonClass()} disabled={!selectedConnectionId || listingLoading} title="Обновить файлы">
              {listingLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
              Обновить
            </button>
          </div>
        </div>

        {!selectedConnectionId ? (
          <div className="px-4 py-8 text-sm text-slate-500">Выберите S3-подключение.</div>
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
                      <td className="max-w-[32rem] px-4 py-3">
                        <button type="button" onClick={() => openObject(item)} className={`flex min-w-0 items-center gap-2 text-left ${item.type === 'folder' ? 'text-sky-700 hover:text-sky-800' : 'text-slate-800'}`}>
                          {item.type === 'folder' ? <Folder className="h-4 w-4 shrink-0" /> : <File className="h-4 w-4 shrink-0" />}
                          <span className="truncate font-medium">{item.name}</span>
                        </button>
                        <div className="mt-1 truncate font-mono text-xs text-slate-400">{item.path}</div>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-slate-600">{item.type === 'folder' ? '-' : formatSize(item.size)}</td>
                      <td className="whitespace-nowrap px-4 py-3 text-slate-600">{formatDate(item.lastModified)}</td>
                      <td className="px-4 py-3">
                        <div className="flex flex-wrap justify-end gap-2">
                          {item.type === 'file' ? (
                            <>
                              <button type="button" onClick={() => void downloadObject(item)} className={iconButtonClass()} title="Скачать файл" aria-label={`Скачать ${item.name}`} disabled={!canExport || actionInProgress}>
                                <Download className="h-4 w-4" />
                                Скачать
                              </button>
                              <button type="button" onClick={() => void openTextEditor(item)} className={iconButtonClass()} title="Редактировать текст" aria-label={`Редактировать ${item.name}`} disabled={!canEdit || actionInProgress}>
                                <Edit3 className="h-4 w-4" />
                                Редактировать
                              </button>
                            </>
                          ) : null}
                          <button type="button" onClick={() => void copyObject(item)} className={iconButtonClass()} title="Копировать объект" aria-label={`Копировать ${item.name}`} disabled={!canCreate || actionInProgress}>
                            <Copy className="h-4 w-4" />
                            Копировать
                          </button>
                          <button type="button" onClick={() => void moveObject(item)} className={iconButtonClass()} title="Переместить объект" aria-label={`Переместить ${item.name}`} disabled={!canEdit || actionInProgress}>
                            <MoveRight className="h-4 w-4" />
                            Переместить
                          </button>
                          <button type="button" onClick={() => void deleteObject(item)} className={iconButtonClass('danger')} title="Удалить объект" aria-label={`Удалить ${item.name}`} disabled={!canDelete || actionInProgress}>
                            {actionInProgress ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                            Удалить
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
          <div className="px-4 py-8 text-sm text-slate-500">В этой папке S3 пока нет объектов.</div>
        )}

        {editorPath ? (
          <div className="border-t border-slate-200">
            <div className="flex flex-col gap-3 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="min-w-0">
                <h3 className="text-sm font-semibold text-slate-900">Текстовый редактор S3</h3>
                <p className="mt-1 truncate font-mono text-xs text-slate-500">{editorPath}</p>
              </div>
              <div className="flex gap-2">
                <button type="button" onClick={() => { setEditorPath(null); setEditorContent(''); }} className={iconButtonClass()} title="Закрыть редактор">
                  <X className="h-4 w-4" />
                  Закрыть
                </button>
                <button type="button" onClick={() => void saveTextEditor()} className={iconButtonClass('primary')} disabled={editorSaving || !canEdit} title="Сохранить файл">
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
          </div>
        ) : null}
      </section>
    );
  }

  function renderLogsTab() {
    return (
      <section className="grid w-full gap-5 xl:grid-cols-[minmax(0,1fr)_32rem]">
        <div className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-200 px-4 py-3">
            <h3 className="text-sm font-semibold text-slate-900">Журнал действий</h3>
          </div>
          {logs.length ? (
            <div className="divide-y divide-slate-100">
              {logs.map((log) => (
                <div key={log.id} className="px-4 py-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${logToneClass(log.tone)}`}>{log.title}</span>
                    <span className="text-xs text-slate-500">{formatDate(log.createdAt)}</span>
                  </div>
                  <p className="mt-2 break-words text-sm text-slate-700">{log.description}</p>
                </div>
              ))}
            </div>
          ) : (
            <div className="px-4 py-8 text-sm text-slate-500">Логи появятся после сохранения, проверки или файловых операций.</div>
          )}
        </div>

        <div className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-200 px-4 py-3">
            <h3 className="text-sm font-semibold text-slate-900">Последняя проверка</h3>
          </div>
          {connectionTest ? renderConnectionTest(connectionTest) : (
            <div className="px-4 py-8 text-sm text-slate-500">Проверка подключения еще не запускалась.</div>
          )}
        </div>
      </section>
    );
  }
}

function renderConnectionTest(connectionTest: S3StorageConnectionTestDto) {
  return (
    <>
      <div className="flex flex-col gap-2 border-b border-slate-200 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <p className="text-xs text-slate-500">
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
    </>
  );
}
