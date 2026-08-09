import { useEffect, useMemo, useState } from 'react';
import { FileSearch, RotateCcw, Save } from 'lucide-react';
import { apiService, type InvoiceDocumentTemplateDto, type InvoiceDocumentTemplateSettingsDto } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { DocumentTemplateHistoryPanel } from './DocumentTemplateHistoryPanel';
import { SystemSelect } from '../ui/SystemSelect';

const previewTokens: Record<string, string> = {
  'document.title': 'Акт № 48 от 9 августа 2026 г.',
  'act.number': '48',
  'act.date': '09.08.2026',
  'act.dateLong': '9 августа 2026 г.',
  'act.basis': 'Счет № 125 от 01.08.2026',
  'act.total': '25 000,00',
  'act.totalWords': 'Двадцать пять тысяч рублей 00 копеек',
  'act.vat': '0,00',
  'act.itemsCount': '2',
  'act.commentHtml': '<div class="comment"><b>Комментарий:</b> Документы переданы заказчику.</div>',
  'seller.requisitesHtml': 'ООО «ЭлитПраво», ИНН 6330096203, КПП 633001001, г. Новокуйбышевск',
  'seller.signaturePartyName': 'ООО «ЭлитПраво»',
  'seller.signatoryShortName': 'Кожеваткин А. В.',
  'seller.signatureHtml': '<span style="position:absolute;left:20px;color:#64748b;font-style:italic">Подпись</span>',
  'seller.sealHtml': '<span style="position:absolute;left:95px;color:#64748b">М.П.</span>',
  'buyer.requisitesHtml': 'Иванов Иван Иванович, паспорт 36 17 № 441597, г. Самара',
  'buyer.signaturePartyName': 'Иванов Иван Иванович',
  'buyer.signatoryShortName': 'Иванов И. И.',
};

const previewItems = [
  { index: '1', name: 'Подготовка процессуальных документов', quantity: '1', unit: 'усл.', price: '15 000,00', total: '15 000,00' },
  { index: '2', name: 'Представительство в судебном заседании', quantity: '1', unit: 'усл.', price: '10 000,00', total: '10 000,00' },
];

