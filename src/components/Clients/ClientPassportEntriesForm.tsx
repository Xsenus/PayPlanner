import { Trash2 } from 'lucide-react';

import { PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE } from '../../config/features';

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
  showValidityFields,
  dateInputClassName = PASSPORT_ENTRY_DATE_INPUT_CLASS,
  onUpdateEntry,
  onRemoveEntry,
  onAddEntry,
}: ClientPassportEntriesFormProps) {
  const shouldShowValidityFields = showValidityFields ?? PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE;

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
              <input
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
                  <input
                    type="date"
                    value={passport.validFrom}
                    onChange={(event) => onUpdateEntry(index, 'validFrom', event.target.value)}
                    disabled={!canEdit}
                    className={dateInputClassName}
                  />
                </label>

                <label className="text-sm text-slate-700">
                  <span className="mb-1 block">Действует по</span>
                  <input
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

            <label className="text-sm text-slate-700 xl:col-span-2">
              <span className="mb-1 block">Место рождения (ФИАС)</span>
              <input
                value={passport.birthPlaceFias}
                onChange={(event) => onUpdateEntry(index, 'birthPlaceFias', event.target.value)}
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
