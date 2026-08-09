import { SystemCheckbox } from '../ui/SystemCheckbox';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Check, Copy, Eye, FileText, History, Loader2, Plus, RefreshCw, RotateCcw, Save, Search } from 'lucide-react';

import { apiService, type DocumentTemplateVersionDto } from '../../services/api';
import type { ClientCaseDocumentTemplate, ClientCaseDocumentTemplateToken } from '../../types';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateInTimeZone } from '../../utils/timeZoneDate';

function createTemplateDraft(index: number): ClientCaseDocumentTemplate {
  return {
    key: `case-template-${index}`,
    label: `Шаблон дела ${index}`,
    documentType: 'Документ',
    heading: `Шаблон дела ${index}`,
    description: 'Новый шаблон документа дела.',
    content: '<p>{{case.title}}</p>\n<p>{{client.name}}</p>',
    caseTypes: [],
    version: 1,
    isActive: true,
  };
}

function parseCaseTypes(value: string): string[] {
  return Array.from(new Set(value
    .split(/[\n,]+/)
    .map((item) => item.trim())
    .filter(Boolean)));
}

function formatCaseTypes(value: string[] | undefined): string {
  return (value ?? []).join(', ');
}

function formatTemplateDate(value?: string | null, timeZoneId?: string | null): string {
  if (!value) return '—';
  const formattedValue = formatDateInTimeZone(value, timeZoneId);
  if (formattedValue) return formattedValue;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return value;
}

function cloneTemplate(template: ClientCaseDocumentTemplate): ClientCaseDocumentTemplate {
  return {
    ...template,
    caseTypes: template.caseTypes ?? [],
    content: template.content ?? '',
    description: template.description ?? '',
    isActive: template.isActive ?? true,
  };
}

