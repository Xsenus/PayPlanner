import { useEffect, useMemo, useState } from 'react';
import { Check, Copy, FileText, Plus, RefreshCw, RotateCcw, Save, Trash2 } from 'lucide-react';
import { apiService } from '../../services/api';
import type { InvoiceDocumentTemplateDto, InvoiceDocumentTemplateSettingsDto } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

function slugifyTemplateKey(value: string) {
  const normalized = value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');

  return normalized || 'invoice-template';
}

function ensureUniqueTemplateKey(
  templates: InvoiceDocumentTemplateDto[],
  seed: string,
  excludeKey?: string,
) {
  const base = slugifyTemplateKey(seed);
  const existing = new Set(
    templates
      .filter((item) => item.key !== excludeKey)
      .map((item) => item.key.toLowerCase()),
  );

  if (!existing.has(base)) {
    return base;
  }

  let index = 2;
  while (existing.has(`${base}-${index}`)) {
    index += 1;
  }

  return `${base}-${index}`;
}

export function InvoiceDocumentTemplateSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
  const [settings, setSettings] = useState<InvoiceDocumentTemplateSettingsDto | null>(null);
  const [selectedTemplateKey, setSelectedTemplateKey] = useState('');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadSettings = async (showSuccessToast = false) => {
    setLoading(true);
    setError(null);
    setMessage(null);
    try {
      const response = await apiService.getInvoiceDocumentTemplateSettings();
      setSettings(response);
      setSelectedTemplateKey((prev) => {
        if (prev && response.templates.some((item) => item.key === prev)) {
          return prev;
        }
        return response.activeTemplateKey || response.templates[0]?.key || '';
      });
      if (showSuccessToast) {
        toast.success('Шаблоны документов обновлены', 'Настройки');
      }
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось загрузить шаблоны печати счетов');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadSettings();
  }, []);

  const selectedTemplate = useMemo(
    () => settings?.templates.find((item) => item.key === selectedTemplateKey) ?? null,
    [selectedTemplateKey, settings],
  );

  const tokenGroups = useMemo(() => {
    const groups = new Map<string, Array<{ key: string; description: string; isRepeating: boolean }>>();
    (settings?.tokens ?? []).forEach((token) => {
      const bucket = groups.get(token.category) ?? [];
      bucket.push({ key: token.key, description: token.description, isRepeating: token.isRepeating });
      groups.set(token.category, bucket);
    });
    return Array.from(groups.entries());
  }, [settings]);

  const updateSelectedTemplate = (patch: Partial<InvoiceDocumentTemplateDto>) => {
    setSettings((prev) => {
      if (!prev) return prev;
      return {
        ...prev,
        templates: prev.templates.map((item) => (
          item.key === selectedTemplateKey
            ? { ...item, ...patch }
            : item
        )),
      };
    });
  };

  const addTemplate = () => {
    setSettings((prev) => {
      if (!prev) return prev;
      const name = `Шаблон ${prev.templates.length + 1}`;
      const template: InvoiceDocumentTemplateDto = {
        key: ensureUniqueTemplateKey(prev.templates, name),
        name,
        description: 'Новый шаблон печати счета.',
        html: prev.defaultTemplateHtml || selectedTemplate?.html || '',
      };
      setSelectedTemplateKey(template.key);
      return {
        ...prev,
        templates: [...prev.templates, template],
      };
    });
    setMessage(null);
    setError(null);
  };

  const duplicateSelectedTemplate = () => {
    if (!settings || !selectedTemplate) return;
    const duplicate: InvoiceDocumentTemplateDto = {
      ...selectedTemplate,
      key: ensureUniqueTemplateKey(settings.templates, `${selectedTemplate.key}-copy`),
      name: `${selectedTemplate.name} (копия)`,
    };
    setSettings({
      ...settings,
      templates: [...settings.templates, duplicate],
    });
    setSelectedTemplateKey(duplicate.key);
    setMessage(null);
    setError(null);
  };

  const removeSelectedTemplate = async () => {
    if (!settings || !selectedTemplate) return;
    if (settings.templates.length <= 1) {
      const nextError = 'Нельзя удалить единственный шаблон документа.';
      setError(nextError);
      toast.error(nextError, 'Настройки');
      return;
    }

    const approved = await confirm({
      title: 'Удалить шаблон печати',
      description: `Удалить шаблон «${selectedTemplate.name}»?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    const remaining = settings.templates.filter((item) => item.key !== selectedTemplate.key);
    const nextSelected = remaining[0];
    setSettings({
      ...settings,
      activeTemplateKey: settings.activeTemplateKey === selectedTemplate.key
        ? nextSelected.key
        : settings.activeTemplateKey,
      templates: remaining,
    });
    setSelectedTemplateKey(nextSelected.key);
    setMessage(null);
    setError(null);
  };

  const activateSelectedTemplate = () => {
    if (!settings || !selectedTemplate) return;
    setSettings({
      ...settings,
      activeTemplateKey: selectedTemplate.key,
    });
    setMessage(null);
    setError(null);
  };

  const resetSelectedTemplateHtml = async () => {
    if (!settings || !selectedTemplate) return;
    const approved = await confirm({
      title: 'Сбросить HTML шаблона',
      description: 'Заменить HTML выбранного шаблона на рекомендованный базовый шаблон?',
      confirmLabel: 'Сбросить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;
    updateSelectedTemplate({ html: settings.defaultTemplateHtml });
  };

  const saveTemplates = async () => {
    if (!settings) return;

    const approved = await confirm({
      title: 'Сохранить шаблоны печати',
      description: 'Сохранить текущие настройки печатных шаблонов счетов?',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const response = await apiService.updateInvoiceDocumentTemplateSettings({
        activeTemplateKey: settings.activeTemplateKey,
        templates: settings.templates,
      });
      setSettings(response);
      setSelectedTemplateKey((prev) => (
        prev && response.templates.some((item) => item.key === prev)
          ? prev
          : response.activeTemplateKey || response.templates[0]?.key || ''
      ));
      const nextMessage = 'Шаблоны печати счетов сохранены';
      setMessage(nextMessage);
      toast.success(nextMessage, 'Настройки');
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось сохранить шаблоны печати счетов');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div>
          <h5 className="text-base font-semibold text-gray-900">Печатные шаблоны счетов</h5>
          <p className="mt-1 max-w-3xl text-sm text-gray-600">
            Активный шаблон используется для печати, скачивания и отправки счета по email. Шаблон можно править как HTML,
            а переменные подставляются автоматически из данных счета, клиента и реквизитов компании.
          </p>
          <p className="mt-2 text-xs text-gray-500">
            Для строк с позициями используйте блок <code>{'{{#lineItems}} ... {{/lineItems}}'}</code>.
          </p>
        </div>

        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void loadSettings(true)}
            disabled={loading || saving}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <RefreshCw className="h-4 w-4" />
            <span>{loading ? 'Обновляем…' : 'Обновить'}</span>
          </button>
          <button
            type="button"
            onClick={() => void saveTemplates()}
            disabled={loading || saving || !settings}
            className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <Save className="h-4 w-4" />
            <span>{saving ? 'Сохраняем…' : 'Сохранить'}</span>
          </button>
        </div>
      </div>

      {(message || error) && (
        <div className={`mt-5 rounded-2xl border px-4 py-3 text-sm ${error ? 'border-red-200 bg-red-50 text-red-700' : 'border-emerald-200 bg-emerald-50 text-emerald-700'}`}>
          {error ?? message}
        </div>
      )}

      <div className="mt-6 grid gap-6 xl:grid-cols-[320px,minmax(0,1fr)]">
        <div className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-sm font-semibold text-gray-900">Список шаблонов</div>
              <div className="text-xs text-gray-500">Можно держать несколько вариантов и переключать активный.</div>
            </div>
            <FileText className="h-5 w-5 text-gray-400" />
          </div>

          <div className="mt-4 space-y-2">
            {(settings?.templates ?? []).map((item) => {
              const isSelected = item.key === selectedTemplateKey;
              const isActive = item.key === settings?.activeTemplateKey;
              return (
                <button
                  key={item.key}
                  type="button"
                  onClick={() => setSelectedTemplateKey(item.key)}
                  className={`w-full rounded-2xl border px-4 py-3 text-left transition ${
                    isSelected
                      ? 'border-blue-300 bg-blue-50 shadow-sm'
                      : 'border-gray-200 bg-white hover:border-gray-300 hover:bg-gray-50'
                  }`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="truncate text-sm font-semibold text-gray-900">{item.name}</div>
                      <div className="mt-1 truncate text-xs text-gray-500">{item.key}</div>
                    </div>
                    {isActive && (
                      <span className="rounded-full bg-emerald-100 px-2 py-1 text-[11px] font-semibold text-emerald-700">
                        Активный
                      </span>
                    )}
                  </div>
                </button>
              );
            })}
          </div>

          <div className="mt-4 grid gap-2">
            <button
              type="button"
              onClick={addTemplate}
              disabled={loading || saving || !settings}
              className="inline-flex items-center justify-center gap-2 rounded-xl border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <Plus className="h-4 w-4" />
              <span>Добавить</span>
            </button>
            <button
              type="button"
              onClick={duplicateSelectedTemplate}
              disabled={loading || saving || !selectedTemplate}
              className="inline-flex items-center justify-center gap-2 rounded-xl border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <Copy className="h-4 w-4" />
              <span>Дублировать</span>
            </button>
            <button
              type="button"
              onClick={removeSelectedTemplate}
              disabled={loading || saving || !selectedTemplate}
              className="inline-flex items-center justify-center gap-2 rounded-xl border border-red-200 bg-white px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <Trash2 className="h-4 w-4" />
              <span>Удалить</span>
            </button>
          </div>
        </div>

        <div className="space-y-6">
          {selectedTemplate ? (
            <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
              <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                <div>
                  <div className="text-base font-semibold text-gray-900">Редактор шаблона</div>
                  <div className="mt-1 text-sm text-gray-600">
                    Здесь можно менять верстку документа и подставляемые переменные без правки кода.
                  </div>
                </div>

                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={activateSelectedTemplate}
                    disabled={settings?.activeTemplateKey === selectedTemplate.key}
                    className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    <Check className="h-4 w-4" />
                    <span>Сделать активным</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void resetSelectedTemplateHtml()}
                    disabled={loading || saving}
                    className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    <RotateCcw className="h-4 w-4" />
                    <span>Сбросить HTML</span>
                  </button>
                </div>
              </div>

              <div className="mt-5 grid gap-4 lg:grid-cols-2">
                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Название шаблона</span>
                  <input
                    type="text"
                    value={selectedTemplate.name}
                    onChange={(event) => {
                      const nextName = event.target.value;
                      updateSelectedTemplate({ name: nextName });
                    }}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>

                <label className="block">
                  <span className="text-sm font-medium text-gray-700">Внутренний ключ</span>
                  <input
                    type="text"
                    value={selectedTemplate.key}
                    onChange={(event) => {
                      const nextKey = ensureUniqueTemplateKey(settings?.templates ?? [], event.target.value, selectedTemplate.key);
                      setSettings((prev) => {
                        if (!prev) return prev;
                        return {
                          ...prev,
                          activeTemplateKey: prev.activeTemplateKey === selectedTemplate.key ? nextKey : prev.activeTemplateKey,
                          templates: prev.templates.map((item) => (
                            item.key === selectedTemplate.key
                              ? { ...item, key: nextKey }
                              : item
                          )),
                        };
                      });
                      setSelectedTemplateKey(nextKey);
                    }}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
              </div>

              <label className="mt-4 block">
                <span className="text-sm font-medium text-gray-700">Описание</span>
                <input
                  type="text"
                  value={selectedTemplate.description ?? ''}
                  onChange={(event) => updateSelectedTemplate({ description: event.target.value })}
                  className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                />
              </label>

              <label className="mt-4 block">
                <span className="text-sm font-medium text-gray-700">HTML шаблона</span>
                <textarea
                  value={selectedTemplate.html}
                  onChange={(event) => updateSelectedTemplate({ html: event.target.value })}
                  spellCheck={false}
                  className="mt-1 min-h-[32rem] w-full rounded-2xl border border-gray-300 bg-slate-950 px-4 py-3 font-mono text-xs leading-6 text-slate-100 shadow-inner"
                />
              </label>
            </div>
          ) : (
            <div className="rounded-2xl border border-dashed border-gray-300 bg-gray-50 p-8 text-sm text-gray-500">
              Выберите шаблон слева или создайте новый.
            </div>
          )}

          <div className="rounded-2xl border border-gray-200 bg-gray-50 p-5">
            <div className="text-base font-semibold text-gray-900">Доступные переменные</div>
            <div className="mt-1 text-sm text-gray-600">
              Переменные подставляются автоматически. Повторяемые поля работают только внутри блока <code>{'{{#lineItems}} ... {{/lineItems}}'}</code>.
            </div>

            <div className="mt-4 grid gap-4 xl:grid-cols-2">
              {tokenGroups.map(([category, items]) => (
                <div key={category} className="rounded-2xl border border-gray-200 bg-white p-4">
                  <div className="text-sm font-semibold text-gray-900">{category}</div>
                  <div className="mt-3 space-y-3">
                    {items.map((token) => (
                      <div key={token.key} className="rounded-xl border border-gray-100 bg-gray-50 px-3 py-2">
                        <div className="font-mono text-xs text-blue-700">
                          {token.key === 'lineItems' ? '{{#lineItems}} ... {{/lineItems}}' : `{{${token.key}}}`}
                        </div>
                        <div className="mt-1 text-xs text-gray-600">{token.description}</div>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