function renderPreview(html: string, includeSignatureAndSeal: boolean) {
  const withRows = html.replace(/\{\{#lineItems\}\}([\s\S]*?)\{\{\/lineItems\}\}/g, (_match, row: string) => (
    previewItems.map((item) => row.replace(/\{\{\s*([a-zA-Z0-9._]+)\s*\}\}/g, (token, key: keyof typeof item) => item[key] ?? token)).join('')
  ));

  return withRows.replace(/\{\{\s*([a-zA-Z0-9._]+)\s*\}\}/g, (_match, key: string) => {
    if (!includeSignatureAndSeal && (key === 'seller.signatureHtml' || key === 'seller.sealHtml')) return '';
    return previewTokens[key] ?? '';
  });
}

function validateTemplate(template: InvoiceDocumentTemplateDto | null) {
  if (!template?.html.trim()) return 'HTML шаблона не заполнен.';
  const open = (template.html.match(/\{\{#lineItems\}\}/g) ?? []).length;
  const close = (template.html.match(/\{\{\/lineItems\}\}/g) ?? []).length;
  if (open !== close) return 'Повторяемый блок {{#lineItems}} закрыт не полностью.';
  if (!/<html[\s>]/i.test(template.html) || !/<body[\s>]/i.test(template.html)) return 'Шаблон должен содержать теги <html> и <body>.';
  return null;
}

export function ActDocumentTemplateSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
  const [settings, setSettings] = useState<InvoiceDocumentTemplateSettingsDto | null>(null);
  const [selectedKey, setSelectedKey] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [historyRefreshKey, setHistoryRefreshKey] = useState(0);

  const activeTemplate = useMemo(
    () => settings?.templates.find((template) => template.key === selectedKey) ?? settings?.templates[0] ?? null,
    [selectedKey, settings],
  );
  const previewHtml = useMemo(
    () => activeTemplate ? renderPreview(activeTemplate.html, settings?.printWithSignatureAndSealByDefault ?? false) : '',
    [activeTemplate, settings?.printWithSignatureAndSealByDefault],
  );

  useEffect(() => {
    let active = true;
    void apiService.getActDocumentTemplateSettings()
      .then((response) => {
        if (!active) return;
        setSettings(response);
        setSelectedKey(response.activeTemplateKey || response.templates[0]?.key || '');
      })
      .catch((err) => active && setError(extractDisplayErrorMessage(err, 'Не удалось загрузить шаблон акта')))
      .finally(() => active && setLoading(false));
    return () => { active = false; };
  }, []);

  const updateTemplate = (patch: Partial<InvoiceDocumentTemplateDto>) => {
    if (!activeTemplate) return;
    setSettings((current) => current ? ({
      ...current,
      templates: current.templates.map((template) => template.key === activeTemplate.key ? { ...template, ...patch } : template),
    }) : current);
  };

  const save = async () => {
    if (!settings || !activeTemplate) return;
    const validationError = validateTemplate(activeTemplate);
    if (validationError) {
      setError(validationError);
      return;
    }
    const approved = await confirm({
      title: 'Сохранить шаблон акта',
      description: 'После сохранения новая версия будет использоваться для печати, PDF, email и Яндекс.Диска.',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setSaving(true);
    setError(null);
    try {
      const response = await apiService.updateActDocumentTemplateSettings({
        activeTemplateKey: selectedKey,
        printWithSignatureAndSealByDefault: settings.printWithSignatureAndSealByDefault,
        templates: settings.templates,
      });
      setSettings(response);
      setSelectedKey(response.activeTemplateKey || response.templates[0]?.key || '');
      setHistoryRefreshKey((value) => value + 1);
      toast.success('Печатный шаблон акта сохранен.', 'Акты');
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить шаблон акта');
      setError(message);
      toast.error(message, 'Акты');
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="rounded-xl border border-gray-200 bg-white p-3 text-sm text-gray-500">Загрузка шаблона акта...</div>;

  return (
    <div className="rounded-2xl border border-gray-200 bg-white p-3 shadow-sm lg:p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="text-lg font-semibold text-gray-900">Печатный шаблон акта</h4>
          <p className="mt-1 text-sm text-gray-600">Один активный HTML-шаблон используется для печати, Word/PDF, email и Яндекс.Диска.</p>
        </div>
        <button type="button" onClick={() => void save()} disabled={saving || !activeTemplate} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60">
          <Save className="h-4 w-4" />{saving ? 'Сохранение...' : 'Сохранить'}
        </button>
      </div>

      {error ? <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div> : null}

      {settings && activeTemplate ? (
        <>
          <div className="mt-3 grid gap-3 md:grid-cols-3">
            <label className="block"><span className="text-sm font-medium text-gray-700">Активный шаблон</span><SystemSelect value={selectedKey} onChange={(event) => { setSelectedKey(event.target.value); setSettings((current) => current ? { ...current, activeTemplateKey: event.target.value } : current); }} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm">{settings.templates.map((template) => <option key={template.key} value={template.key}>{template.name}</option>)}</SystemSelect></label>
            <label className="block"><span className="text-sm font-medium text-gray-700">Название</span><input value={activeTemplate.name} onChange={(event) => updateTemplate({ name: event.target.value })} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" /></label>
            <label className="flex items-end"><span className="flex min-h-10 w-full items-center gap-2 rounded-lg border border-gray-200 bg-gray-50 px-3 text-sm text-gray-700"><input type="checkbox" checked={settings.printWithSignatureAndSealByDefault} onChange={(event) => setSettings((current) => current ? { ...current, printWithSignatureAndSealByDefault: event.target.checked } : current)} />Подпись и печать по умолчанию</span></label>
          </div>
          <label className="mt-3 block"><span className="text-sm font-medium text-gray-700">Описание</span><input value={activeTemplate.description ?? ''} onChange={(event) => updateTemplate({ description: event.target.value })} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" /></label>

          <div className="mt-3 grid gap-3 xl:grid-cols-[minmax(0,1fr)_minmax(360px,0.9fr)]">
            <div>
              <div className="mb-2 flex items-center justify-between gap-2"><span className="text-sm font-semibold text-gray-900">HTML шаблона</span><button type="button" onClick={() => updateTemplate({ html: settings.defaultTemplateHtml })} className="inline-flex items-center gap-1.5 rounded-lg border border-gray-300 px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"><RotateCcw className="h-3.5 w-3.5" />Стандартный HTML</button></div>
              <textarea value={activeTemplate.html} onChange={(event) => updateTemplate({ html: event.target.value })} spellCheck={false} className="min-h-[38rem] w-full rounded-xl border border-gray-300 bg-slate-950 px-3 py-2 font-mono text-xs leading-5 text-slate-100" />
            </div>
            <div>
              <div className="mb-2 flex items-center gap-2 text-sm font-semibold text-gray-900"><FileSearch className="h-4 w-4 text-blue-600" />Предпросмотр на демо-данных</div>
              <div className="overflow-hidden rounded-xl border border-gray-200 bg-white"><iframe title="Предпросмотр шаблона акта" srcDoc={previewHtml} sandbox="" className="h-[38rem] w-full bg-white" /></div>
            </div>
          </div>

          <details className="mt-3 rounded-xl border border-gray-200 bg-gray-50 p-3"><summary className="cursor-pointer text-sm font-semibold text-gray-900">Доступные переменные: {settings.tokens.length}</summary><div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-3">{settings.tokens.map((token) => <div key={token.key} className="rounded-lg border border-gray-200 bg-white p-2"><code className="text-xs font-semibold text-blue-700">{token.isRepeating && token.key === 'lineItems' ? '{{#lineItems}}...{{/lineItems}}' : `{{${token.key}}}`}</code><div className="mt-1 text-xs text-gray-600">{token.description}</div></div>)}</div></details>

          <DocumentTemplateHistoryPanel kind="act" refreshKey={historyRefreshKey} onRestored={(response) => { setSettings(response); setSelectedKey(response.activeTemplateKey || response.templates[0]?.key || ''); setHistoryRefreshKey((value) => value + 1); }} />
        </>
      ) : <div className="mt-3 text-sm text-gray-500">Шаблоны акта не найдены.</div>}
    </div>
  );
}
