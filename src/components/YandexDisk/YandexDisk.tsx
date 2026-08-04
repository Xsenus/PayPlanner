import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import {
  ArrowLeft,
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
  LogIn,
  MoveRight,
  Plus,
  RefreshCw,
  Save,
  SlidersHorizontal,
  TestTube2,
  Trash2,
  UploadCloud,
  X,
} from 'lucide-react';
import { ChangeEvent, ReactNode, useEffect, useMemo, useRef, useState } from 'react';
import {
  apiService,
  type UpsertYandexDiskConnectionRequest,
  type UpdateYandexDiskCrmSyncSettingsRequest,
  type YandexDiskConnectionDto,
  type YandexDiskConnectionTestDto,
  type YandexDiskConnectionsOverviewDto,
  type YandexDiskCrmSyncPreviewDto,
  type YandexDiskCrmSyncStatusReportDto,
  type YandexDiskListResponseDto,
  type YandexDiskResourceDto,
} from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { defaultRestrictedMenuPermissions } from '../../types/permissions';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { TextPromptModal } from '../ui/TextPromptModal';

type YandexDiskProps = {
  embedded?: boolean;
};

type YandexDiskTab = 'connections' | 'files' | 'sync' | 'logs';
type YandexDiskMode = 'overview' | 'form';
type PathDialogMode = 'folder' | 'copy' | 'move';

type PathDialogState = {
  mode: PathDialogMode;
  item?: YandexDiskResourceDto;
  value: string;
  error?: string | null;
};

type ConnectionDraft = UpsertYandexDiskConnectionRequest & {
  id?: string | null;
  name: string;
  rootPath: string;
  oAuthToken: string;
  isEnabled: boolean;
};

const defaultCrmSyncDraft: UpdateYandexDiskCrmSyncSettingsRequest = {
  enabled: false,
  syncInvoices: true,
  syncActs: true,
  syncContracts: true,
  syncCaseDocuments: true,
  syncTaskFiles: false,
  overwriteExisting: false,
  conflictResolutionMode: 'yandexDiskMaster',
  folderTemplate: 'CRM/{clientName}/{caseNumber}/{documentType}',
  fileNameTemplate: '{documentDate}-{documentNumber}-{documentTitle}',
};

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

function normalizeTargetPath(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return '';
  return trimmed.startsWith('/') ? trimmed : `/${trimmed}`;
}

function formatSize(size?: number | null) {
  if (!size) return '-';
  const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let value = size;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }
  return `${value.toLocaleString('ru-RU', { maximumFractionDigits: unitIndex === 0 ? 0 : 1 })} ${units[unitIndex]}`;
}

function formatDate(value?: string | null, timeZoneId?: string | null) {
  if (!value) return '-';
  return formatDateTimeInTimeZone(value, timeZoneId) || value;
}

function appendYandexDiskHint(message: string, hint: string) {
  const normalized = message.toLowerCase();
  const hintStart = hint.slice(0, 32).toLowerCase();
  return normalized.includes(hintStart) ? message : `${message} ${hint}`;
}

function extractYandexDiskErrorMessage(error: unknown, fallback: string) {
  const message = extractDisplayErrorMessage(error, fallback);
  const raw = error instanceof Error ? error.message : String(error ?? '');
  const normalized = `${message} ${raw}`.toLowerCase();

  if (normalized.includes('oauth') || normalized.includes('unauthorized') || normalized.includes('401')) {
    return appendYandexDiskHint(message, 'Проверьте OAuth-токен Я.Диска и сохраните подключение заново.');
  }

  if (normalized.includes('forbidden') || normalized.includes('403') || normalized.includes('доступ')) {
    return appendYandexDiskHint(message, 'Проверьте права токена на операции с файлами Яндекс.Диска.');
  }

  if (normalized.includes('quota') || normalized.includes('insufficient') || normalized.includes('мест')) {
    return appendYandexDiskHint(message, 'Освободите место на Яндекс.Диске или подключите другой аккаунт.');
  }

  return message;
}

function emptyDraft(): ConnectionDraft {
  return {
    id: null,
    name: 'Яндекс.Диск',
    rootPath: 'disk:/PayPlanner',
    oAuthToken: '',
    isEnabled: true,
  };
}

function draftFromConnection(connection: YandexDiskConnectionDto): ConnectionDraft {
  return {
    id: connection.id,
    name: connection.name,
    rootPath: connection.rootPath || 'disk:/PayPlanner',
    oAuthToken: '',
    isEnabled: connection.isEnabled,
  };
}

