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

const PREVIEW_HTML_TOKEN_KEYS = new Set(['seller.logoHtml', 'seller.sealHtml', 'seller.signatureHtml']);

const PREVIEW_TOKENS: Record<string, string> = {
  'document.title': 'Счет № 25 от 02.04.2026',
  'invoice.number': '25',
  'invoice.date': '02.04.2026',
  'invoice.dateLong': '2 апреля 2026 г.',
  'invoice.dueDate': '12.04.2026',
  'invoice.amount': '11 000,00',
  'invoice.amountWords': 'Одиннадцать тысяч рублей 00 копеек',
  'invoice.description': 'Оплата юридических услуг',
  'invoice.total': '11 000,00',
  'invoice.totalWords': 'Одиннадцать тысяч рублей 00 копеек',
  'invoice.vatText': 'Без налога (НДС): —',
  'seller.shortName': 'ИП Кожеваткин А.В.',
  'seller.fullName': 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
  'seller.inn': '633009620345',
  'seller.kpp': '—',
  'seller.address': '446200, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9',
  'seller.bankName': 'ООО "Банк Точка"',
  'seller.bik': '044525104',
  'seller.accountNumber': '40802810414500006073',
  'seller.corrAccountNumber': '30101810745374525104',
  'seller.requisites': 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович, ИНН 633009620345, 446200, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9, р/с 40802810414500006073, в банке ООО "Банк Точка", БИК 044525104',
  'seller.documentDirectorPosition': 'Индивидуальный предприниматель',
  'seller.documentDirectorFullName': 'Кожеваткин Андрей Владимирович',
  'seller.logoHtml': '<div style="font-size:28px;font-weight:700;letter-spacing:0.08em;color:#111827;text-align:right;">ЭЛИТПРАВО</div>',
  'seller.sealHtml': '<div style="width:132px;height:132px;border:2px dashed #94a3b8;border-radius:9999px;margin-left:auto;margin-top:16px;display:flex;align-items:center;justify-content:center;color:#64748b;font-size:12px;">Печать</div>',
  'seller.signatureHtml': '<div style="font-style:italic;color:#334155;font-size:18px;">А. В. Кожеваткин</div>',
  'buyer.name': 'Губанова Оксана Ивановна',
  'buyer.address': 'Самарская область, р-н Безенчукский, пгт Безенчук, ул. Тимирязева, д. 17',
  'buyer.passport': 'Паспорт серия 36 17 № 441597',
  'buyer.requisites': 'Губанова Оксана Ивановна, Самарская область, р-н Безенчукский, пгт Безенчук, ул. Тимирязева, д. 17, Паспорт серия 36 17 № 441597',
};

const PREVIEW_LINE_ITEMS = [
  {
    index: '1',
    name: 'Подготовка частной жалобы на определение суда и искового заявления по делу № 2-1158/2024',
    quantity: '1',
    unit: 'усл. ед.',
    price: '10 000,00',
    total: '10 000,00',
  },
  {
    index: '2',
    name: 'Услуга по направлению документов в суд через ГАС ПРАВОСУДИЕ (1 направление)',
    quantity: '1',
    unit: 'усл. ед.',
    price: '1 000,00',
    total: '1 000,00',
  },
] as const;

