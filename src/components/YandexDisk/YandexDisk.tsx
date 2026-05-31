import {
  ArrowUp,
  Cloud,
  Copy,
  Download,
  Edit3,
  Eye,
  File,
  Folder,
  KeyRound,
  Loader2,
  MoveRight,
  Plus,
  RefreshCw,
  Save,
  SlidersHorizontal,
  Trash2,
  UploadCloud,
  X,
} from 'lucide-react';
import { ChangeEvent, ReactNode, useEffect, useMemo, useRef, useState } from 'react';
import {
  apiService,
  type UpdateYandexDiskCrmSyncSettingsRequest,
  type YandexDiskCrmSyncPreviewDto,
  type YandexDiskCrmSyncSettingsDto,
  type YandexDiskListResponseDto,
  type YandexDiskResourceDto,
  type YandexDiskSettingsDto,
} from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { useToast } from '../../hooks/useToast';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { defaultRestrictedMenuPermissions } from '../../types/permissions';

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

function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString('ru-RU') : '—';
}

const defaultCrmSyncDraft: UpdateYandexDiskCrmSyncSettingsRequest = {
  enabled: false,
  syncInvoices: true,
  syncActs: true,
  syncContracts: true,
  syncCaseDocuments: true,
  syncTaskFiles: false,
  overwriteExisting: false,
  folderTemplate: 'CRM/{clientName}/{caseNumber}/{documentType}',
  fileNameTemplate: '{documentDate}-{documentNumber}-{documentTitle}',
};

