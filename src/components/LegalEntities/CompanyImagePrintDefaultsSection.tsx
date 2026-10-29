import { useId } from 'react';
import type { CompanyDocumentImagePrintDefaults } from '../../types/documentImagePrint';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { companyPrintDocumentKinds, hasCompanyImagePrintVersion, normalizeCompanyImagePrintDefaults, type CompanyImagePrintDraft } from './companyImagePrintDefaults';

const labels = { invoice: ['Счета', 'счетов'], act: ['Акты', 'актов'], contract: ['Договоры', 'договоров'] } as const;
const images = { includeLogo: 'Логотип', includeSeal: 'Печать', includeSignature: 'Подпись' } as const;

interface Props {
  draft: CompanyImagePrintDraft;
  disabled: boolean;
  onChange: (value: CompanyDocumentImagePrintDefaults | null) => void;
}

export function CompanyImagePrintDefaultsSection({ draft, disabled, onChange }: Props) {
  const id = useId();
  const ready = hasCompanyImagePrintVersion(draft);
  const blocked = disabled || !ready;
  const change = (value: CompanyDocumentImagePrintDefaults | null) => {
    if (!blocked) onChange(normalizeCompanyImagePrintDefaults(value));
  };
  return (
    <section aria-labelledby={`${id}-title`} className="mt-3 space-y-3 rounded-xl border border-slate-200 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={`${id}-title`} className="text-sm font-semibold text-slate-800">Изображения в документах</h3>
        <button type="button" disabled={blocked} onClick={() => change(null)} className="rounded-lg border border-slate-300 px-2 py-1 text-sm disabled:opacity-50">Сбросить настройки изображений</button>
      </div>
      <p className="text-xs text-slate-600">Выберите отдельный набор логотипа, печати и подписи для каждого вида документа. Без своего набора используются общие параметры печати.</p>
      {!ready ? <p role="status" className="text-xs text-amber-800">Откройте карточку заново, чтобы изменить настройки изображений.</p> : null}
      {draft.invalid ? <p role="status" className="text-xs text-amber-800">Сохранённые настройки изображений не удалось прочитать. Выберите набор заново или сбросьте настройки; остальные поля профиля можно сохранять как обычно.</p> : null}
      <div className="grid gap-3 sm:grid-cols-3">
        {companyPrintDocumentKinds.map(kind => {
          const options = draft.value?.[kind] ?? null;
          const value = draft.value ?? { invoice: null, act: null, contract: null };
          return (
            <fieldset key={kind} disabled={blocked} className="space-y-2 rounded-lg border border-slate-200 p-3">
              <legend className="px-1 text-sm font-medium">{labels[kind][0]}</legend>
              <label className="flex items-center gap-2 text-sm">
                <SystemCheckbox checked={options !== null} disabled={blocked} onChange={event => change({ ...value, [kind]: event.target.checked ? { includeLogo: true, includeSeal: false, includeSignature: false } : null })} aria-label={`Свой набор для ${labels[kind][1]}`} />
                Свой набор
              </label>
              <div className="flex flex-wrap gap-2">
                {(Object.keys(images) as Array<keyof typeof images>).map(key => (
                  <label key={key} className="flex items-center gap-1.5 text-xs">
                    <SystemCheckbox checked={options?.[key] ?? false} disabled={blocked || !options} onChange={event => { if (options) change({ ...value, [kind]: { ...options, [key]: event.target.checked } }); }} aria-label={`${images[key]} для ${labels[kind][1]}`} />
                    {images[key]}
                  </label>
                ))}
              </div>
            </fieldset>
          );
        })}
      </div>
    </section>
  );
}