function statusBadge(connection: YandexDiskConnectionDto) {
  if (connection.requiresReconnect) {
    return <span className="rounded-full bg-rose-50 px-2 py-0.5 text-xs font-medium text-rose-700">Нужно переподключить</span>;
  }

  if (!connection.isEnabled) {
    return <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">Выключено</span>;
  }

  if (!connection.isConfigured) {
    return <span className="rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700">Нужен токен</span>;
  }

  return <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">Включено</span>;
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

function cardActionButton({
  label,
  icon,
  onClick,
  tone = 'default',
  disabled = false,
}: {
  label: string;
  icon: ReactNode;
  onClick: () => void;
  tone?: 'default' | 'success' | 'danger';
  disabled?: boolean;
}) {
  return (
    <button type="button" onClick={onClick} disabled={disabled} title={label} aria-label={label} className={cardIconButtonClass(tone)}>
      {icon}
    </button>
  );
}

export function YandexDisk({ embedded = false }: YandexDiskProps = {}) {
  const toast = useToast();
  const { confirm } = useDialog();
  const { user } = useAuth();
  const timeZoneId = useUserTimeZone();
  const rolePermissions = useRolePermissions(user?.role?.id);
  const isAdminRole = (user?.role?.name ?? '').toLowerCase() === 'admin';
  const permissions = rolePermissions.yandexDisk ?? defaultRestrictedMenuPermissions;
  const canView = isAdminRole || permissions.canView;
  const canCreate = isAdminRole || permissions.canCreate;
  const canEdit = isAdminRole || permissions.canEdit;
  const canDelete = isAdminRole || permissions.canDelete;
  const canExport = isAdminRole || permissions.canExport;
  const canTestConnection = canView && canEdit && canCreate && canDelete && canExport;

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const oauthPopupRef = useRef<Window | null>(null);
  const oauthPollRef = useRef<number | null>(null);
  const [overview, setOverview] = useState<YandexDiskConnectionsOverviewDto | null>(null);
  const [activeTab, setActiveTab] = useState<YandexDiskTab>('connections');
  const [mode, setMode] = useState<YandexDiskMode>('overview');
  const [draft, setDraft] = useState<ConnectionDraft>(() => emptyDraft());
  const [selectedConnectionId, setSelectedConnectionId] = useState<string | null>(null);
  const [currentPath, setCurrentPath] = useState('/');
  const [listing, setListing] = useState<YandexDiskListResponseDto | null>(null);
  const [connectionTest, setConnectionTest] = useState<YandexDiskConnectionTestDto | null>(null);
  const [crmSyncPreview, setCrmSyncPreview] = useState<YandexDiskCrmSyncPreviewDto | null>(null);
  const [crmSyncStatus, setCrmSyncStatus] = useState<YandexDiskCrmSyncStatusReportDto | null>(null);
  const [crmSyncDraft, setCrmSyncDraft] = useState<UpdateYandexDiskCrmSyncSettingsRequest>(defaultCrmSyncDraft);
  const [editorPath, setEditorPath] = useState<string | null>(null);
  const [editorContent, setEditorContent] = useState('');
  const [pathDialog, setPathDialog] = useState<PathDialogState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadingAction, setLoadingAction] = useState(false);
  const [savingConnection, setSavingConnection] = useState(false);
  const [connectingOAuth, setConnectingOAuth] = useState(false);
  const [savingSync, setSavingSync] = useState(false);

  const connections = useMemo(() => overview?.connections ?? [], [overview]);
  const oauthCallbackUrl = overview?.oAuthCallbackUrl;
  const selectedConnection = useMemo(
    () => connections.find((connection) => connection.id === selectedConnectionId) ?? connections[0] ?? null,
    [connections, selectedConnectionId],
  );
  const shellClassName = embedded ? 'w-full' : 'min-h-screen w-full bg-slate-50 px-4 py-5 sm:px-6 lg:px-8';

  const showError = (message: string) => {
    setError(message);
    toast.error(message, 'Я.Диск');
  };

  const loadOverview = async () => {
    setLoading(true);
    setError(null);
    try {
      const loaded = await apiService.getYandexDiskConnections();
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
      showError(extractYandexDiskErrorMessage(loadError, 'Не удалось загрузить подключения Я.Диска.'));
    } finally {
      setLoading(false);
    }
  };

  const loadSync = async () => {
    try {
      const [settings, preview, status] = await Promise.all([
        apiService.getYandexDiskCrmSyncSettings(),
        apiService.getYandexDiskCrmSyncPreview(),
        apiService.getYandexDiskCrmSyncStatus(),
      ]);
      setCrmSyncPreview(preview);
      setCrmSyncStatus(status);
      setCrmSyncDraft({
        enabled: settings.enabled,
        syncInvoices: settings.syncInvoices,
        syncActs: settings.syncActs,
        syncContracts: settings.syncContracts,
        syncCaseDocuments: settings.syncCaseDocuments,
        syncTaskFiles: settings.syncTaskFiles,
        overwriteExisting: settings.overwriteExisting,
        conflictResolutionMode: settings.conflictResolutionMode ?? 'yandexDiskMaster',
        folderTemplate: settings.folderTemplate,
        fileNameTemplate: settings.fileNameTemplate,
      });
    } catch (syncError) {
      showError(extractYandexDiskErrorMessage(syncError, 'Не удалось загрузить настройки синхронизации Я.Диска.'));
    }
  };

  const loadObjects = async (path = currentPath, silent = false) => {
    if (!selectedConnection?.id) return;
    if (!silent) setLoading(true);
    setError(null);
    try {
      const data = await apiService.listYandexDiskResources(path, selectedConnection.id);
      setListing(data);
      setCurrentPath(data.current.path || path || '/');
    } catch (loadError) {
      showError(extractYandexDiskErrorMessage(loadError, 'Не удалось загрузить файлы Я.Диска.'));
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => {
    if (canView) {
      void loadOverview();
      void loadSync();
    }
    // Initial loading is tied to permission state; loaders are intentionally not dependencies.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [canView]);

  useEffect(() => () => {
    if (oauthPollRef.current !== null) {
      window.clearInterval(oauthPollRef.current);
    }
    if (oauthPopupRef.current && !oauthPopupRef.current.closed) {
      oauthPopupRef.current.close();
    }
  }, []);

  useEffect(() => {
    if (activeTab === 'files' && selectedConnection?.id) {
      void loadObjects('/');
    }
    // Opening the files tab or changing connection resets the browser to the root folder.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, selectedConnection?.id]);

  const openCreateForm = () => {
    setDraft(emptyDraft());
    setMode('form');
  };

  const openEditForm = (connection: YandexDiskConnectionDto) => {
    setDraft(draftFromConnection(connection));
    setMode('form');
  };

  const saveConnection = async () => {
    if (!canEdit) return;
    if (!draft.name.trim()) {
      showError('Укажите название подключения Я.Диска.');
      return;
    }
    if (!draft.id && !draft.oAuthToken.trim()) {
      showError('Для нового подключения нужен OAuth-токен Я.Диска.');
      return;
    }

    setSavingConnection(true);
    setError(null);
    try {
      const saved = await apiService.upsertYandexDiskConnection({
        id: draft.id,
        name: draft.name.trim(),
        rootPath: draft.rootPath.trim() || 'disk:/PayPlanner',
        oAuthToken: draft.oAuthToken.trim() || undefined,
        isEnabled: draft.isEnabled,
      });
      toast.success(draft.id ? 'Подключение Я.Диска обновлено.' : 'Подключение Я.Диска создано.', 'Я.Диск');
      await loadOverview();
      setSelectedConnectionId(saved.id);
      setMode('overview');
      setActiveTab('connections');
    } catch (saveError) {
      showError(extractYandexDiskErrorMessage(saveError, 'Не удалось сохранить подключение Я.Диска.'));
    } finally {
      setSavingConnection(false);
    }
  };

  const connectWithYandex = async () => {
    if (!canEdit || (!draft.id && !canCreate)) return;
    if (!draft.name.trim()) {
      showError('Укажите название подключения Я.Диска.');
      return;
    }

    setConnectingOAuth(true);
    setError(null);
    try {
      const started = await apiService.startYandexDiskAuth({
        connectionId: draft.id,
        name: draft.name.trim(),
        rootPath: draft.rootPath.trim() || 'disk:/PayPlanner',
        isEnabled: draft.isEnabled,
        returnUrl: '/?ppTab=settings&settingsSection=yandex-disk',
      });
      oauthPopupRef.current = window.open(started.authUrl, 'pp-yandex-disk-link', 'popup=yes,width=560,height=720');
      if (!oauthPopupRef.current) {
        window.location.assign(started.authUrl);
        return;
      }

      oauthPollRef.current = window.setInterval(async () => {
        try {
          const authStatus = await apiService.getYandexDiskAuthStatus(started.state);
          if (!authStatus.completed) return;

          if (oauthPollRef.current !== null) {
            window.clearInterval(oauthPollRef.current);
            oauthPollRef.current = null;
          }
          if (oauthPopupRef.current && !oauthPopupRef.current.closed) {
            oauthPopupRef.current.close();
          }
          oauthPopupRef.current = null;
          setConnectingOAuth(false);

          if (authStatus.status === 'completed' && authStatus.connectionId) {
            await loadOverview();
            setSelectedConnectionId(authStatus.connectionId);
            setMode('overview');
            setActiveTab('connections');
            toast.success('Яндекс.Диск подключен через Яндекс ID.', 'Я.Диск');
            return;
          }

          showError(authStatus.detail || 'Не удалось завершить подключение Яндекс.Диска.');
        } catch (statusError) {
          if (oauthPollRef.current !== null) {
            window.clearInterval(oauthPollRef.current);
            oauthPollRef.current = null;
          }
          setConnectingOAuth(false);
          showError(extractYandexDiskErrorMessage(statusError, 'Не удалось проверить подключение Яндекс.Диска.'));
        }
      }, started.pollIntervalMs || 2000);
    } catch (connectError) {
      setConnectingOAuth(false);
      showError(extractYandexDiskErrorMessage(connectError, 'Не удалось начать подключение Яндекс.Диска.'));
    }
  };

  const clearToken = async () => {
    if (!draft.id || !canEdit) return;
    const approved = await confirm({
      title: 'Очистить OAuth-токен?',
      description: `Сохраненный токен подключения "${draft.name}" будет удален. Само подключение останется в списке.`,
      confirmLabel: 'Очистить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setSavingConnection(true);
    try {
      await apiService.upsertYandexDiskConnection({
        id: draft.id,
        name: draft.name,
        rootPath: draft.rootPath,
        clearToken: true,
        isEnabled: draft.isEnabled,
      });
      setDraft((current) => ({ ...current, oAuthToken: '' }));
      await loadOverview();
      toast.success('OAuth-токен очищен.', 'Я.Диск');
    } catch (clearError) {
      showError(extractYandexDiskErrorMessage(clearError, 'Не удалось очистить OAuth-токен.'));
    } finally {
      setSavingConnection(false);
    }
  };

  const deleteConnection = async (connection: YandexDiskConnectionDto) => {
    if (!canDelete) return;
    const approved = await confirm({
      title: 'Удалить подключение Я.Диска?',
      description: `Подключение "${connection.name}" будет удалено из настроек. Файлы в аккаунте Яндекс.Диска не удаляются.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setLoadingAction(true);
    try {
      await apiService.deleteYandexDiskConnection(connection.id);
      toast.success('Подключение Я.Диска удалено.', 'Я.Диск');
      await loadOverview();
      if (selectedConnectionId === connection.id) {
        setSelectedConnectionId(null);
        setListing(null);
      }
    } catch (deleteError) {
      showError(extractYandexDiskErrorMessage(deleteError, 'Не удалось удалить подключение Я.Диска.'));
    } finally {
      setLoadingAction(false);
    }
  };

  const testConnection = async (connection = selectedConnection) => {
    if (!connection || !canTestConnection) return;
    setLoadingAction(true);
    setError(null);
    try {
      const result = await apiService.testYandexDiskConnectionById(connection.id, '/_payplanner-test');
      setConnectionTest(result);
      setActiveTab('logs');
      toast.success('Подключение Я.Диска проверено.', 'Я.Диск');
    } catch (testError) {
      showError(extractYandexDiskErrorMessage(testError, 'Не удалось проверить подключение Я.Диска.'));
    } finally {
      setLoadingAction(false);
    }
  };

  const submitPathDialog = async () => {
    if (!pathDialog || !selectedConnection?.id) return;
    const value = normalizeTargetPath(pathDialog.value);
    if (!value) {
      setPathDialog((dialog) => (dialog ? { ...dialog, error: 'Укажите путь.' } : dialog));
      return;
    }

    setLoadingAction(true);
    try {
      if (pathDialog.mode === 'folder') {
        await apiService.createYandexDiskFolder(joinPath(currentPath, value), selectedConnection.id);
        toast.success('Папка создана.', 'Я.Диск');
      } else if (pathDialog.mode === 'copy' && pathDialog.item) {
        await apiService.copyYandexDiskResource(pathDialog.item.path, value, false, selectedConnection.id);
        toast.success('Объект скопирован.', 'Я.Диск');
      } else if (pathDialog.mode === 'move' && pathDialog.item) {
        await apiService.moveYandexDiskResource(pathDialog.item.path, value, false, selectedConnection.id);
        toast.success('Объект перемещен.', 'Я.Диск');
      }
      setPathDialog(null);
      await loadObjects(currentPath, true);
    } catch (dialogError) {
      setPathDialog((dialog) => (dialog ? { ...dialog, error: extractYandexDiskErrorMessage(dialogError, 'Операция Я.Диска не выполнена.') } : dialog));
    } finally {
      setLoadingAction(false);
    }
  };

  const uploadFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file || !selectedConnection?.id || !canCreate) return;

    setLoadingAction(true);
    try {
      await apiService.uploadYandexDiskFile(joinPath(currentPath, file.name), file, true, selectedConnection.id);
      toast.success('Файл загружен.', 'Я.Диск');
      await loadObjects(currentPath, true);
    } catch (uploadError) {
      showError(extractYandexDiskErrorMessage(uploadError, 'Не удалось загрузить файл.'));
    } finally {
      setLoadingAction(false);
    }
  };

  const downloadFile = async (item: YandexDiskResourceDto) => {
    if (!selectedConnection?.id || !canExport) return;
    try {
      const result = await apiService.downloadYandexDiskFile(item.path, selectedConnection.id);
      const url = URL.createObjectURL(result.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = result.fileName;
      link.click();
      URL.revokeObjectURL(url);
    } catch (downloadError) {
      showError(extractYandexDiskErrorMessage(downloadError, 'Не удалось скачать файл.'));
    }
  };

  const deleteResource = async (item: YandexDiskResourceDto) => {
    if (!selectedConnection?.id || !canDelete) return;
    const approved = await confirm({
      title: 'Удалить объект Я.Диска?',
      description: `"${item.name}" будет удален из выбранного подключения.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setLoadingAction(true);
    try {
      await apiService.deleteYandexDiskResource(item.path, false, selectedConnection.id);
      toast.success('Объект удален.', 'Я.Диск');
      await loadObjects(currentPath, true);
    } catch (deleteError) {
      showError(extractYandexDiskErrorMessage(deleteError, 'Не удалось удалить объект.'));
    } finally {
      setLoadingAction(false);
    }
  };

  const openTextEditor = async (item: YandexDiskResourceDto) => {
    if (!selectedConnection?.id || !canEdit || item.type !== 'file') return;
    setLoadingAction(true);
    try {
      const file = await apiService.getYandexDiskTextFile(item.path, selectedConnection.id);
      setEditorPath(file.path);
      setEditorContent(file.content);
    } catch (editorError) {
      showError(extractYandexDiskErrorMessage(editorError, 'Не удалось открыть файл для редактирования.'));
    } finally {
      setLoadingAction(false);
    }
  };

  const saveTextEditor = async () => {
    if (!selectedConnection?.id || !editorPath || !canEdit) return;
    setLoadingAction(true);
    try {
      await apiService.saveYandexDiskTextFile(editorPath, editorContent, true, selectedConnection.id);
      toast.success('Файл сохранен.', 'Я.Диск');
      setEditorPath(null);
      await loadObjects(currentPath, true);
    } catch (editorError) {
      showError(extractYandexDiskErrorMessage(editorError, 'Не удалось сохранить файл.'));
    } finally {
      setLoadingAction(false);
    }
  };

  const saveSyncSettings = async () => {
    if (!canEdit) return;
    setSavingSync(true);
    try {
      const updated = await apiService.updateYandexDiskCrmSyncSettings(crmSyncDraft);
      setCrmSyncDraft({
        enabled: updated.enabled,
        syncInvoices: updated.syncInvoices,
        syncActs: updated.syncActs,
        syncContracts: updated.syncContracts,
        syncCaseDocuments: updated.syncCaseDocuments,
        syncTaskFiles: updated.syncTaskFiles,
        overwriteExisting: updated.overwriteExisting,
        conflictResolutionMode: updated.conflictResolutionMode ?? 'yandexDiskMaster',
        folderTemplate: updated.folderTemplate,
        fileNameTemplate: updated.fileNameTemplate,
      });
      setCrmSyncPreview(await apiService.getYandexDiskCrmSyncPreview());
      setCrmSyncStatus(await apiService.getYandexDiskCrmSyncStatus());
      toast.success('Правила синхронизации сохранены.', 'Я.Диск');
    } catch (syncError) {
      showError(extractYandexDiskErrorMessage(syncError, 'Не удалось сохранить правила синхронизации.'));
    } finally {
      setSavingSync(false);
    }
  };

  if (!canView) {
    return (
      <div className={embedded ? 'w-full' : 'p-6'}>
        <div className="rounded-lg border border-slate-200 bg-white p-5 text-sm text-slate-700">
          Недостаточно прав для просмотра Я.Диска.
        </div>
      </div>
    );
  }

  if (mode === 'form') {
    return (
      <div className={shellClassName}>
        <div className="flex w-full flex-col gap-5">
          <header className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div>
              <button type="button" onClick={() => setMode('overview')} className={iconButtonClass()} title="Вернуться к подключениям">
                <ArrowLeft className="h-4 w-4" />
                К подключениям
              </button>
              <div className="mt-4 flex items-center gap-2 text-sm font-semibold text-sky-700">
                <Cloud className="h-4 w-4" />
                <span>{draft.id ? 'Редактирование подключения' : 'Новое подключение'}</span>
              </div>
              <h2 className="mt-1 text-2xl font-semibold text-slate-900">{draft.id ? draft.name : 'Создание подключения Я.Диска'}</h2>
            </div>
            <button type="button" onClick={saveConnection} disabled={savingConnection || connectingOAuth || !canEdit} className={iconButtonClass()} title="Сохранить">
              {savingConnection ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Сохранить
            </button>
          </header>

          {error ? <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}

          <form
            className="rounded-lg border border-slate-200 bg-white p-5 shadow-sm"
            autoComplete="off"
            onSubmit={(event) => {
              event.preventDefault();
              void saveConnection();
            }}
          >
            <div className="grid gap-4 lg:grid-cols-2">
              <label className="space-y-1">
                <span className="text-sm font-semibold text-slate-700">Название</span>
                <input value={draft.name} onChange={(event) => setDraft((current) => ({ ...current, name: event.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="space-y-1">
                <span className="text-sm font-semibold text-slate-700">Рабочая папка</span>
                <input value={draft.rootPath} onChange={(event) => setDraft((current) => ({ ...current, rootPath: event.target.value }))} placeholder="disk:/PayPlanner" autoComplete="off" className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
                <SystemCheckbox type="checkbox" checked={draft.isEnabled} onChange={(event) => setDraft((current) => ({ ...current, isEnabled: event.target.checked }))} className="h-4 w-4 rounded border-slate-300 text-sky-600" />
                Включить подключение
              </label>
              <div className="rounded-lg border border-sky-200 bg-sky-50 p-3 lg:col-span-2">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                  <div>
                    <div className="text-sm font-semibold text-sky-900">Подключение через Яндекс ID</div>
                    <div className="mt-1 text-xs leading-5 text-sky-700">Войдите в нужный аккаунт Яндекса и разрешите CRM работу с общим Диском. Токен сохранится автоматически.</div>
                    {oauthCallbackUrl ? <div className="mt-1 break-all text-[11px] text-sky-600">Redirect URI для приложения Яндекса: {oauthCallbackUrl}</div> : null}
                  </div>
                  <button
                    type="button"
                    onClick={connectWithYandex}
                    disabled={connectingOAuth || savingConnection || !canEdit || (!draft.id && !canCreate)}
                    className={iconButtonClass('primary')}
                    title="Подключить через Яндекс ID"
                  >
                    {connectingOAuth ? <Loader2 className="h-4 w-4 animate-spin" /> : <LogIn className="h-4 w-4" />}
                    {connectingOAuth ? 'Ожидаем Яндекс...' : draft.id ? 'Переподключить через Яндекс' : 'Подключить Яндекс.Диск'}
                  </button>
                </div>
              </div>
              <details className="rounded-lg border border-slate-200 bg-slate-50 p-3 lg:col-span-2">
                <summary className="cursor-pointer text-sm font-semibold text-slate-700">Ручной OAuth-токен — резервный способ</summary>
                <div className="mt-3 space-y-3">
                  <label className="space-y-1">
                    <span className="text-sm font-semibold text-slate-700">OAuth-токен</span>
                    <span className="block text-xs text-slate-600">Используйте это поле только если вход через Яндекс временно недоступен.</span>
                    <div className="relative">
                      <KeyRound className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                      <input
                        type="password"
                        aria-label="OAuth-токен"
                        value={draft.oAuthToken}
                        onChange={(event) => setDraft((current) => ({ ...current, oAuthToken: event.target.value }))}
                        placeholder={draft.id ? 'Оставьте пустым, чтобы не менять сохраненный токен' : 'Вставьте OAuth-токен Я.Диска'}
                        autoComplete="new-password"
                        data-lpignore="true"
                        data-1p-ignore="true"
                        data-form-type="other"
                        className="w-full rounded-lg border border-slate-300 py-2 pl-9 pr-3 text-sm"
                      />
                    </div>
                  </label>
                  {draft.id ? (
                    <button type="button" onClick={clearToken} disabled={savingConnection || connectingOAuth || !canEdit} className={iconButtonClass('danger')} title="Очистить сохраненный токен">
                      <X className="h-4 w-4" />
                      Очистить токен
                    </button>
                  ) : null}
                </div>
              </details>
            </div>
          </form>
        </div>
      </div>
    );
  }

  function renderTabs() {
    const tabs = [
      { id: 'connections' as const, label: 'Подключения', icon: Cloud },
      { id: 'files' as const, label: 'Файлы', icon: Folder },
      { id: 'sync' as const, label: 'Синхронизация', icon: SlidersHorizontal },
      { id: 'logs' as const, label: 'Логи', icon: CheckCircle2 },
    ];

    return (
      <div className="flex flex-wrap gap-2 rounded-lg border border-slate-200 bg-white p-2 shadow-sm">
        {tabs.map((tab) => {
          const Icon = tab.icon;
          const active = activeTab === tab.id;
          return (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={`inline-flex items-center gap-2 rounded-md px-3 py-2 text-sm font-semibold transition ${
                active ? 'bg-sky-100 text-sky-800' : 'text-slate-600 hover:bg-slate-100'
              }`}
            >
              <Icon className="h-4 w-4" />
              {tab.label}
            </button>
          );
        })}
      </div>
    );
  }

  function renderConnectionsTab() {
    if (loading) {
      return <div className="rounded-lg border border-slate-200 bg-white p-6 text-sm text-slate-500">Загружаем подключения...</div>;
    }

    if (!connections.length) {
      return (
        <div className="rounded-lg border border-slate-200 bg-white p-6 text-sm text-slate-500">
          Подключений пока нет. Создайте первое подключение Я.Диска.
        </div>
      );
    }

    return (
      <section className="grid w-full gap-4 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
        {connections.map((connection) => (
          <article key={connection.id} className="flex min-h-[17rem] flex-col rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <h3 className="truncate text-base font-semibold text-slate-900">{connection.name}</h3>
                <div className="mt-2 flex flex-wrap gap-2">{statusBadge(connection)}</div>
              </div>
              <Cloud className="h-5 w-5 text-sky-600" />
            </div>

            <div className="mt-4 grid gap-3 text-xs text-slate-600">
              <div>
                <div className="font-semibold uppercase text-slate-400">Рабочая папка</div>
                <div className="mt-1 break-all font-mono text-slate-800">{connection.rootPath || 'disk:/'}</div>
              </div>
              <div>
                <div className="font-semibold uppercase text-slate-400">OAuth-токен</div>
                <div className={`mt-1 ${connection.requiresReconnect ? 'text-rose-700' : 'font-mono text-slate-800'}`}>
                  {connection.requiresReconnect ? 'Токен недоступен — подключите Я.Диск заново' : (connection.tokenPreview ?? 'Не сохранен')}
                </div>
              </div>
              <div>
                <div className="font-semibold uppercase text-slate-400">Обновлено</div>
                <div className="mt-1 text-slate-800">{formatDate(connection.updatedAt, timeZoneId)}</div>
              </div>
            </div>

            <div className="mt-auto flex justify-end gap-2 pt-4">
              {cardActionButton({ label: 'Открыть файлы', icon: <Folder className="h-4 w-4" />, tone: 'success', onClick: () => { setSelectedConnectionId(connection.id); setActiveTab('files'); }, disabled: !connection.isConfigured })}
              {cardActionButton({ label: 'Изменить', icon: <Edit3 className="h-4 w-4" />, onClick: () => openEditForm(connection), disabled: !canEdit })}
              {cardActionButton({ label: 'Проверить', icon: <TestTube2 className="h-4 w-4" />, onClick: () => void testConnection(connection), disabled: !canTestConnection || !connection.isConfigured })}
              {cardActionButton({ label: 'Удалить', icon: <Trash2 className="h-4 w-4" />, tone: 'danger', onClick: () => void deleteConnection(connection), disabled: !canDelete || loadingAction })}
            </div>
          </article>
        ))}
      </section>
    );
  }

  function renderFilesTab() {
    return (
      <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-col gap-3 border-b border-slate-200 p-4 lg:flex-row lg:items-center lg:justify-between">
          <div>
            <h3 className="text-base font-semibold text-slate-900">Файлы Я.Диска</h3>
            <p className="mt-1 text-sm text-slate-500">{selectedConnection ? `${selectedConnection.name}: ${currentPath}` : 'Выберите подключение'}</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <SystemSelect value={selectedConnection?.id ?? ''} onChange={(event) => { setSelectedConnectionId(event.target.value || null); setCurrentPath('/'); }} className="rounded-lg border border-slate-300 px-3 py-2 text-sm">
              {connections.map((connection) => <option key={connection.id} value={connection.id}>{connection.name}</option>)}
            </SystemSelect>
            <button type="button" onClick={() => void loadObjects(parentPath(currentPath))} disabled={!selectedConnection || currentPath === '/'} className={iconButtonClass()} title="Вверх">
              <ArrowUp className="h-4 w-4" />
              Вверх
            </button>
            <button type="button" onClick={() => setPathDialog({ mode: 'folder', value: '' })} disabled={!selectedConnection || !canCreate} className={iconButtonClass()} title="Создать папку">
              <Folder className="h-4 w-4" />
              Папка
            </button>
            <button type="button" onClick={() => fileInputRef.current?.click()} disabled={!selectedConnection || !canCreate} className={iconButtonClass('primary')} title="Загрузить файл">
              <UploadCloud className="h-4 w-4" />
              Загрузить
            </button>
            <button type="button" onClick={() => void loadObjects(currentPath)} disabled={!selectedConnection} className={iconButtonClass()} title="Обновить">
              <RefreshCw className="h-4 w-4" />
              Обновить
            </button>
            <input ref={fileInputRef} type="file" className="hidden" onChange={uploadFile} />
          </div>
        </div>

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
            <tbody className="divide-y divide-slate-100">
              {listing?.items.map((item) => (
                <tr key={item.fullPath || item.path} className="hover:bg-slate-50">
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => item.type === 'dir' ? void loadObjects(item.path) : undefined} className="inline-flex items-center gap-2 text-left font-semibold text-sky-700">
                      {item.type === 'dir' ? <Folder className="h-4 w-4" /> : <File className="h-4 w-4" />}
                      <span>{item.name}</span>
                    </button>
                    <div className="mt-1 font-mono text-xs text-slate-400">{item.path}</div>
                  </td>
                  <td className="px-4 py-3 text-slate-600">{item.type === 'dir' ? '-' : formatSize(item.size)}</td>
                  <td className="px-4 py-3 text-slate-600">{formatDate(item.modified, timeZoneId)}</td>
                  <td className="px-4 py-3">
                    <div className="flex justify-end gap-2">
                      {item.type === 'file' && cardActionButton({ label: 'Скачать', icon: <Download className="h-4 w-4" />, onClick: () => void downloadFile(item), disabled: !canExport })}
                      {item.type === 'file' && cardActionButton({ label: 'Редактировать', icon: <Edit3 className="h-4 w-4" />, onClick: () => void openTextEditor(item), disabled: !canEdit })}
                      {cardActionButton({ label: 'Копировать', icon: <Copy className="h-4 w-4" />, onClick: () => setPathDialog({ mode: 'copy', item, value: joinPath(parentPath(item.path), `copy-${item.name}`) }), disabled: !canCreate })}
                      {cardActionButton({ label: 'Переместить', icon: <MoveRight className="h-4 w-4" />, onClick: () => setPathDialog({ mode: 'move', item, value: item.path }), disabled: !canEdit })}
                      {cardActionButton({ label: 'Удалить', icon: <Trash2 className="h-4 w-4" />, tone: 'danger', onClick: () => void deleteResource(item), disabled: !canDelete })}
                    </div>
                  </td>
                </tr>
              ))}
              {!listing?.items.length ? (
                <tr>
                  <td colSpan={4} className="px-4 py-8 text-center text-sm text-slate-500">
                    {selectedConnection ? 'В папке пока нет файлов.' : 'Выберите подключение Я.Диска.'}
                  </td>
                </tr>
              ) : null}
            </tbody>
          </table>
        </div>
      </section>
    );
  }

  function renderSyncTab() {
    return (
      <section className="rounded-lg border border-slate-200 bg-white p-5 shadow-sm">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
          <div>
            <h3 className="text-base font-semibold text-slate-900">Синхронизация CRM-документов</h3>
            <p className="mt-1 text-sm text-slate-500">Автоматическая отправка документов использует первое включенное и настроенное подключение Я.Диска.</p>
          </div>
          <button type="button" onClick={saveSyncSettings} disabled={!canEdit || savingSync} className={iconButtonClass('primary')} title="Сохранить правила синхронизации">
            {savingSync ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Сохранить
          </button>
        </div>

        <div className="mt-5 grid gap-4 lg:grid-cols-2">
          <label className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
            <SystemCheckbox type="checkbox" checked={crmSyncDraft.enabled} onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, enabled: event.target.checked }))} className="h-4 w-4 rounded border-slate-300 text-sky-600" />
            Включить синхронизацию CRM-документов
          </label>
          {[
            ['syncInvoices', 'Счета'],
            ['syncActs', 'Акты'],
            ['syncContracts', 'Договоры'],
            ['syncCaseDocuments', 'Документы дел'],
            ['syncTaskFiles', 'Файлы задач'],
          ].map(([field, label]) => (
            <label key={field} className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
              <SystemCheckbox
                type="checkbox"
                checked={Boolean(crmSyncDraft[field as keyof UpdateYandexDiskCrmSyncSettingsRequest])}
                onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, [field]: event.target.checked }))}
                className="h-4 w-4 rounded border-slate-300 text-sky-600"
              />
              {label}
            </label>
          ))}
          <label className="space-y-1 lg:col-span-2">
            <span className="text-sm font-semibold text-slate-700">Шаблон папки</span>
            <input value={crmSyncDraft.folderTemplate ?? ''} onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, folderTemplate: event.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
          </label>
          <label className="space-y-1 lg:col-span-2">
            <span className="text-sm font-semibold text-slate-700">Шаблон имени файла</span>
            <input value={crmSyncDraft.fileNameTemplate ?? ''} onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, fileNameTemplate: event.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
          </label>
        </div>

        {crmSyncPreview ? (
          <div className="mt-5 rounded-lg border border-slate-200">
            <div className="border-b border-slate-200 px-4 py-3 text-sm font-semibold text-slate-900">Предпросмотр путей</div>
            <div className="divide-y divide-slate-100">
              {crmSyncPreview.items.slice(0, 5).map((item) => (
                <div key={item.documentKind} className="px-4 py-3 text-sm">
                  <div className="font-semibold text-slate-900">{item.documentType}</div>
                  <div className="mt-1 break-all font-mono text-xs text-slate-500">{item.fullPath}</div>
                </div>
              ))}
            </div>
          </div>
        ) : null}
      </section>
    );
  }

  function renderLogsTab() {
    return (
      <div className="space-y-4">
        {connectionTest ? (
          <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
            <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
              <div>
                <h3 className="text-base font-semibold text-slate-900">Результат проверки</h3>
                <p className="text-sm text-slate-500">{formatDate(connectionTest.checkedAt, timeZoneId)}</p>
              </div>
              <span className="rounded-full bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-700">Успешно</span>
            </div>
            <div className="divide-y divide-slate-100">
              {connectionTest.steps.map((step) => (
                <div key={step.key} className="flex items-start justify-between gap-3 px-4 py-3 text-sm">
                  <div>
                    <div className="font-semibold text-slate-900">{step.name}</div>
                    <div className="mt-1 text-slate-500">{step.message}</div>
                  </div>
                  <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-semibold text-emerald-700">OK</span>
                </div>
              ))}
            </div>
          </section>
        ) : null}

        <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-200 px-4 py-3">
            <h3 className="text-base font-semibold text-slate-900">Журнал синхронизации CRM</h3>
            <p className="text-sm text-slate-500">Последние операции отправки документов на Я.Диск.</p>
          </div>
          <div className="divide-y divide-slate-100">
            {crmSyncStatus?.recentItems.length ? crmSyncStatus.recentItems.map((item) => (
              <div key={`${item.documentKind}-${item.objectType}-${item.objectId}`} className="px-4 py-3 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="font-semibold text-slate-900">{item.fileName ?? item.documentKind}</div>
                  <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${item.status === 'success' ? 'bg-emerald-50 text-emerald-700' : item.status === 'failed' ? 'bg-rose-50 text-rose-700' : 'bg-amber-50 text-amber-700'}`}>
                    {item.statusText}
                  </span>
                </div>
                <div className="mt-1 break-all font-mono text-xs text-slate-500">{item.yandexDiskPath ?? '-'}</div>
                {item.lastError ? <div className="mt-1 text-xs text-rose-600">{item.lastError}</div> : null}
              </div>
            )) : (
              <div className="px-4 py-8 text-center text-sm text-slate-500">Событий пока нет.</div>
            )}
          </div>
        </section>
      </div>
    );
  }

  function renderPathDialog() {
    if (!pathDialog) return null;
    const options = {
      folder: {
        title: 'Создать папку на Я.Диске',
        description: `Папка будет создана внутри ${currentPath}.`,
        label: 'Название или путь папки',
        confirmLabel: 'Создать',
        placeholder: 'Новая папка',
      },
      copy: {
        title: 'Копировать объект Я.Диска',
        description: pathDialog.item ? `Исходный объект "${pathDialog.item.name}" останется на месте.` : undefined,
        label: 'Новый путь копии',
        confirmLabel: 'Копировать',
        placeholder: '/copy-file.txt',
      },
      move: {
        title: 'Переместить объект Я.Диска',
        description: pathDialog.item ? `Объект "${pathDialog.item.name}" будет перенесен на новый путь.` : undefined,
        label: 'Новый путь объекта',
        confirmLabel: 'Переместить',
        placeholder: '/archive/file.txt',
      },
    }[pathDialog.mode];

    return (
      <TextPromptModal
        open
        title={options.title}
        description={options.description}
        label={options.label}
        value={pathDialog.value}
        onValueChange={(value) => setPathDialog((dialog) => (dialog ? { ...dialog, value, error: null } : dialog))}
        onCancel={() => {
          if (!loadingAction) setPathDialog(null);
        }}
        onConfirm={submitPathDialog}
        confirmLabel={options.confirmLabel}
        cancelLabel="Отмена"
        placeholder={options.placeholder}
        error={pathDialog.error ?? undefined}
        loading={loadingAction}
      />
    );
  }

  return (
    <div className={shellClassName}>
      <div className="flex w-full flex-col gap-5">
        <header className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div>
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-700">
              <Cloud className="h-4 w-4" />
              <span>Я.Диск</span>
            </div>
            <h2 className="mt-1 text-2xl font-semibold text-slate-900">Управление Я.Диском</h2>
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="button" onClick={() => void loadOverview()} className={iconButtonClass()} title="Обновить">
              <RefreshCw className="h-4 w-4" />
              Обновить
            </button>
            <button type="button" onClick={openCreateForm} disabled={!canEdit} className={iconButtonClass('primary')} title="Новое подключение">
              <Plus className="h-4 w-4" />
              Новое
            </button>
          </div>
        </header>

        {error ? <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}

        {renderTabs()}
        {activeTab === 'connections' ? renderConnectionsTab() : null}
        {activeTab === 'files' ? renderFilesTab() : null}
        {activeTab === 'sync' ? renderSyncTab() : null}
        {activeTab === 'logs' ? renderLogsTab() : null}
      </div>

      {editorPath ? (
        <div className="fixed inset-0 z-[2100] flex items-center justify-center bg-slate-950/40 p-4">
          <div className="flex max-h-[90vh] w-full max-w-4xl flex-col rounded-lg bg-white shadow-xl">
            <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
              <div>
                <h3 className="font-semibold text-slate-900">Редактирование файла</h3>
                <p className="text-xs text-slate-500">{editorPath}</p>
              </div>
              <button type="button" onClick={() => setEditorPath(null)} className={cardIconButtonClass()} aria-label="Закрыть">
                <X className="h-4 w-4" />
              </button>
            </div>
            <textarea value={editorContent} onChange={(event) => setEditorContent(event.target.value)} className="min-h-[50vh] flex-1 resize-none border-0 p-4 font-mono text-sm outline-none" />
            <div className="flex justify-end gap-2 border-t border-slate-200 px-4 py-3">
              <button type="button" onClick={() => setEditorPath(null)} className={iconButtonClass()}>Отмена</button>
              <button type="button" onClick={() => void saveTextEditor()} disabled={loadingAction} className={iconButtonClass('primary')}>
                <Save className="h-4 w-4" />
                Сохранить
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {renderPathDialog()}
    </div>
  );
}

export default YandexDisk;
