import { useCallback, useEffect, useMemo, useState } from 'react';
import { Check, Copy, FileSearch, FileText, Plus, RefreshCw, RotateCcw, Save, Search, Trash2 } from 'lucide-react';
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

const PREVIEW_HTML_TOKEN_KEYS = new Set([
  'seller.logoHtml',
  'seller.sealHtml',
  'seller.signatureHtml',
  'invoice.qrCodeHtml',
  'buyer.requisitesHtml',
  'buyer.foundationRowHtml',
]);

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
  'invoice.itemsCount': '2',
  'invoice.vatText': 'Без налога (НДС): —',
  'invoice.qrCodeHtml': '<img src="https://api.qrserver.com/v1/create-qr-code/?size=150x150&data=demo" alt="QR-код" style="display:block;width:150px;height:150px;margin-left:auto;object-fit:contain;" />',
  'seller.shortName': 'ИП Кожеваткин А.В.',
  'seller.fullName': 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
  'seller.inn': '633009620345',
  'seller.kpp': '—',
  'seller.kppOrDash': '—',
  'seller.address': '446200, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9',
  'seller.bankName': 'ООО "Банк Точка"',
  'seller.bik': '044525104',
  'seller.accountNumber': '40802810414500006073',
  'seller.corrAccountNumber': '30101810745374525104',
  'seller.requisites': 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович, ИНН 633009620345, 446200, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9, р/с 40802810414500006073, в банке ООО "Банк Точка", БИК 044525104',
  'seller.documentDirectorPosition': 'Индивидуальный предприниматель',
  'seller.documentDirectorFullName': 'Кожеваткин Андрей Владимирович',
  'seller.documentDirectorShortName': 'Кожеваткин А. В.',
  'seller.logoHtml': '<div style="font-size:28px;font-weight:700;letter-spacing:0.08em;color:#111827;text-align:right;">ЭЛИТПРАВО</div>',
  'seller.sealHtml': '<div style="width:132px;height:132px;border:2px dashed #94a3b8;border-radius:9999px;margin-left:auto;margin-top:16px;display:flex;align-items:center;justify-content:center;color:#64748b;font-size:12px;">Печать</div>',
  'seller.signatureHtml': '<div style="font-style:italic;color:#334155;font-size:18px;">А. В. Кожеваткин</div>',
  'buyer.name': 'Губанова Оксана Ивановна',
  'buyer.address': 'Самарская область, р-н Безенчукский, пгт Безенчук, ул. Тимирязева, д. 17',
  'buyer.passport': 'Паспорт 36 17 № 441597',
  'buyer.inn': '633123456789',
  'buyer.snils': '123-456-789 00',
  'buyer.contract': 'Договор: Договор № 1-03/04-26 от 03.04.2026 БАНКРОТСТВО',
  'buyer.foundation': 'Договор № 1-03/04-26 от 03.04.2026 БАНКРОТСТВО',
  'buyer.requisites': 'Губанова Оксана Ивановна, ИНН 633123456789, СНИЛС 123-456-789 00, Паспорт 36 17 № 441597, Самарская область, р-н Безенчукский, пгт Безенчук, ул. Тимирязева, д. 17',
  'buyer.requisitesHtml': '<span class="counterparty-line">Губанова Оксана Ивановна, ИНН 633123456789, СНИЛС 123-456-789 00</span><span class="counterparty-line">Паспорт 36 17 № 441597</span><span class="counterparty-line">Самарская область, р-н Безенчукский, пгт Безенчук, ул. Тимирязева, д. 17</span>',
  'buyer.foundationRowHtml': '<tr><td class="counterparty-label pad-t-5">Основание:</td><td class="counterparty-value pad-t-5">Договор № 1-03/04-26 от 03.04.2026 БАНКРОТСТВО</td></tr>',
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

function renderTemplatePreview(templateHtml: string, includeSignatureAndSeal: boolean) {
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
    if (!includeSignatureAndSeal && (key === 'seller.sealHtml' || key === 'seller.signatureHtml')) {
      return '';
    }
    return PREVIEW_HTML_TOKEN_KEYS.has(key) ? raw : escapeHtml(raw);
  });
}

type TemplateValidationResult = {
  errors: string[];
  warnings: string[];
};

