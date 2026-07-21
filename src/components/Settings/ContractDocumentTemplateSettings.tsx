import { SystemSelect } from '../ui/SystemSelect';
import { RotateCcw, Save } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { apiService, type InvoiceDocumentTemplateDto, type InvoiceDocumentTemplateSettingsDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { useToast } from '../../hooks/useToast';

function createEmptySettings(): InvoiceDocumentTemplateSettingsDto {
  return {
    activeTemplateKey: 'contract-standard',
    printWithSignatureAndSealByDefault: false,
    templates: [],
    tokens: [],
    defaultTemplateHtml: '',
  };
}

function ensureStandardTemplate(settings: InvoiceDocumentTemplateSettingsDto): InvoiceDocumentTemplateSettingsDto {
  if (settings.templates.length > 0) {
    return settings;
  }

  return {
    ...settings,
    activeTemplateKey: 'contract-standard',
    templates: [
      {
        key: 'contract-standard',
        name: 'Стандартный договор',
        description: 'Базовый печатный шаблон договора.',
        html: settings.defaultTemplateHtml,
      },
    ],
  };
}

export function ContractDocumentTemplateSettings() {
  const toast = useToast();
  const [settings, setSettings] = useState<InvoiceDocumentTemplateSettingsDto>(() => createEmptySettings());
  const [selectedKey, setSelectedKey] = useState('contract-standard');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const activeTemplate = useMemo(
    () => settings.templates.find((template) => template.key === selectedKey) ?? settings.templates[0] ?? null,
    [selectedKey, settings.templates],
  );

  const updateActiveTemplate = (patch: Partial<InvoiceDocumentTemplateDto>) => {
    if (!activeTemplate) return;

    setSettings((prev) => ({
      ...prev,
      templates: prev.templates.map((template) =>
        template.key === activeTemplate.key ? { ...template, ...patch } : template,
      ),
    }));
  };

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const response = ensureStandardTemplate(await apiService.getContractDocumentTemplateSettings());
      setSettings(response);
      setSelectedKey(response.activeTemplateKey || response.templates[0]?.key || 'contract-standard');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить шаблон договора'));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const response = ensureStandardTemplate(await apiService.updateContractDocumentTemplateSettings({
        activeTemplateKey: selectedKey,
        printWithSignatureAndSealByDefault: false,
        templates: settings.templates,
      }));
      setSettings(response);
      setSelectedKey(response.activeTemplateKey || response.templates[0]?.key || selectedKey);
      toast.success('Печатный шаблон договора сохранён.', 'Договоры');
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить шаблон договора');
      setError(message);
      toast.error(message, 'Договоры');
    } finally {
      setSaving(false);
    }
  };

  const resetActiveTemplate = () => {
    if (!activeTemplate || !settings.defaultTemplateHtml) return;

    updateActiveTemplate({
      html: settings.defaultTemplateHtml,
      name: activeTemplate.name || 'Стандартный договор',
      description: activeTemplate.description || 'Базовый печатный шаблон договора.',
    });
  };

  return (
    <div className="mt-6 rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div>
          <h5 className="text-base font-semibold text-gray-900">Печатный шаблон договора</h5>
          <p className="mt-1 text-sm text-gray-600">
            Активный HTML-шаблон используется для печати, скачивания Word/PDF и отправки договора по email.
          </p>
        </div>
        <button
          type="button"
          onClick={save}
          disabled={loading || saving || !activeTemplate}
          className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <Save className="h-4 w-4" />
          <span>{saving ? 'Сохранение...' : 'Сохранить шаблон'}</span>
        </button>
      </div>

      {error && (
        <div className="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {loading ? (
        <div className="rounded-xl border border-gray-200 px-4 py-6 text-sm text-gray-500">Загрузка шаблона...</div>
      ) : activeTemplate ? (
        <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_280px]">
          <div className="space-y-4">
            <div className="grid gap-4 md:grid-cols-2">
              <label className="block">
                <span className="text-sm font-medium text-gray-700">Активный шаблон</span>
                <SystemSelect
                  value={selectedKey}
                  onChange={(event) => {
                    const nextKey = event.target.value;
                    setSelectedKey(nextKey);
                    setSettings((prev) => ({ ...prev, activeTemplateKey: nextKey }));
                  }}
                  className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                >
                  {settings.templates.map((template) => (
                    <option key={template.key} value={template.key}>
                      {template.name}
                    </option>
                  ))}
                </SystemSelect>
              </label>

              <label className="block">
                <span className="text-sm font-medium text-gray-700">Название</span>
                <input
                  value={activeTemplate.name}
                  onChange={(event) => updateActiveTemplate({ name: event.target.value })}
                  className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                />
              </label>
            </div>

            <label className="block">
              <span className="text-sm font-medium text-gray-700">Описание</span>
              <input
                value={activeTemplate.description ?? ''}
                onChange={(event) => updateActiveTemplate({ description: event.target.value })}
                className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
              />
            </label>

            <label className="block">
              <span className="text-sm font-medium text-gray-700">HTML шаблона</span>
              <textarea
                value={activeTemplate.html}
                onChange={(event) => updateActiveTemplate({ html: event.target.value })}
                spellCheck={false}
                className="mt-1 min-h-[360px] w-full rounded-lg border border-gray-300 px-3 py-2 font-mono text-xs leading-5"
              />
            </label>

            <button
              type="button"
              onClick={resetActiveTemplate}
              disabled={!settings.defaultTemplateHtml}
              className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <RotateCcw className="h-4 w-4" />
              Вернуть стандартный HTML
            </button>
          </div>

          <div className="rounded-xl border border-gray-200 bg-gray-50 p-4">
            <div className="text-sm font-semibold text-gray-900">Токены договора</div>
            <div className="mt-3 max-h-[520px] space-y-3 overflow-auto pr-1">
              {settings.tokens.map((token) => (
                <div key={token.key} className="rounded-lg border border-gray-200 bg-white p-3">
                  <code className="text-xs font-semibold text-blue-700">{`{{${token.key}}}`}</code>
                  <div className="mt-1 text-xs text-gray-600">{token.description}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
      ) : (
        <div className="rounded-xl border border-gray-200 px-4 py-6 text-sm text-gray-500">Шаблоны договора не найдены.</div>
      )}
    </div>
  );
}