function escapeHtml(value: string) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function renderTemplatePreview(templateHtml: string) {
  const withLineItems = templateHtml.replace(/\{\{#lineItems\}\}([\s\S]*?)\{\{\/lineItems\}\}/g, (_, rowTemplate: string) => (
    PREVIEW_LINE_ITEMS.map((item) => rowTemplate.replace(/\{\{\s*([a-zA-Z0-9._]+)\s*\}\}/g, (match: string, key: string) => (
      Object.prototype.hasOwnProperty.call(item, key)
        ? escapeHtml(String(item[key as keyof typeof item] ?? ''))
        : match
    ))).join('')
  ));

  return withLineItems.replace(/\{\{\s*([a-zA-Z0-9._]+)\s*\}\}/g, (match, key: string) => {
    const raw = PREVIEW_TOKENS[key];
    if (raw === undefined) return '';
    return PREVIEW_HTML_TOKEN_KEYS.has(key) ? raw : escapeHtml(raw);
  });
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
  const previewHtml = useMemo(
    () => (selectedTemplate ? renderTemplatePreview(selectedTemplate.html) : ''),
    [selectedTemplate],
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

  const openPreviewInNewWindow = () => {
    if (!previewHtml || typeof window === 'undefined') return;
    const previewUrl = URL.createObjectURL(new Blob([previewHtml], { type: 'text/html;charset=utf-8' }));
    const previewWindow = window.open(previewUrl, '_blank', 'noopener,noreferrer,width=1180,height=900');
    if (!previewWindow) {
      URL.revokeObjectURL(previewUrl);
      toast.error('Браузер заблокировал окно предпросмотра. Разрешите всплывающие окна и повторите.');
      return;
    }
    window.setTimeout(() => URL.revokeObjectURL(previewUrl), 10_000);
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
          <p className="mt-2 max-w-3xl text-xs text-gray-500">
            Сейчас поддерживается HTML-шаблон с CSS. Загрузка Word/DOCX-шаблона пока не реализована, поэтому ниже есть
            живое превью итогового документа.
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
                      <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-gray-500">
                        <span className="truncate">{item.key}</span>
                        <span className="rounded-full bg-slate-100 px-2 py-0.5 font-medium text-slate-600">HTML</span>
                      </div>
                      {item.description ? (
                        <div className="mt-2 line-clamp-2 text-xs leading-5 text-gray-500">{item.description}</div>
                      ) : null}
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

              <div className="mt-5 grid gap-6 2xl:grid-cols-[minmax(0,1.05fr)_minmax(360px,0.95fr)]">
                <div className="space-y-4">
                  <div className="grid gap-4 lg:grid-cols-2">
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

                  <label className="block">
                    <span className="text-sm font-medium text-gray-700">Описание</span>
                    <input
                      type="text"
                      value={selectedTemplate.description ?? ''}
                      onChange={(event) => updateSelectedTemplate({ description: event.target.value })}
                      className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    />
                  </label>

                  <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
                    Формат шаблона сейчас один: HTML + CSS. Если понадобится загрузка готовых Word/DOCX-файлов, это
                    можно будет добавить следующим этапом.
                  </div>

                  <label className="block">
                    <span className="text-sm font-medium text-gray-700">HTML шаблона</span>
                    <textarea
                      value={selectedTemplate.html}
                      onChange={(event) => updateSelectedTemplate({ html: event.target.value })}
                      spellCheck={false}
                      className="mt-1 min-h-[36rem] w-full rounded-2xl border border-gray-300 bg-slate-950 px-4 py-3 font-mono text-xs leading-6 text-slate-100 shadow-inner"
                    />
                  </label>
                </div>

                <div className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                    <div>
                      <div className="text-base font-semibold text-gray-900">Живое превью</div>
                      <div className="mt-1 text-sm text-gray-600">
                        Ниже показан результат шаблона на демо-данных, близких к твоему присланному счету.
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={openPreviewInNewWindow}
                      disabled={!previewHtml}
                      className="inline-flex items-center justify-center rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      Открыть отдельно
                    </button>
                  </div>

                  <div className="mt-4 rounded-2xl border border-gray-200 bg-white p-3 text-xs leading-5 text-gray-500">
                    Превью использует именно печатный HTML-шаблон. Раньше на карточке счета отображался отдельный старый
                    макет, поэтому стандартный шаблон визуально не был похож на документ, который ты присылал.
                  </div>

                  <div className="mt-4 overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-inner">
                    {previewHtml ? (
                      <iframe
                        title={`Превью шаблона ${selectedTemplate.name}`}
                        srcDoc={previewHtml}
                        sandbox="allow-same-origin"
                        className="h-[58rem] w-full bg-white"
                      />
                    ) : (
                      <div className="flex h-[22rem] items-center justify-center px-6 text-center text-sm text-gray-500">
                        Введите HTML шаблона, чтобы увидеть итоговый документ.
                      </div>
                    )}
                  </div>
                </div>
              </div>
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