export function YandexDisk() {
  const toast = useToast();
  const { user } = useAuth();
  const rolePermissions = useRolePermissions(user?.role?.id);
  const isAdminRole = (user?.role?.name ?? '').toLowerCase() === 'admin';
  const permissions = rolePermissions.yandexDisk ?? defaultRestrictedMenuPermissions;
  const canCreate = isAdminRole || permissions.canCreate;
  const canEdit = isAdminRole || permissions.canEdit;
  const canDelete = isAdminRole || permissions.canDelete;
  const canExport = isAdminRole || permissions.canExport;
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const [settings, setSettings] = useState<YandexDiskSettingsDto | null>(null);
  const [crmSyncSettings, setCrmSyncSettings] = useState<YandexDiskCrmSyncSettingsDto | null>(null);
  const [crmSyncPreview, setCrmSyncPreview] = useState<YandexDiskCrmSyncPreviewDto | null>(null);
  const [crmSyncDraft, setCrmSyncDraft] = useState<UpdateYandexDiskCrmSyncSettingsRequest>(defaultCrmSyncDraft);
  const [tokenDraft, setTokenDraft] = useState('');
  const [rootPathDraft, setRootPathDraft] = useState('disk:/PayPlanner');
  const [currentPath, setCurrentPath] = useState('/');
  const [listing, setListing] = useState<YandexDiskListResponseDto | null>(null);
  const [newFolderName, setNewFolderName] = useState('');
  const [loading, setLoading] = useState(false);
  const [savingSettings, setSavingSettings] = useState(false);
  const [savingCrmSync, setSavingCrmSync] = useState(false);
  const [busyPath, setBusyPath] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editorPath, setEditorPath] = useState<string | null>(null);
  const [editorContent, setEditorContent] = useState('');
  const [editorLoading, setEditorLoading] = useState(false);

  const sortedItems = useMemo(() => {
    const items = listing?.items ?? [];
    return [...items].sort((a, b) => {
      if (a.type !== b.type) return a.type === 'dir' ? -1 : 1;
      return a.name.localeCompare(b.name, 'ru');
    });
  }, [listing]);

  const loadSettings = async () => {
    const [loaded, loadedCrmSyncSettings, loadedCrmSyncPreview] = await Promise.all([
      apiService.getYandexDiskSettings(),
      apiService.getYandexDiskCrmSyncSettings(),
      apiService.getYandexDiskCrmSyncPreview(),
    ]);
    setSettings(loaded);
    setRootPathDraft(loaded.rootPath || 'disk:/PayPlanner');
    setCrmSyncSettings(loadedCrmSyncSettings);
    setCrmSyncPreview(loadedCrmSyncPreview);
    setCrmSyncDraft({
      enabled: loadedCrmSyncSettings.enabled,
      syncInvoices: loadedCrmSyncSettings.syncInvoices,
      syncActs: loadedCrmSyncSettings.syncActs,
      syncContracts: loadedCrmSyncSettings.syncContracts,
      syncCaseDocuments: loadedCrmSyncSettings.syncCaseDocuments,
      syncTaskFiles: loadedCrmSyncSettings.syncTaskFiles,
      overwriteExisting: loadedCrmSyncSettings.overwriteExisting,
      folderTemplate: loadedCrmSyncSettings.folderTemplate,
      fileNameTemplate: loadedCrmSyncSettings.fileNameTemplate,
    });
    return loaded;
  };

  const loadListing = async (path = currentPath, silent = false) => {
    if (!silent) setLoading(true);
    setError(null);
    try {
      const loadedSettings = settings ?? await loadSettings();
      if (!loadedSettings.isConfigured) {
        setListing(null);
        return;
      }
      const data = await apiService.listYandexDiskResources(path);
      setListing(data);
      setCurrentPath(data.current.path || path || '/');
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить Яндекс.Диск.'));
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => {
    void loadSettings().then((loaded) => {
      if (loaded.isConfigured) {
        void loadListing('/');
      }
    });
  }, []);

  const saveSettings = async () => {
    if (!canEdit) return;
    setSavingSettings(true);
    setError(null);
    try {
      const updated = await apiService.updateYandexDiskSettings({
        rootPath: rootPathDraft,
        oAuthToken: tokenDraft.trim() || undefined,
      });
      setSettings(updated);
      setTokenDraft('');
      toast.success('Настройки Яндекс.Диска сохранены.', 'Я.Диск');
      if (updated.isConfigured) {
        await loadListing('/');
      }
    } catch (saveError) {
      setError(extractDisplayErrorMessage(saveError, 'Не удалось сохранить настройки Яндекс.Диска.'));
    } finally {
      setSavingSettings(false);
    }
  };

  const clearToken = async () => {
    if (!canEdit) return;
    if (!window.confirm('Удалить сохраненный OAuth-токен Яндекс.Диска?')) return;
    setSavingSettings(true);
    try {
      const updated = await apiService.updateYandexDiskSettings({ rootPath: rootPathDraft, clearToken: true });
      setSettings(updated);
      setListing(null);
      toast.success('OAuth-токен удален.', 'Я.Диск');
    } catch (clearError) {
      setError(extractDisplayErrorMessage(clearError, 'Не удалось удалить токен.'));
    } finally {
      setSavingSettings(false);
    }
  };

  const saveCrmSyncSettings = async () => {
    if (!canEdit) return;
    setSavingCrmSync(true);
    setError(null);
    try {
      const updated = await apiService.updateYandexDiskCrmSyncSettings(crmSyncDraft);
      setCrmSyncSettings(updated);
      setCrmSyncDraft({
        enabled: updated.enabled,
        syncInvoices: updated.syncInvoices,
        syncActs: updated.syncActs,
        syncContracts: updated.syncContracts,
        syncCaseDocuments: updated.syncCaseDocuments,
        syncTaskFiles: updated.syncTaskFiles,
        overwriteExisting: updated.overwriteExisting,
        folderTemplate: updated.folderTemplate,
        fileNameTemplate: updated.fileNameTemplate,
      });
      setCrmSyncPreview(await apiService.getYandexDiskCrmSyncPreview());
      toast.success('Правила синхронизации CRM сохранены.', 'Я.Диск');
    } catch (saveError) {
      setError(extractDisplayErrorMessage(saveError, 'Не удалось сохранить правила синхронизации CRM.'));
    } finally {
      setSavingCrmSync(false);
    }
  };

  const refreshCrmSyncPreview = async () => {
    try {
      setCrmSyncPreview(await apiService.getYandexDiskCrmSyncPreview());
    } catch (previewError) {
      setError(extractDisplayErrorMessage(previewError, 'Не удалось обновить предпросмотр синхронизации CRM.'));
    }
  };

  const createFolder = async () => {
    if (!canCreate) return;
    const name = newFolderName.trim();
    if (!name) return;
    setBusyPath(joinPath(currentPath, name));
    try {
      await apiService.createYandexDiskFolder(joinPath(currentPath, name));
      setNewFolderName('');
      toast.success('Папка создана.', 'Я.Диск');
      await loadListing(currentPath, true);
    } catch (createError) {
      setError(extractDisplayErrorMessage(createError, 'Не удалось создать папку.'));
    } finally {
      setBusyPath(null);
    }
  };

  const uploadFile = async (event: ChangeEvent<HTMLInputElement>) => {
    if (!canCreate) return;
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    const targetPath = joinPath(currentPath, file.name);
    setBusyPath(targetPath);
    try {
      await apiService.uploadYandexDiskFile(targetPath, file, true);
      toast.success('Файл загружен.', 'Я.Диск');
      await loadListing(currentPath, true);
    } catch (uploadError) {
      setError(extractDisplayErrorMessage(uploadError, 'Не удалось загрузить файл.'));
    } finally {
      setBusyPath(null);
    }
  };

  const downloadFile = async (item: YandexDiskResourceDto) => {
    if (!canExport) return;
    setBusyPath(item.path);
    try {
      const result = await apiService.downloadYandexDiskFile(item.path);
      const url = URL.createObjectURL(result.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = result.fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (downloadError) {
      setError(extractDisplayErrorMessage(downloadError, 'Не удалось скачать файл.'));
    } finally {
      setBusyPath(null);
    }
  };

  const deleteResource = async (item: YandexDiskResourceDto) => {
    if (!canDelete) return;
    if (!window.confirm(`Удалить "${item.name}" с Яндекс.Диска?`)) return;
    setBusyPath(item.path);
    try {
      await apiService.deleteYandexDiskResource(item.path, false);
      toast.success('Ресурс удален.', 'Я.Диск');
      await loadListing(currentPath, true);
    } catch (deleteError) {
      setError(extractDisplayErrorMessage(deleteError, 'Не удалось удалить ресурс.'));
    } finally {
      setBusyPath(null);
    }
  };

  const copyResource = async (item: YandexDiskResourceDto) => {
    if (!canCreate) return;
    const target = window.prompt('Куда скопировать ресурс?', joinPath(parentPath(item.path), `copy-${item.name}`));
    if (!target) return;
    setBusyPath(item.path);
    try {
      await apiService.copyYandexDiskResource(item.path, target, false);
      toast.success('Ресурс скопирован.', 'Я.Диск');
      await loadListing(currentPath, true);
    } catch (copyError) {
      setError(extractDisplayErrorMessage(copyError, 'Не удалось скопировать ресурс.'));
    } finally {
      setBusyPath(null);
    }
  };

  const moveResource = async (item: YandexDiskResourceDto) => {
    if (!canEdit) return;
    const target = window.prompt('Новый путь ресурса:', item.path);
    if (!target || target === item.path) return;
    setBusyPath(item.path);
    try {
      await apiService.moveYandexDiskResource(item.path, target, false);
      toast.success('Ресурс перемещен.', 'Я.Диск');
      await loadListing(currentPath, true);
    } catch (moveError) {
      setError(extractDisplayErrorMessage(moveError, 'Не удалось переместить ресурс.'));
    } finally {
      setBusyPath(null);
    }
  };

  const openTextEditor = async (item: YandexDiskResourceDto) => {
    if (!canEdit) return;
    setEditorPath(item.path);
    setEditorContent('');
    setEditorLoading(true);
    try {
      const file = await apiService.getYandexDiskTextFile(item.path);
      setEditorPath(file.path);
      setEditorContent(file.content);
    } catch (editorError) {
      setEditorPath(null);
      setError(extractDisplayErrorMessage(editorError, 'Не удалось открыть файл для редактирования.'));
    } finally {
      setEditorLoading(false);
    }
  };

  const saveTextEditor = async () => {
    if (!canEdit) return;
    if (!editorPath) return;
    setEditorLoading(true);
    try {
      await apiService.saveYandexDiskTextFile(editorPath, editorContent, true);
      toast.success('Файл сохранен.', 'Я.Диск');
      await loadListing(currentPath, true);
      setEditorPath(null);
    } catch (editorError) {
      setError(extractDisplayErrorMessage(editorError, 'Не удалось сохранить файл.'));
    } finally {
      setEditorLoading(false);
    }
  };

  return (
    <div className="space-y-4 p-4 lg:p-6">
      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
        <div className="flex flex-col gap-3 xl:flex-row xl:items-end xl:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-700">
              <Cloud className="h-4 w-4" />
              <span>Яндекс.Диск</span>
            </div>
            <h2 className="mt-1 text-xl font-semibold text-slate-900">Файлы и папки Я.Диска</h2>
            <p className="mt-1 text-sm text-slate-600">
              Статус: {settings?.isConfigured ? `подключен (${settings.tokenPreview ?? 'токен сохранен'})` : 'нужен OAuth-токен'}.
            </p>
          </div>

          <div className="grid gap-2 md:grid-cols-[minmax(180px,280px)_minmax(180px,260px)_auto_auto]">
            <label className="block">
              <span className="text-xs font-semibold text-slate-600">Корневая папка</span>
              <input
                value={rootPathDraft}
                onChange={(event) => setRootPathDraft(event.target.value)}
                disabled={!canEdit}
                className="mt-1 h-10 w-full rounded-lg border border-slate-300 px-3 text-sm outline-none focus:border-sky-500 focus:ring-2 focus:ring-sky-100"
                placeholder="disk:/PayPlanner"
              />
            </label>
            <label className="block">
              <span className="text-xs font-semibold text-slate-600">OAuth-токен</span>
              <div className="relative mt-1">
                <KeyRound className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                <input
                  type="password"
                  value={tokenDraft}
                  onChange={(event) => setTokenDraft(event.target.value)}
                  disabled={!canEdit}
                  className="h-10 w-full rounded-lg border border-slate-300 pl-9 pr-3 text-sm outline-none focus:border-sky-500 focus:ring-2 focus:ring-sky-100"
                  placeholder="Новый токен"
                />
              </div>
            </label>
            <button
              type="button"
              onClick={() => void saveSettings()}
              disabled={savingSettings || !canEdit}
              className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-sky-600 px-4 text-sm font-semibold text-white hover:bg-sky-700 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {savingSettings ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Сохранить
            </button>
            <button
              type="button"
              onClick={() => void clearToken()}
              disabled={savingSettings || !settings?.isConfigured || !canEdit}
              className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <X className="h-4 w-4" />
              Сбросить
            </button>
          </div>
        </div>
      </section>

      {error ? (
        <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
      ) : null}

      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
        <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div className="min-w-0 xl:max-w-sm">
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-700">
              <SlidersHorizontal className="h-4 w-4" />
              <span>Синхронизация CRM</span>
            </div>
            <h3 className="mt-1 text-lg font-semibold text-slate-900">Правила для документов</h3>
            <p className="mt-1 text-sm text-slate-600">
              Настройте, какие CRM-документы будут отправляться на Яндекс.Диск и по какому шаблону формировать папки.
            </p>
            {crmSyncSettings?.updatedAt ? (
              <p className="mt-2 text-xs text-slate-500">Обновлено: {formatDate(crmSyncSettings.updatedAt)}</p>
            ) : null}
          </div>

          <div className="grid flex-1 gap-4 lg:grid-cols-[minmax(260px,1fr)_minmax(260px,1fr)]">
            <div className="space-y-3">
              <label className="flex items-center gap-2 text-sm font-medium text-slate-700">
                <input
                  type="checkbox"
                  checked={crmSyncDraft.enabled}
                  onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, enabled: event.target.checked }))}
                  disabled={!canEdit}
                  className="h-4 w-4 rounded border-slate-300 text-sky-600 focus:ring-sky-500"
                />
                Включить правила синхронизации
              </label>
              <div className="grid gap-2 sm:grid-cols-2">
                {[
                  ['syncInvoices', 'Счета'],
                  ['syncActs', 'Акты'],
                  ['syncContracts', 'Договоры'],
                  ['syncCaseDocuments', 'Документы дел'],
                  ['syncTaskFiles', 'Файлы задач'],
                  ['overwriteExisting', 'Перезаписывать файлы'],
                ].map(([field, label]) => (
                  <label key={field} className="flex items-center gap-2 text-sm text-slate-700">
                    <input
                      type="checkbox"
                      checked={Boolean(crmSyncDraft[field as keyof UpdateYandexDiskCrmSyncSettingsRequest])}
                      onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, [field]: event.target.checked }))}
                      disabled={!canEdit}
                      className="h-4 w-4 rounded border-slate-300 text-sky-600 focus:ring-sky-500"
                    />
                    {label}
                  </label>
                ))}
              </div>
            </div>

            <div className="space-y-3">
              <label className="block">
                <span className="text-xs font-semibold text-slate-600">Шаблон папки</span>
                <input
                  value={crmSyncDraft.folderTemplate ?? ''}
                  onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, folderTemplate: event.target.value }))}
                  disabled={!canEdit}
                  className="mt-1 h-10 w-full rounded-lg border border-slate-300 px-3 text-sm outline-none focus:border-sky-500 focus:ring-2 focus:ring-sky-100"
                  placeholder="CRM/{clientName}/{caseNumber}/{documentType}"
                />
              </label>
              <label className="block">
                <span className="text-xs font-semibold text-slate-600">Шаблон имени файла</span>
                <input
                  value={crmSyncDraft.fileNameTemplate ?? ''}
                  onChange={(event) => setCrmSyncDraft((draft) => ({ ...draft, fileNameTemplate: event.target.value }))}
                  disabled={!canEdit}
                  className="mt-1 h-10 w-full rounded-lg border border-slate-300 px-3 text-sm outline-none focus:border-sky-500 focus:ring-2 focus:ring-sky-100"
                  placeholder="{documentDate}-{documentNumber}-{documentTitle}"
                />
              </label>
              <div className="flex flex-wrap gap-1.5">
                {(crmSyncSettings?.availableTokens ?? []).map((token) => (
                  <span key={token} className="rounded border border-slate-200 bg-slate-50 px-2 py-1 text-xs font-medium text-slate-600">
                    {'{'}{token}{'}'}
                  </span>
                ))}
              </div>
              <button
                type="button"
                onClick={() => void saveCrmSyncSettings()}
                disabled={savingCrmSync || !canEdit}
                className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-sky-600 px-4 text-sm font-semibold text-white hover:bg-sky-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {savingCrmSync ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Сохранить правила
              </button>
            </div>
          </div>
        </div>
      </section>

      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
        <div className="flex flex-col gap-3 border-b border-slate-100 pb-3 md:flex-row md:items-center md:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-700">
              <Eye className="h-4 w-4" />
              <span>Dry-run CRM</span>
            </div>
            <h3 className="mt-1 text-lg font-semibold text-slate-900">Предпросмотр путей синхронизации</h3>
          </div>
          <button
            type="button"
            onClick={() => void refreshCrmSyncPreview()}
            disabled={savingCrmSync}
            className="inline-flex h-9 items-center justify-center gap-2 rounded-lg border border-slate-300 px-3 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <RefreshCw className="h-4 w-4" />
            Обновить
          </button>
        </div>
        <div className="mt-3 overflow-x-auto">
          <table className="min-w-full divide-y divide-slate-200 text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left font-semibold">Тип</th>
                <th className="px-3 py-2 text-left font-semibold">Статус</th>
                <th className="px-3 py-2 text-left font-semibold">Путь</th>
                <th className="px-3 py-2 text-left font-semibold">Действие</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {(crmSyncPreview?.items ?? []).map((item) => (
                <tr key={item.documentKind} className={item.enabled ? 'bg-white' : 'bg-slate-50 text-slate-500'}>
                  <td className="whitespace-nowrap px-3 py-2 font-medium text-slate-800">{item.documentType}</td>
                  <td className="whitespace-nowrap px-3 py-2">
                    {item.enabled ? 'Будет отправляться' : 'Отключено'}
                  </td>
                  <td className="max-w-[520px] px-3 py-2">
                    <div className="truncate font-mono text-xs text-slate-700" title={item.fullPath}>{item.fullPath}</div>
                  </td>
                  <td className="whitespace-nowrap px-3 py-2">{item.operationText}</td>
                </tr>
              ))}
              {!crmSyncPreview?.items?.length ? (
                <tr>
                  <td colSpan={4} className="px-3 py-6 text-center text-sm text-slate-500">Предпросмотр пока не загружен.</td>
                </tr>
              ) : null}
            </tbody>
          </table>
        </div>
      </section>

      <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-col gap-3 border-b border-slate-200 p-4 xl:flex-row xl:items-center xl:justify-between">
          <div className="min-w-0">
            <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Текущая папка</div>
            <div className="mt-1 truncate text-lg font-semibold text-slate-900">{currentPath}</div>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void loadListing(currentPath)}
              disabled={loading || !settings?.isConfigured}
              className="inline-flex h-9 items-center gap-2 rounded-lg border border-slate-300 px-3 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
              Обновить
            </button>
            <button
              type="button"
              onClick={() => void loadListing(parentPath(currentPath))}
              disabled={currentPath === '/' || loading}
              className="inline-flex h-9 items-center gap-2 rounded-lg border border-slate-300 px-3 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <ArrowUp className="h-4 w-4" />
              Вверх
            </button>
            <button
              type="button"
              onClick={() => fileInputRef.current?.click()}
              disabled={!settings?.isConfigured || !canCreate}
              className="inline-flex h-9 items-center gap-2 rounded-lg bg-slate-900 px-3 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <UploadCloud className="h-4 w-4" />
              Загрузить
            </button>
            <input ref={fileInputRef} type="file" className="hidden" onChange={uploadFile} />
          </div>
        </div>

        <div className="flex flex-col gap-2 border-b border-slate-200 p-4 md:flex-row md:items-center">
          <input
            value={newFolderName}
            onChange={(event) => setNewFolderName(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') void createFolder();
            }}
            className="h-10 min-w-0 flex-1 rounded-lg border border-slate-300 px-3 text-sm outline-none focus:border-sky-500 focus:ring-2 focus:ring-sky-100"
            placeholder="Название новой папки"
            disabled={!settings?.isConfigured || !canCreate}
          />
          <button
            type="button"
            onClick={() => void createFolder()}
            disabled={!newFolderName.trim() || !settings?.isConfigured || !canCreate}
            className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <Plus className="h-4 w-4" />
            Создать папку
          </button>
        </div>

        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-4 py-3">Имя</th>
                <th className="px-4 py-3">Тип</th>
                <th className="px-4 py-3">Размер</th>
                <th className="px-4 py-3">Изменено</th>
                <th className="px-4 py-3 text-right">Действия</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {loading ? (
                <tr>
                  <td colSpan={5} className="px-4 py-10 text-center text-slate-500">
                    <Loader2 className="mx-auto mb-2 h-5 w-5 animate-spin" />
                    Загружаем Яндекс.Диск
                  </td>
                </tr>
              ) : sortedItems.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-10 text-center text-slate-500">
                    {settings?.isConfigured ? 'В папке пока нет файлов.' : 'Сохраните OAuth-токен, чтобы открыть Яндекс.Диск.'}
                  </td>
                </tr>
              ) : (
                sortedItems.map((item) => {
                  const isFolder = item.type === 'dir';
                  const isBusy = busyPath === item.path;
                  return (
                    <tr key={item.fullPath || item.path} className="hover:bg-slate-50">
                      <td className="max-w-[360px] px-4 py-3">
                        <button
                          type="button"
                          onClick={() => isFolder ? void loadListing(item.path) : undefined}
                          className={`inline-flex max-w-full items-center gap-2 text-left font-medium ${isFolder ? 'text-sky-700 hover:text-sky-900' : 'text-slate-800'}`}
                        >
                          {isFolder ? <Folder className="h-4 w-4 shrink-0" /> : <File className="h-4 w-4 shrink-0" />}
                          <span className="truncate">{item.name}</span>
                        </button>
                        <div className="mt-0.5 truncate text-xs text-slate-500">{item.path}</div>
                      </td>
                      <td className="px-4 py-3 text-slate-600">{isFolder ? 'Папка' : item.mimeType || 'Файл'}</td>
                      <td className="px-4 py-3 text-slate-600">{isFolder ? '—' : formatSize(item.size)}</td>
                      <td className="px-4 py-3 text-slate-600">{formatDate(item.modified)}</td>
                      <td className="px-4 py-3">
                        <div className="flex justify-end gap-1">
                          {isBusy ? <Loader2 className="h-8 w-8 animate-spin rounded-lg p-2 text-slate-500" /> : null}
                          {!isFolder ? (
                            <>
                              {canExport ? <IconButton label="Скачать" onClick={() => void downloadFile(item)} icon={<Download className="h-4 w-4" />} /> : null}
                              {canEdit ? <IconButton label="Редактировать текст" onClick={() => void openTextEditor(item)} icon={<Edit3 className="h-4 w-4" />} /> : null}
                            </>
                          ) : null}
                          {canCreate ? <IconButton label="Копировать" onClick={() => void copyResource(item)} icon={<Copy className="h-4 w-4" />} /> : null}
                          {canEdit ? <IconButton label="Переместить" onClick={() => void moveResource(item)} icon={<MoveRight className="h-4 w-4" />} /> : null}
                          {canDelete ? <IconButton label="Удалить" danger onClick={() => void deleteResource(item)} icon={<Trash2 className="h-4 w-4" />} /> : null}
                        </div>
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </section>

      {editorPath ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/50 p-4">
          <div className="flex max-h-[88vh] w-full max-w-4xl flex-col rounded-lg bg-white shadow-xl">
            <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
              <div className="min-w-0">
                <h3 className="truncate text-base font-semibold text-slate-900">Редактирование файла</h3>
                <p className="truncate text-xs text-slate-500">{editorPath}</p>
              </div>
              <button type="button" onClick={() => setEditorPath(null)} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100" aria-label="Закрыть">
                <X className="h-4 w-4" />
              </button>
            </div>
            <textarea
              value={editorContent}
              onChange={(event) => setEditorContent(event.target.value)}
              className="min-h-[360px] flex-1 resize-none border-0 p-4 font-mono text-sm outline-none"
              disabled={editorLoading}
            />
            <div className="flex justify-end gap-2 border-t border-slate-200 px-4 py-3">
              <button type="button" onClick={() => setEditorPath(null)} className="rounded-lg border border-slate-300 px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50">
                Отмена
              </button>
              <button
                type="button"
                onClick={() => void saveTextEditor()}
                disabled={editorLoading}
                className="inline-flex items-center gap-2 rounded-lg bg-sky-600 px-4 py-2 text-sm font-semibold text-white hover:bg-sky-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {editorLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Сохранить
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function IconButton({
  label,
  icon,
  onClick,
  danger = false,
}: {
  label: string;
  icon: ReactNode;
  onClick: () => void;
  danger?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      className={`inline-flex h-8 w-8 items-center justify-center rounded-lg border text-sm ${
        danger
          ? 'border-rose-200 text-rose-600 hover:bg-rose-50'
          : 'border-slate-200 text-slate-600 hover:bg-slate-100'
      }`}
    >
      {icon}
    </button>
  );
}
