import { useCallback, useEffect, useMemo, useState } from 'react';
import { Eye, History, RefreshCw, RotateCcw, X } from 'lucide-react';
import { apiService, type DocumentTemplateVersionDto, type InvoiceDocumentTemplateSettingsDto } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type DocumentTemplateKind = 'invoice' | 'contract';

interface DocumentTemplateHistoryPanelProps {
  kind: DocumentTemplateKind;
  refreshKey: number;
  onRestored: (settings: InvoiceDocumentTemplateSettingsDto) => void;
}

const labels: Record<DocumentTemplateKind, string> = {
  invoice: 'счета',
  contract: 'договора',
};

export function DocumentTemplateHistoryPanel({ kind, refreshKey, onRestored }: DocumentTemplateHistoryPanelProps) {
  const { confirm } = useDialog();
  const toast = useToast();
  const [versions, setVersions] = useState<DocumentTemplateVersionDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const [preview, setPreview] = useState<InvoiceDocumentTemplateSettingsDto | null>(null);
  const [previewVersion, setPreviewVersion] = useState<DocumentTemplateVersionDto | null>(null);
  const [previewTemplateKey, setPreviewTemplateKey] = useState('');
  const [restoringId, setRestoringId] = useState<number | null>(null);

  const loadVersions = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = kind === 'invoice'
        ? await apiService.getInvoiceDocumentTemplateHistory()
        : await apiService.getContractDocumentTemplateHistory();
      setVersions(response);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, `Не удалось загрузить историю шаблона ${labels[kind]}`));
    } finally {
      setLoading(false);
    }
  }, [kind]);

  useEffect(() => {
    void loadVersions();
  }, [loadVersions, refreshKey]);

  const previewTemplate = useMemo(
    () => preview?.templates.find((template) => template.key === previewTemplateKey) ?? preview?.templates[0] ?? null,
    [preview, previewTemplateKey],
  );

  const openPreview = async (version: DocumentTemplateVersionDto) => {
    setError(null);
    try {
      const response = kind === 'invoice'
        ? await apiService.getInvoiceDocumentTemplateVersion(version.id)
        : await apiService.getContractDocumentTemplateVersion(version.id);
      setPreview(response);
      setPreviewVersion(version);
      setPreviewTemplateKey(response.activeTemplateKey || response.templates[0]?.key || '');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось открыть сохраненную версию шаблона'));
    }
  };

  const restore = async (version: DocumentTemplateVersionDto) => {
    const approved = await confirm({
      title: `Восстановить версию №${version.revision}`,
      description: 'Текущие шаблоны останутся в истории, а выбранная версия станет новой актуальной версией.',
      confirmLabel: 'Восстановить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setRestoringId(version.id);
    setError(null);
    try {
      const response = kind === 'invoice'
        ? await apiService.restoreInvoiceDocumentTemplateVersion(version.id)
        : await apiService.restoreContractDocumentTemplateVersion(version.id);
      onRestored(response);
      setPreview(null);
      setPreviewVersion(null);
      await loadVersions();
      toast.success(`Версия №${version.revision} восстановлена.`, 'История шаблонов');
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось восстановить версию шаблона');
      setError(message);
      toast.error(message, 'История шаблонов');
    } finally {
      setRestoringId(null);
    }
  };

  return (
    <section className="mt-4 rounded-xl border border-gray-200 bg-gray-50 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          className="inline-flex items-center gap-2 text-sm font-semibold text-gray-900"
          aria-expanded={expanded}
        >
          <History className="h-4 w-4 text-blue-600" />
          История версий{versions.length > 0 ? `: ${versions.length}` : ''}
        </button>
        <button
          type="button"
          onClick={() => void loadVersions()}
          disabled={loading}
          className="inline-flex items-center gap-1.5 rounded-lg border border-gray-300 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-100 disabled:opacity-60"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          Обновить
        </button>
      </div>

      {error ? <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div> : null}

      {expanded ? (
        <div className="mt-3 space-y-2">
          {!loading && versions.length === 0 ? (
            <div className="rounded-lg border border-dashed border-gray-300 bg-white px-3 py-4 text-sm text-gray-500">
              История появится после первого фактического изменения шаблона.
            </div>
          ) : null}
          {versions.map((version) => (
            <div key={version.id} className="flex flex-col gap-2 rounded-lg border border-gray-200 bg-white p-3 md:flex-row md:items-center md:justify-between">
              <div className="min-w-0 text-sm">
                <div className="font-semibold text-gray-900">Версия №{version.revision}</div>
                <div className="mt-0.5 text-xs text-gray-500">
                  {new Date(version.createdAt).toLocaleString('ru-RU')} · {version.createdByUserName || 'Система'}
                </div>
                <div className="mt-1 truncate text-xs text-gray-600">
                  Активный: {version.activeTemplateKey || 'не выбран'} · {version.templateNames.join(', ') || 'без названия'}
                </div>
              </div>
              <div className="flex shrink-0 gap-2">
                <button
                  type="button"
                  onClick={() => void openPreview(version)}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-gray-300 px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
                >
                  <Eye className="h-3.5 w-3.5" />
                  Просмотреть
                </button>
                <button
                  type="button"
                  onClick={() => void restore(version)}
                  disabled={restoringId !== null}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-60"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  {restoringId === version.id ? 'Восстанавливаем...' : 'Восстановить'}
                </button>
              </div>
            </div>
          ))}
        </div>
      ) : null}

      {preview && previewVersion && previewTemplate ? (
        <div className="mt-3 rounded-xl border border-blue-200 bg-white p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="text-sm font-semibold text-gray-900">Предпросмотр версии №{previewVersion.revision}</div>
            <button type="button" onClick={() => setPreview(null)} className="rounded-lg p-1 text-gray-500 hover:bg-gray-100" aria-label="Закрыть предпросмотр">
              <X className="h-4 w-4" />
            </button>
          </div>
          {preview.templates.length > 1 ? (
            <select
              value={previewTemplate.key}
              onChange={(event) => setPreviewTemplateKey(event.target.value)}
              className="mt-3 w-full rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm"
            >
              {preview.templates.map((template) => <option key={template.key} value={template.key}>{template.name}</option>)}
            </select>
          ) : null}
          <div className="mt-3 overflow-hidden rounded-lg border border-gray-200 bg-white">
            <iframe title={`Версия ${previewVersion.revision}: ${previewTemplate.name}`} srcDoc={previewTemplate.html} sandbox="" className="h-[36rem] w-full bg-white" />
          </div>
        </div>
      ) : null}
    </section>
  );
}
