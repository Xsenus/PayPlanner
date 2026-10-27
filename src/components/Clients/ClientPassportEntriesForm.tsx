import { SystemDateInput } from '../ui/SystemDateInput';
import { Trash2 } from 'lucide-react';

import { PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE } from '../../config/features';
import { toRuDate } from '../../utils/dateUtils';

export interface ClientPassportEntryFormValue {
  series: string;
  number: string;
  issuedBy: string;
  issueDate: string;
  departmentCode: string;
  birthPlacePassport: string;
  birthPlaceFias: string;
  validFrom: string;
  validTo: string;
}

interface ClientPassportEntriesFormProps {
  entries: ClientPassportEntryFormValue[];
  canEdit: boolean;
  textReadOnly?: boolean;
  showValidityFields?: boolean;
  dateInputClassName?: string;
  onUpdateEntry: (index: number, key: keyof ClientPassportEntryFormValue, value: string) => void;
  onRemoveEntry: (index: number) => void | Promise<void>;
  onAddEntry: () => void;
}

const PASSPORT_DELETE_ICON_BUTTON_CLASS =
  'inline-flex h-9 w-9 items-center justify-center rounded-lg text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700 disabled:cursor-not-allowed disabled:opacity-60';
const PASSPORT_ENTRY_DATE_INPUT_CLASS =
  'w-full rounded-lg border border-gray-300 px-3 py-2 text-center disabled:bg-gray-100';

export function ClientPassportEntriesForm({
  entries,
  canEdit,
  textReadOnly = false,
  showValidityFields,
  dateInputClassName = PASSPORT_ENTRY_DATE_INPUT_CLASS,
  onUpdateEntry,
  onRemoveEntry,
  onAddEntry,
}: ClientPassportEntriesFormProps) {
  const shouldShowValidityFields = showValidityFields ?? PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE;

  if (textReadOnly && !canEdit) {
    return (
      <div className="space-y-3 md:col-span-2" aria-label="Паспорта">
        {entries.length === 0 ? <p className="text-sm text-gray-500">Паспорта не указаны</p> : entries.map((passport, index) => {
          const fields = [
            ['Серия', passport.series], ['Номер', passport.number], ['Дата выдачи', toRuDate(passport.issueDate)],
            ['Код подразделения', passport.departmentCode], ['Кем выдан', passport.issuedBy],
            ['Место рождения (как в паспорте)', passport.birthPlacePassport],
            ...(passport.birthPlaceFias?.trim() ? [['Место рождения по адресному справочнику', passport.birthPlaceFias]] : []),
            ...(shouldShowValidityFields ? [['Действует с', toRuDate(passport.validFrom)], ['Действует по', toRuDate(passport.validTo)]] : []),
          ];
          return (
            <section key={`passport-entry-${index}`} className="min-w-0 border-b border-slate-200 pb-3 last:border-0 last:pb-0">
              <h3 className="text-sm font-semibold text-slate-800">Паспорт #{index + 1}</h3>
              <dl className="mt-2 grid min-w-0 gap-2 md:grid-cols-2 xl:grid-cols-4">
                {fields.map(([label, value]) => <div key={label} className="min-w-0"><dt className="text-xs text-slate-500">{label}</dt><dd className="whitespace-pre-wrap text-sm text-slate-900 [overflow-wrap:anywhere]">{value || 'Не указано'}</dd></div>)}
              </dl>
            </section>
          );
        })}
      </div>
    );
  }

  return (
    <div className="md:col-span-2 space-y-4">
      {entries.map((passport, index) => (
        <div
          key={`passport-entry-${index}`}
          className="rounded-xl border border-slate-200 bg-slate-50 p-4"
        >
          <div className="mb-3 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800">Паспорт #{index + 1}</h3>
            <button
              type="button"
              onClick={() => {
                void onRemoveEntry(index);
              }}
              disabled={!canEdit}
              className={PASSPORT_DELETE_ICON_BUTTON_CLASS}
              title="Удалить паспорт"
              aria-label="Удалить паспорт"
            >
              <Trash2 className="h-4 w-4" />
            </button>
          </div>

          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Серия</span>
              <input
                value={passport.series}
                onChange={(event) => onUpdateEntry(index, 'series', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Номер</span>
              <input
                value={passport.number}
                onChange={(event) => onUpdateEntry(index, 'number', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Дата выдачи</span>
              <SystemDateInput
                type="date"
                value={passport.issueDate}
                onChange={(event) => onUpdateEntry(index, 'issueDate', event.target.value)}
                disabled={!canEdit}
                className={dateInputClassName}
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Код подразделения</span>
              <input
                value={passport.departmentCode}
                onChange={(event) => onUpdateEntry(index, 'departmentCode', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            {shouldShowValidityFields ? (
              <>
                <label className="text-sm text-slate-700">
                  <span className="mb-1 block">Действует с</span>
                  <SystemDateInput
                    type="date"
                    value={passport.validFrom}
                    onChange={(event) => onUpdateEntry(index, 'validFrom', event.target.value)}
                    disabled={!canEdit}
                    className={dateInputClassName}
                  />
                </label>

                <label className="text-sm text-slate-700">
                  <span className="mb-1 block">Действует по</span>
                  <SystemDateInput
                    type="date"
                    value={passport.validTo}
                    onChange={(event) => onUpdateEntry(index, 'validTo', event.target.value)}
                    disabled={!canEdit}
                    className={dateInputClassName}
                  />
                </label>
              </>
            ) : null}

            <label className="text-sm text-slate-700 xl:col-span-2">
              <span className="mb-1 block">Кем выдан</span>
              <input
                value={passport.issuedBy}
                onChange={(event) => onUpdateEntry(index, 'issuedBy', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700 xl:col-span-2">
              <span className="mb-1 block">Место рождения (как в паспорте)</span>
              <input
                value={passport.birthPlacePassport}
                onChange={(event) => onUpdateEntry(index, 'birthPlacePassport', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

          </div>
        </div>
      ))}

      <button
        type="button"
        onClick={onAddEntry}
        disabled={!canEdit}
        className="text-sm text-emerald-600"
      >
        + Добавить паспорт
      </button>
    </div>
  );
}