function validateTemplateHtml(
  templateHtml: string,
  settings: InvoiceDocumentTemplateSettingsDto | null,
): TemplateValidationResult {
  const errors: string[] = [];
  const warnings: string[] = [];
  const source = templateHtml.trim();

  if (!source) {
    errors.push('HTML шаблона пуст.');
    return { errors, warnings };
  }

  const loopOpenCount = (source.match(/\{\{#lineItems\}\}/g) ?? []).length;
  const loopCloseCount = (source.match(/\{\{\/lineItems\}\}/g) ?? []).length;
  if (loopOpenCount !== loopCloseCount) {
    errors.push('Блок {{#lineItems}} ... {{/lineItems}} закрыт не полностью.');
  }

  const mustacheOpenCount = (source.match(/\{\{/g) ?? []).length;
  const mustacheCloseCount = (source.match(/\}\}/g) ?? []).length;
  if (mustacheOpenCount !== mustacheCloseCount) {
    errors.push('В шаблоне есть незакрытые конструкции {{...}}.');
  }

  if (!/<html[\s>]/i.test(source)) {
    warnings.push('В шаблоне нет тега <html>. Документ все равно может работать, но лучше держать полный HTML.');
  }
  if (!/<body[\s>]/i.test(source)) {
    warnings.push('В шаблоне нет тега <body>. Для печатного документа это нежелательно.');
  }
  if (!/<title[\s>]/i.test(source)) {
    warnings.push('В шаблоне нет тега <title>. Файл будет открываться менее понятно.');
  }
  if (!/\{\{invoice\.number\}\}/.test(source)) {
    warnings.push('В шаблоне не используется {{invoice.number}}. Пользователь может не увидеть номер счета.');
  }
  if (!/\{\{invoice\.date(Long)?\}\}/.test(source)) {
    warnings.push('В шаблоне не используется дата счета.');
  }

  const lineItemsBlocks = Array.from(source.matchAll(/\{\{#lineItems\}\}([\s\S]*?)\{\{\/lineItems\}\}/g));
  if (lineItemsBlocks.length > 0 && !lineItemsBlocks.some(([, block]) => /\{\{\s*total\s*\}\}/.test(block))) {
    warnings.push('В блоке {{#lineItems}} не используется {{total}}. Если нужна колонка суммы по позиции, добавьте этот токен в строку таблицы.');
  }

  const allowedTokens = new Set<string>((settings?.tokens ?? []).map((token) => token.key));
  allowedTokens.add('#lineItems');
  allowedTokens.add('/lineItems');

  const discoveredTokens = Array.from(source.matchAll(/\{\{\s*([a-zA-Z0-9._#/]+)\s*\}\}/g))
    .map((match) => match[1]?.trim())
    .filter((token): token is string => Boolean(token));

  const unknownTokens = Array.from(new Set(discoveredTokens.filter((token) => !allowedTokens.has(token))));
  if (unknownTokens.length > 0) {
    warnings.push(`Обнаружены неподдерживаемые токены: ${unknownTokens.join(', ')}.`);
  }

  const parser = new DOMParser();
  const parsed = parser.parseFromString(source, 'text/html');
  if (!parsed.body) {
    errors.push('Браузер не смог построить DOM для шаблона.');
  }

  return { errors, warnings };
}

export function InvoiceDocumentTemplateSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
  const [settings, setSettings] = useState<InvoiceDocumentTemplateSettingsDto | null>(null);
  const [selectedTemplateKey, setSelectedTemplateKey] = useState('');
  const [workspaceTab, setWorkspaceTab] = useState<'preview' | 'editor' | 'tokens'>('preview');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [tokenSearch, setTokenSearch] = useState('');
  const [copiedToken, setCopiedToken] = useState<string | null>(null);
  const [validationResult, setValidationResult] = useState<TemplateValidationResult | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadSettings = useCallback(async (showSuccessToast = false) => {
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
  }, [toast]);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  const selectedTemplate = useMemo(
    () => settings?.templates.find((item) => item.key === selectedTemplateKey) ?? null,
    [selectedTemplateKey, settings],
  );
  const previewHtml = useMemo(
    () => (selectedTemplate && settings ? renderTemplatePreview(selectedTemplate.html, settings.printWithSignatureAndSealByDefault) : ''),
    [selectedTemplate, settings],
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
  const filteredTokenGroups = useMemo(() => {
    const term = tokenSearch.trim().toLowerCase();
    if (!term) {
      return tokenGroups;
    }

    return tokenGroups
      .map(([category, items]) => [
        category,
        items.filter((token) =>
          category.toLowerCase().includes(term)
          || token.key.toLowerCase().includes(term)
          || token.description.toLowerCase().includes(term)),
      ] as const)
      .filter(([, items]) => items.length > 0);
  }, [tokenGroups, tokenSearch]);

  const updateSelectedTemplate = (patch: Partial<InvoiceDocumentTemplateDto>) => {
    if (patch.html !== undefined) {
      setValidationResult(null);
    }
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

  useEffect(() => {
    setValidationResult(null);
  }, [selectedTemplateKey]);

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
      setWorkspaceTab('editor');
      return {
        ...prev,
        templates: [...prev.templates, template],
      };
    });
    setMessage(null);
    setError(null);
    setValidationResult(null);
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
    setWorkspaceTab('editor');
    setMessage(null);
    setError(null);
    setValidationResult(null);
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
    setValidationResult(null);
  };

  const activateSelectedTemplate = () => {
    if (!settings || !selectedTemplate) return;
    setSettings({
      ...settings,
      activeTemplateKey: selectedTemplate.key,
    });
    setMessage(null);
    setError(null);
    setValidationResult(null);
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
    setValidationResult(null);
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
        printWithSignatureAndSealByDefault: settings.printWithSignatureAndSealByDefault,
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
    const previewWindow = window.open('', '_blank', 'width=1180,height=900');
    if (!previewWindow) {
      toast.error('Браузер заблокировал окно предпросмотра. Разрешите всплывающие окна и повторите.');
      return;
    }
    previewWindow.document.open();
    previewWindow.document.write(previewHtml);
    previewWindow.document.close();
    previewWindow.focus();
  };

  const validateSelectedTemplate = () => {
    if (!selectedTemplate) return;
    const result = validateTemplateHtml(selectedTemplate.html, settings);
    setValidationResult(result);
    if (result.errors.length > 0) {
      toast.error(`Найдено ошибок: ${result.errors.length}.`, 'Проверка шаблона');
      return;
    }

    if (result.warnings.length > 0) {
      toast.success(`Критичных ошибок нет, но есть предупреждения: ${result.warnings.length}.`, 'Проверка шаблона');
      return;
    }

    toast.success('Шаблон прошел базовую проверку без замечаний.', 'Проверка шаблона');
  };

  const copyTokenToClipboard = async (tokenKey: string) => {
    const value = tokenKey === 'lineItems'
      ? '{{#lineItems}}\n  ...\n{{/lineItems}}'
      : `{{${tokenKey}}}`;
    try {
      await navigator.clipboard.writeText(value);
      setCopiedToken(tokenKey);
      window.setTimeout(() => setCopiedToken((prev) => (prev === tokenKey ? null : prev)), 1500);
      toast.success('Токен скопирован в буфер обмена.', 'Шаблоны');
    } catch {
      toast.error('Не удалось скопировать токен в буфер обмена.', 'Шаблоны');
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

      {settings ? (
        <div className="mt-5 rounded-2xl border border-slate-200 bg-slate-50 p-4">
          <div className="text-sm font-semibold text-slate-900">Режим печати по умолчанию</div>
          <p className="mt-1 text-sm text-slate-600">
            Кнопка «Печать» и встроенное превью используют этот режим. Для скачивания пользователь сможет выбрать вариант отдельно.
          </p>
          <div className="mt-3 inline-flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => setSettings((prev) => prev ? ({
                ...prev,
                printWithSignatureAndSealByDefault: false,
              }) : prev)}
              className={`rounded-xl px-4 py-2 text-sm font-medium transition ${
                !settings.printWithSignatureAndSealByDefault
                  ? 'bg-slate-900 text-white'
                  : 'border border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
              }`}
            >
              Без подписи и печати
            </button>
            <button
              type="button"
              onClick={() => setSettings((prev) => prev ? ({
                ...prev,
                printWithSignatureAndSealByDefault: true,
              }) : prev)}
              className={`rounded-xl px-4 py-2 text-sm font-medium transition ${
                settings.printWithSignatureAndSealByDefault
                  ? 'bg-slate-900 text-white'
                  : 'border border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
              }`}
            >
              С подписью и печатью
            </button>
          </div>
        </div>
      ) : null}

      {(message || error) && (
        <div className={`mt-5 rounded-2xl border px-4 py-3 text-sm ${error ? 'border-red-200 bg-red-50 text-red-700' : 'border-emerald-200 bg-emerald-50 text-emerald-700'}`}>
          {error ?? message}
        </div>
      )}

      <div className="mt-6 grid gap-6 xl:grid-cols-[320px,minmax(0,1fr)]">
        <div className="rounded-2xl border border-gray-200 bg-gray-50 p-4 xl:sticky xl:top-4 xl:self-start">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-sm font-semibold text-gray-900">Список шаблонов</div>
              <div className="text-xs text-gray-500">Выберите шаблон слева, затем работайте с ним справа.</div>
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
                  <div className="text-base font-semibold text-gray-900">Рабочая область шаблона</div>
                  <div className="mt-1 text-sm text-gray-600">
                    Настрой свойства шаблона, затем переключайся между превью, HTML и списком переменных.
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
                    onClick={validateSelectedTemplate}
                    disabled={loading || saving}
                    className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    <FileSearch className="h-4 w-4" />
                    <span>Проверить HTML</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void resetSelectedTemplateHtml()}
                    disabled={loading || saving}
                    className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    <RotateCcw className="h-4 w-4" />
                    <span>Сбросить к стандартному</span>
                  </button>
                </div>
              </div>

              <div className="mt-5 space-y-5">
                <div className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="rounded-full bg-slate-900 px-3 py-1 text-xs font-semibold text-white">HTML + CSS</span>
                    <span className={`rounded-full px-3 py-1 text-xs font-semibold ${
                      settings?.activeTemplateKey === selectedTemplate.key
                        ? 'bg-emerald-100 text-emerald-700'
                        : 'bg-slate-100 text-slate-600'
                    }`}>
                      {settings?.activeTemplateKey === selectedTemplate.key ? 'Используется в системе' : 'Неактивный шаблон'}
                    </span>
                  </div>

                  <div className="mt-4 grid gap-4 lg:grid-cols-2">
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

                  <div className="mt-4 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
                    Пока шаблон редактируется как HTML. Word/DOCX-шаблоны в этот блок еще не подключены.
                  </div>
                </div>

                <div
                  role="tablist"
                  aria-label="Рабочая зона шаблона"
                  className="grid grid-cols-1 gap-2 rounded-2xl border border-gray-200 bg-white p-2 sm:grid-cols-3"
                >
                  {[
                    { key: 'preview', label: 'Превью' },
                    { key: 'editor', label: 'HTML' },
                    { key: 'tokens', label: 'Переменные' },
                  ].map((tab) => (
                    <button
                      key={tab.key}
                      type="button"
                      role="tab"
                      aria-selected={workspaceTab === tab.key}
                      onClick={() => setWorkspaceTab(tab.key as 'preview' | 'editor' | 'tokens')}
                      className={[
                        'rounded-xl px-4 py-2.5 text-sm font-medium transition-colors',
                        workspaceTab === tab.key
                          ? 'bg-blue-50 text-blue-700 shadow-sm'
                          : 'text-gray-600 hover:bg-gray-50',
                      ].join(' ')}
                    >
                      {tab.label}
                    </button>
                  ))}
                </div>

                {validationResult ? (
                  <div className="rounded-2xl border border-gray-200 bg-white p-4">
                    <div className="text-base font-semibold text-gray-900">Результат проверки шаблона</div>
                    {validationResult.errors.length === 0 && validationResult.warnings.length === 0 ? (
                      <div className="mt-3 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">
                        Критичных ошибок не найдено. Шаблон выглядит корректным.
                      </div>
                    ) : (
                      <div className="mt-3 space-y-3">
                        {validationResult.errors.length > 0 ? (
                          <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                            <div className="font-semibold">Ошибки</div>
                            <div className="mt-2 space-y-1">
                              {validationResult.errors.map((item) => (
                                <div key={item}>• {item}</div>
                              ))}
                            </div>
                          </div>
                        ) : null}
                        {validationResult.warnings.length > 0 ? (
                          <div className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                            <div className="font-semibold">Предупреждения</div>
                            <div className="mt-2 space-y-1">
                              {validationResult.warnings.map((item) => (
                                <div key={item}>• {item}</div>
                              ))}
                            </div>
                          </div>
                        ) : null}
                      </div>
                    )}
                  </div>
                ) : null}

                {workspaceTab === 'preview' && (
                  <div className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
                    <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                      <div>
                        <div className="text-base font-semibold text-gray-900">Живое превью документа</div>
                        <div className="mt-1 max-w-2xl text-sm text-gray-600">
                          Здесь показан итоговый документ на демо-данных, близких к твоему образцу. Это именно тот
                          печатный HTML-шаблон, который будет использоваться системой.
                        </div>
                      </div>
                      <button
                        type="button"
                        onClick={openPreviewInNewWindow}
                        disabled={!previewHtml}
                        className="inline-flex items-center justify-center rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        Открыть отдельно
                      </button>
                    </div>

                    <div className="mt-4 rounded-2xl border border-gray-200 bg-white p-3 text-xs leading-5 text-gray-500">
                      Старый встроенный макет счета больше не должен путать это превью: здесь отображается именно
                      печатный шаблон, а не отдельная ручная верстка.
                    </div>

                    <div className="mt-4 overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-inner">
                      {previewHtml ? (
                        <iframe
                          title={`Превью шаблона ${selectedTemplate.name}`}
                          srcDoc={previewHtml}
                          sandbox="allow-same-origin"
                          className="h-[72rem] w-full bg-white"
                        />
                      ) : (
                        <div className="flex h-[24rem] items-center justify-center px-6 text-center text-sm text-gray-500">
                          Введите HTML шаблона, чтобы увидеть итоговый документ.
                        </div>
                      )}
                    </div>
                  </div>
                )}

                {workspaceTab === 'editor' && (
                  <div className="rounded-2xl border border-gray-200 bg-white p-4">
                    <div className="flex flex-col gap-1">
                      <div className="text-base font-semibold text-gray-900">HTML шаблона</div>
                      <div className="text-sm text-gray-600">
                        Здесь редактируется структура документа. После правок переключись на вкладку превью и сразу
                        посмотри результат.
                      </div>
                    </div>

                    <textarea
                      value={selectedTemplate.html}
                      onChange={(event) => updateSelectedTemplate({ html: event.target.value })}
                      spellCheck={false}
                      className="mt-4 min-h-[40rem] w-full rounded-2xl border border-gray-300 bg-slate-950 px-4 py-3 font-mono text-xs leading-6 text-slate-100 shadow-inner"
                    />
                  </div>
                )}

                {workspaceTab === 'tokens' && (
                  <div className="rounded-2xl border border-gray-200 bg-gray-50 p-5">
                    <div className="text-base font-semibold text-gray-900">Доступные переменные</div>
                    <div className="mt-1 text-sm text-gray-600">
                      Переменные подставляются автоматически. Повторяемые поля работают только внутри блока <code>{'{{#lineItems}} ... {{/lineItems}}'}</code>.
                    </div>

                    <div className="mt-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
                      <label className="relative block lg:max-w-md lg:flex-1">
                        <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                        <input
                          type="text"
                          value={tokenSearch}
                          onChange={(event) => setTokenSearch(event.target.value)}
                          placeholder="Поиск по переменным, описанию или категории"
                          className="w-full rounded-xl border border-gray-300 bg-white py-2 pl-10 pr-3 text-sm text-gray-700"
                        />
                      </label>
                      <div className="text-xs text-gray-500">
                        Найдено токенов: {filteredTokenGroups.reduce((sum, [, items]) => sum + items.length, 0)}
                      </div>
                    </div>

                    <div className="mt-4 grid gap-4 xl:grid-cols-2">
                      {filteredTokenGroups.map(([category, items]) => (
                        <div key={category} className="rounded-2xl border border-gray-200 bg-white p-4">
                          <div className="text-sm font-semibold text-gray-900">{category}</div>
                          <div className="mt-3 space-y-3">
                            {items.map((token) => (
                              <div key={token.key} className="rounded-xl border border-gray-100 bg-gray-50 px-3 py-2">
                                <div className="flex items-start justify-between gap-3">
                                  <div className="font-mono text-xs text-blue-700">
                                    {token.key === 'lineItems' ? '{{#lineItems}} ... {{/lineItems}}' : `{{${token.key}}}`}
                                  </div>
                                  <button
                                    type="button"
                                    onClick={() => void copyTokenToClipboard(token.key)}
                                    className="inline-flex items-center gap-1 rounded-lg border border-gray-200 bg-white px-2 py-1 text-[11px] font-medium text-gray-600 hover:bg-gray-100"
                                  >
                                    {copiedToken === token.key ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                                    {copiedToken === token.key ? 'Скопировано' : 'Копировать'}
                                  </button>
                                </div>
                                <div className="mt-1 text-xs text-gray-600">{token.description}</div>
                              </div>
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                    {filteredTokenGroups.length === 0 ? (
                      <div className="mt-4 rounded-2xl border border-dashed border-gray-300 bg-white px-6 py-8 text-center text-sm text-gray-500">
                        По текущему запросу переменные не найдены.
                      </div>
                    ) : null}
                  </div>
                )}
              </div>
            </div>
          ) : (
            <div className="rounded-2xl border border-dashed border-gray-300 bg-gray-50 p-8 text-sm text-gray-500">
              Выберите шаблон слева или создайте новый.
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