export function CaseDocumentTemplateSettings() {
  const toast = useToast();
  const timeZoneId = useUserTimeZone();
  const [templates, setTemplates] = useState<ClientCaseDocumentTemplate[]>([]);
  const [originalKeys, setOriginalKeys] = useState<Set<string>>(new Set());
  const [tokens, setTokens] = useState<ClientCaseDocumentTemplateToken[]>([]);
  const [selectedKey, setSelectedKey] = useState('');
  const [tokenSearch, setTokenSearch] = useState('');
  const [copiedToken, setCopiedToken] = useState<string | null>(null);
  const [previewCaseId, setPreviewCaseId] = useState('');
  const [previewHtml, setPreviewHtml] = useState('');
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyVersions, setHistoryVersions] = useState<DocumentTemplateVersionDto[]>([]);
  const [historyPreview, setHistoryPreview] = useState<ClientCaseDocumentTemplate[] | null>(null);
  const [historyPreviewRevision, setHistoryPreviewRevision] = useState<number | null>(null);
  const [restoringVersionId, setRestoringVersionId] = useState<number | null>(null);

  const load = useCallback(async (showToast = false) => {
    setLoading(true);
    setError(null);
    setMessage(null);
    try {
      const [templateResponse, tokenResponse] = await Promise.all([
        apiService.getCaseDocumentTemplates(),
        apiService.getCaseDocumentTemplateTokens(),
      ]);
      const normalizedTemplates = templateResponse.map(cloneTemplate);
      setTemplates(normalizedTemplates);
      setOriginalKeys(new Set(normalizedTemplates.map((item) => item.key)));
      setTokens(tokenResponse);
      setPreviewHtml('');
      setPreviewError(null);
      setSelectedKey((current) => (
        current && normalizedTemplates.some((item) => item.key === current)
          ? current
          : normalizedTemplates[0]?.key ?? ''
      ));
      if (showToast) {
        toast.success('Библиотека шаблонов документов обновлена.', 'Настройки');
      }
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось загрузить шаблоны документов дела.');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void load();
  }, [load]);

  const loadHistory = useCallback(async () => {
    setHistoryLoading(true);
    try {
      setHistoryVersions(await apiService.getCaseDocumentTemplateHistory());
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось загрузить историю шаблонов документов дела.');
      setError(nextError);
      toast.error(nextError, 'История шаблонов');
    } finally {
      setHistoryLoading(false);
    }
  }, [toast]);

  const toggleHistory = async () => {
    const nextOpen = !historyOpen;
    setHistoryOpen(nextOpen);
    if (nextOpen) {
      await loadHistory();
    }
  };

  const previewHistoryVersion = async (version: DocumentTemplateVersionDto) => {
    setHistoryLoading(true);
    try {
      const snapshot = await apiService.getCaseDocumentTemplateVersion(version.id);
      setHistoryPreview(snapshot.map(cloneTemplate));
      setHistoryPreviewRevision(version.revision);
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось открыть версию шаблонов.');
      setError(nextError);
      toast.error(nextError, 'История шаблонов');
    } finally {
      setHistoryLoading(false);
    }
  };

  const restoreHistoryVersion = async (version: DocumentTemplateVersionDto) => {
    if (!window.confirm(`Восстановить версию №${version.revision} библиотеки шаблонов документов дела?`)) return;
    setRestoringVersionId(version.id);
    setError(null);
    try {
      const restored = (await apiService.restoreCaseDocumentTemplateVersion(version.id)).map(cloneTemplate);
      setTemplates(restored);
      setOriginalKeys(new Set(restored.map((item) => item.key)));
      setSelectedKey((current) => restored.some((item) => item.key === current) ? current : restored[0]?.key ?? '');
      setHistoryPreview(null);
      setHistoryPreviewRevision(null);
      await loadHistory();
      const nextMessage = `Версия №${version.revision} восстановлена.`;
      setMessage(nextMessage);
      toast.success(nextMessage, 'История шаблонов');
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось восстановить версию шаблонов.');
      setError(nextError);
      toast.error(nextError, 'История шаблонов');
    } finally {
      setRestoringVersionId(null);
    }
  };

  const selectedTemplate = useMemo(
    () => templates.find((item) => item.key === selectedKey) ?? null,
    [selectedKey, templates],
  );

  const filteredTokens = useMemo(() => {
    const term = tokenSearch.trim().toLowerCase();
    if (!term) return tokens;
    return tokens.filter((token) =>
      token.key.toLowerCase().includes(term) ||
      token.category.toLowerCase().includes(term) ||
      token.description.toLowerCase().includes(term));
  }, [tokenSearch, tokens]);

  const tokenGroups = useMemo(() => {
    const groups = new Map<string, ClientCaseDocumentTemplateToken[]>();
    filteredTokens.forEach((token) => {
      const bucket = groups.get(token.category) ?? [];
      bucket.push(token);
      groups.set(token.category, bucket);
    });
    return Array.from(groups.entries());
  }, [filteredTokens]);

  const updateSelectedTemplate = (patch: Partial<ClientCaseDocumentTemplate>) => {
    setTemplates((current) => current.map((template) => (
      template.key === selectedKey ? { ...template, ...patch } : template
    )));
    setPreviewHtml('');
    setPreviewError(null);
    setMessage(null);
    setError(null);
  };

  const addTemplate = () => {
    const draft = createTemplateDraft(templates.length + 1);
    setTemplates((current) => [...current, draft]);
    setSelectedKey(draft.key);
    setPreviewHtml('');
    setPreviewError(null);
    setMessage(null);
    setError(null);
  };

  const saveSelectedTemplate = async () => {
    if (!selectedTemplate) return;
    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const payload = {
        key: selectedTemplate.key,
        label: selectedTemplate.label,
        documentType: selectedTemplate.documentType,
        heading: selectedTemplate.heading,
        caseTypes: selectedTemplate.caseTypes ?? [],
        description: selectedTemplate.description,
        content: selectedTemplate.content ?? undefined,
        isActive: selectedTemplate.isActive ?? true,
      };
      const saved = originalKeys.has(selectedTemplate.key)
        ? await apiService.updateCaseDocumentTemplate(selectedTemplate.key, payload)
        : await apiService.createCaseDocumentTemplate(payload);
      const normalized = cloneTemplate(saved);
      setTemplates((current) => current.map((template) => (
        template.key === selectedTemplate.key ? normalized : template
      )));
      setOriginalKeys((current) => new Set(current).add(normalized.key));
      setSelectedKey(normalized.key);
      const nextMessage = 'Шаблон документа дела сохранен.';
      setMessage(nextMessage);
      setPreviewHtml('');
      setPreviewError(null);
      toast.success(nextMessage, 'Настройки');
      if (historyOpen) {
        await loadHistory();
      }
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось сохранить шаблон документа дела.');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setSaving(false);
    }
  };

  const renderPreview = async () => {
    if (!selectedTemplate) return;
    const numericCaseId = Number(previewCaseId);
    if (!Number.isInteger(numericCaseId) || numericCaseId <= 0) {
      const nextError = 'Укажите ID дела для предпросмотра.';
      setPreviewError(nextError);
      toast.error(nextError, 'Шаблоны');
      return;
    }

    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const rendered = await apiService.renderCaseDocumentTemplate(selectedTemplate.key, {
        clientCaseId: numericCaseId,
        documentType: selectedTemplate.documentType,
      });
      setPreviewHtml(rendered.html);
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось построить предпросмотр шаблона.');
      setPreviewError(nextError);
      toast.error(nextError, 'Шаблоны');
    } finally {
      setPreviewLoading(false);
    }
  };

  const copyToken = async (token: ClientCaseDocumentTemplateToken) => {
    try {
      await navigator.clipboard.writeText(token.placeholder);
      setCopiedToken(token.key);
      window.setTimeout(() => setCopiedToken((current) => (current === token.key ? null : current)), 1500);
      toast.success('Переменная скопирована.', 'Шаблоны');
    } catch {
      toast.error('Не удалось скопировать переменную.', 'Шаблоны');
    }
  };

  const insertToken = (token: ClientCaseDocumentTemplateToken) => {
    if (!selectedTemplate) return;
    const currentContent = selectedTemplate.content ?? '';
    const separator = currentContent.length > 0 && !currentContent.endsWith('\n') ? '\n' : '';
    updateSelectedTemplate({ content: `${currentContent}${separator}${token.placeholder}` });
    setMessage(`Переменная ${token.placeholder} добавлена в шаблон.`);
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h5 className="text-base font-semibold text-gray-900">Шаблоны документов дела</h5>
          <p className="mt-1 max-w-3xl text-sm text-gray-600">
            Библиотека управляет HTML-шаблонами, которые доступны во вкладке документов карточки дела.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void toggleHistory()}
            disabled={loading || saving || historyLoading}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
          >
            <History className="h-4 w-4" />
            История версий{historyVersions.length > 0 ? `: ${historyVersions.length}` : ''}
          </button>
          <button
            type="button"
            onClick={() => void load(true)}
            disabled={loading || saving}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
          <button
            type="button"
            onClick={() => void saveSelectedTemplate()}
            disabled={loading || saving || !selectedTemplate}
            className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60"
          >
            <Save className="h-4 w-4" />
            {saving ? 'Сохраняем...' : 'Сохранить'}
          </button>
        </div>
      </div>

      {(message || error) ? (
        <div className={`mt-5 rounded-2xl border px-4 py-3 text-sm ${error ? 'border-red-200 bg-red-50 text-red-700' : 'border-emerald-200 bg-emerald-50 text-emerald-700'}`}>
          {error ?? message}
        </div>
      ) : null}

      {historyOpen ? (
        <section className="mt-5 rounded-2xl border border-slate-200 bg-slate-50 p-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-sm font-semibold text-slate-900">История библиотеки шаблонов</div>
              <div className="mt-1 text-xs text-slate-500">Каждое фактическое изменение сохраняется отдельной восстановимой версией.</div>
            </div>
            <button
              type="button"
              onClick={() => void loadHistory()}
              disabled={historyLoading || restoringVersionId !== null}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-xs font-semibold text-slate-700 disabled:opacity-60"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${historyLoading ? 'animate-spin' : ''}`} />
              Обновить историю
            </button>
          </div>

          {historyVersions.length === 0 && !historyLoading ? (
            <div className="mt-4 rounded-xl border border-dashed border-slate-300 bg-white px-4 py-5 text-sm text-slate-500">
              История появится после первого фактического изменения библиотеки.
            </div>
          ) : (
            <div className="mt-4 grid gap-3 lg:grid-cols-2">
              {historyVersions.map((version) => (
                <article key={version.id} className="rounded-xl border border-slate-200 bg-white p-3">
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <div className="text-sm font-semibold text-slate-900">Версия №{version.revision}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {formatTemplateDate(version.createdAt, timeZoneId)} · {version.createdByUserName || 'Система'}
                      </div>
                    </div>
                    {version.restoredFromVersionId ? (
                      <span className="rounded-full bg-amber-50 px-2 py-1 text-[11px] font-semibold text-amber-700">Восстановление</span>
                    ) : null}
                  </div>
                  <div className="mt-2 line-clamp-2 text-xs text-slate-600">
                    {version.templateNames.join(', ') || 'Без названий'}
                  </div>
                  <div className="mt-3 flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => void previewHistoryVersion(version)}
                      disabled={historyLoading || restoringVersionId !== null}
                      className="rounded-lg border border-slate-300 px-3 py-1.5 text-xs font-semibold text-slate-700 disabled:opacity-60"
                    >
                      Просмотреть
                    </button>
                    <button
                      type="button"
                      onClick={() => void restoreHistoryVersion(version)}
                      disabled={historyLoading || restoringVersionId !== null}
                      className="inline-flex items-center gap-1.5 rounded-lg border border-blue-200 bg-blue-50 px-3 py-1.5 text-xs font-semibold text-blue-700 disabled:opacity-60"
                    >
                      <RotateCcw className={`h-3.5 w-3.5 ${restoringVersionId === version.id ? 'animate-spin' : ''}`} />
                      Восстановить
                    </button>
                  </div>
                </article>
              ))}
            </div>
          )}

          {historyPreview ? (
            <div className="mt-4 rounded-xl border border-blue-200 bg-white p-4">
              <div className="text-sm font-semibold text-slate-900">Содержимое версии №{historyPreviewRevision}</div>
              <div className="mt-3 space-y-2">
                {historyPreview.map((template) => (
                  <details key={template.key} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <summary className="cursor-pointer text-sm font-medium text-slate-800">{template.label} · {template.key}</summary>
                    <pre className="mt-2 max-h-72 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-slate-950 p-3 text-xs leading-5 text-slate-100">{template.content}</pre>
                  </details>
                ))}
              </div>
            </div>
          ) : null}
        </section>
      ) : null}

      <div className="mt-6 grid gap-6 xl:grid-cols-[300px_minmax(0,1fr)]">
        <aside className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-sm font-semibold text-gray-900">Список шаблонов</div>
              <div className="text-xs text-gray-500">{templates.length} в библиотеке</div>
            </div>
            <FileText className="h-5 w-5 text-gray-400" />
          </div>
          <div className="mt-4 space-y-2">
            {templates.map((template) => (
              <button
                key={template.key}
                type="button"
                onClick={() => setSelectedKey(template.key)}
                className={`w-full rounded-2xl border px-4 py-3 text-left transition ${
                  template.key === selectedKey
                    ? 'border-blue-300 bg-blue-50'
                    : 'border-gray-200 bg-white hover:border-gray-300'
                }`}
              >
                <div className="truncate text-sm font-semibold text-gray-900">{template.label}</div>
                <div className="mt-1 truncate text-xs text-gray-500">{template.key}</div>
                <div className="mt-2 flex flex-wrap gap-2 text-xs">
                  <span className="rounded-full bg-slate-100 px-2 py-0.5 text-slate-600">{template.documentType}</span>
                  <span className="rounded-full bg-blue-100 px-2 py-0.5 text-blue-700">v{template.version ?? 1}</span>
                  {template.isActive === false ? (
                    <span className="rounded-full bg-amber-100 px-2 py-0.5 text-amber-700">Выключен</span>
                  ) : null}
                </div>
              </button>
            ))}
          </div>
          <button
            type="button"
            onClick={addTemplate}
            disabled={loading || saving}
            className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
          >
            <Plus className="h-4 w-4" />
            Добавить
          </button>
        </aside>

        {selectedTemplate ? (
          <section className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_320px]">
            <div className="rounded-2xl border border-gray-200 bg-white p-5">
              <div className="mb-4 flex flex-wrap gap-2 text-xs font-semibold">
                <span className="rounded-full border border-blue-200 bg-blue-50 px-2.5 py-1 text-blue-700">Версия {selectedTemplate.version ?? 1}</span>
                <span className="rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-slate-600">
                  Обновлено: {formatTemplateDate(selectedTemplate.updatedAt, timeZoneId)}
                </span>
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Название</span>
                  <input
                    value={selectedTemplate.label}
                    onChange={(event) => updateSelectedTemplate({ label: event.target.value, heading: event.target.value })}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Ключ</span>
                  <input
                    value={selectedTemplate.key}
                    disabled={originalKeys.has(selectedTemplate.key)}
                    onChange={(event) => {
                      const nextKey = event.target.value.trim().toLowerCase().replace(/[^a-z0-9_-]/g, '-');
                      setTemplates((current) => current.map((template) => (
                        template.key === selectedTemplate.key ? { ...template, key: nextKey } : template
                      )));
                      setSelectedKey(nextKey);
                      setPreviewHtml('');
                      setPreviewError(null);
                    }}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:bg-gray-100 disabled:text-gray-500"
                  />
                </label>
                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Тип документа</span>
                  <input
                    value={selectedTemplate.documentType}
                    onChange={(event) => updateSelectedTemplate({ documentType: event.target.value })}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Заголовок</span>
                  <input
                    value={selectedTemplate.heading}
                    onChange={(event) => updateSelectedTemplate({ heading: event.target.value })}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
              </div>

              <label className="mt-4 block">
                <span className="text-sm font-medium text-gray-700">Описание</span>
                <input
                  value={selectedTemplate.description ?? ''}
                  onChange={(event) => updateSelectedTemplate({ description: event.target.value })}
                  className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                />
              </label>

              <label className="mt-4 block">
                <span className="text-sm font-medium text-gray-700">Типы дел</span>
                <input
                  value={formatCaseTypes(selectedTemplate.caseTypes)}
                  onChange={(event) => updateSelectedTemplate({ caseTypes: parseCaseTypes(event.target.value) })}
                  placeholder="Банкротство, Семейное право"
                  className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                />
                <span className="mt-1 block text-xs text-gray-500">
                  Оставьте пустым, чтобы шаблон был доступен для всех типов дел.
                </span>
              </label>

              <label className="mt-4 flex items-center gap-2 text-sm font-medium text-gray-700">
                <SystemCheckbox
                  type="checkbox"
                  checked={selectedTemplate.isActive !== false}
                  onChange={(event) => updateSelectedTemplate({ isActive: event.target.checked })}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600"
                />
                Активен для генерации документов
              </label>

              <label className="mt-5 block">
                <span className="text-sm font-medium text-gray-700">HTML / текст шаблона</span>
                <textarea
                  value={selectedTemplate.content ?? ''}
                  onChange={(event) => updateSelectedTemplate({ content: event.target.value })}
                  spellCheck={false}
                  className="mt-1 min-h-[28rem] w-full rounded-2xl border border-gray-300 bg-slate-950 px-4 py-3 font-mono text-xs leading-6 text-slate-100"
                />
              </label>

              <div className="mt-5 rounded-2xl border border-gray-200 bg-gray-50 p-4">
                <div className="flex flex-col gap-3 lg:flex-row lg:items-end">
                  <label className="block lg:w-44">
                    <span className="text-sm font-medium text-gray-700">ID дела</span>
                    <input
                      type="number"
                      min={1}
                      value={previewCaseId}
                      onChange={(event) => setPreviewCaseId(event.target.value)}
                      className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    />
                  </label>
                  <button
                    type="button"
                    onClick={() => void renderPreview()}
                    disabled={previewLoading || loading || !originalKeys.has(selectedTemplate.key)}
                    className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {previewLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Eye className="h-4 w-4" />}
                    Предпросмотр
                  </button>
                </div>
                {!originalKeys.has(selectedTemplate.key) ? (
                  <div className="mt-2 text-xs text-gray-500">Сначала сохраните новый шаблон.</div>
                ) : null}
                {previewError ? (
                  <div className="mt-3 rounded-xl border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                    {previewError}
                  </div>
                ) : null}
                {previewHtml ? (
                  <div className="mt-4 overflow-hidden rounded-2xl border border-gray-200 bg-white">
                    <iframe
                      title="Предпросмотр шаблона документа дела"
                      srcDoc={previewHtml}
                      sandbox="allow-same-origin"
                      className="h-[42rem] w-full bg-white"
                    />
                  </div>
                ) : null}
              </div>
            </div>

            <aside className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
              <div className="text-sm font-semibold text-gray-900">Переменные шаблона</div>
              <label className="relative mt-3 block">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                <input
                  value={tokenSearch}
                  onChange={(event) => setTokenSearch(event.target.value)}
                  placeholder="Поиск переменной"
                  className="w-full rounded-xl border border-gray-300 bg-white py-2 pl-10 pr-3 text-sm"
                />
              </label>
              <div className="mt-4 space-y-4">
                {tokenGroups.map(([category, items]) => (
                  <div key={category} className="rounded-2xl border border-gray-200 bg-white p-3">
                    <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">{category}</div>
                    <div className="mt-3 space-y-2">
                      {items.map((token) => (
                        <div key={token.key} className="rounded-xl border border-gray-100 bg-gray-50 p-2">
                          <div className="flex items-start justify-between gap-2">
                            <code className="break-all text-xs text-blue-700">{token.placeholder}</code>
                            <div className="flex shrink-0 flex-wrap justify-end gap-1">
                              <button
                                type="button"
                                onClick={() => insertToken(token)}
                                className="inline-flex items-center gap-1 rounded-lg border border-blue-200 bg-blue-50 px-2 py-1 text-[11px] font-medium text-blue-700 hover:bg-blue-100"
                              >
                                Вставить
                              </button>
                              <button
                                type="button"
                                onClick={() => void copyToken(token)}
                                className="inline-flex items-center gap-1 rounded-lg border border-gray-200 bg-white px-2 py-1 text-[11px] font-medium text-gray-600 hover:bg-gray-100"
                              >
                                {copiedToken === token.key ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                                {copiedToken === token.key ? 'Ок' : 'Копия'}
                              </button>
                            </div>
                          </div>
                          <div className="mt-1 text-xs text-gray-600">{token.description}</div>
                        </div>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </aside>
          </section>
        ) : (
          <div className="rounded-2xl border border-dashed border-gray-300 bg-gray-50 p-8 text-sm text-gray-500">
            Шаблоны пока не загружены.
          </div>
        )}
      </div>
    </div>
  );
}
