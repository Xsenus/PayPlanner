import { Trash2 } from 'lucide-react';

import type { DadataAddressSuggestion } from '../../types';
import { ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE } from '../../config/features';

export interface ClientAddressEntryFormValue {
  type: string;
  additionalTypes: string[];
  fullAddress: string;
  postalCode: string;
  country?: string;
  region: string;
  district: string;
  city: string;
  settlement: string;
  street: string;
  house: string;
  flat: string;
  fiasId?: string;
  kladrId?: string;
  geoLat?: string;
  geoLon?: string;
  validFrom: string;
  validTo: string;
}

export interface AddressTypeOption {
  value: string;
  label: string;
}

interface ClientAddressEntriesFormProps {
  entries: ClientAddressEntryFormValue[];
  canEdit: boolean;
  compactReadOnly?: boolean;
  showValidityFields?: boolean;
  validityDateInputClassName?: string;
  addressTypeOptions: AddressTypeOption[];
  suggestionsByIndex: Record<number, DadataAddressSuggestion[]>;
  onUpdateEntry: (index: number, key: keyof ClientAddressEntryFormValue, value: string) => void;
  onRemoveEntry: (index: number) => void | Promise<void>;
  onAddEntry: () => void;
  onToggleAdditionalType: (index: number, type: string) => void;
  onScheduleSuggestions: (index: number, query: string) => void;
  onFindAddress: (index: number) => void;
  onApplySuggestion: (index: number, suggestion: DadataAddressSuggestion) => Promise<void>;
}

const ADDRESS_DELETE_ICON_BUTTON_CLASS =
  'inline-flex h-9 w-9 items-center justify-center rounded-lg text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700 disabled:cursor-not-allowed disabled:opacity-60';
const ADDRESS_ENTRY_VALIDITY_DATE_INPUT_CLASS =
  'w-full rounded-lg border border-gray-300 px-3 py-2 text-center disabled:bg-gray-100';

export function ClientAddressEntriesForm({
  entries,
  canEdit,
  compactReadOnly = false,
  showValidityFields,
  validityDateInputClassName = ADDRESS_ENTRY_VALIDITY_DATE_INPUT_CLASS,
  addressTypeOptions,
  suggestionsByIndex,
  onUpdateEntry,
  onRemoveEntry,
  onAddEntry,
  onToggleAdditionalType,
  onScheduleSuggestions,
  onFindAddress,
  onApplySuggestion,
}: ClientAddressEntriesFormProps) {
  const showReadonlyControls = !compactReadOnly;
  const shouldShowValidityFields = showValidityFields ?? ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE;

  return (
    <div className="space-y-4 md:col-span-2">
      {entries.map((entry, index) => (
        <div
          key={`address-entry-${index}`}
          className="rounded-xl border border-slate-200 bg-slate-50 p-4"
        >
          <div className="mb-3 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800">Адрес #{index + 1}</h3>
            {showReadonlyControls ? (
              <button
                type="button"
                onClick={() => {
                  void onRemoveEntry(index);
                }}
                disabled={!canEdit}
                className={ADDRESS_DELETE_ICON_BUTTON_CLASS}
                title="Удалить адрес"
                aria-label="Удалить адрес"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            ) : null}
          </div>

          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            <label className="text-sm text-slate-700 xl:col-span-2">
              <span className="mb-1 block">Основной тип</span>
              <select
                value={entry.type}
                onChange={(event) => onUpdateEntry(index, 'type', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              >
                {addressTypeOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>

            {shouldShowValidityFields ? (
              <>
                <label className="text-sm text-slate-700">
                  <span className="mb-1 block">Действует с</span>
                  <input
                    type="date"
                    value={entry.validFrom}
                    onChange={(event) => onUpdateEntry(index, 'validFrom', event.target.value)}
                    disabled={!canEdit}
                    className={validityDateInputClassName}
                  />
                </label>

                <label className="text-sm text-slate-700">
                  <span className="mb-1 block">Действует по</span>
                  <input
                    type="date"
                    value={entry.validTo}
                    onChange={(event) => onUpdateEntry(index, 'validTo', event.target.value)}
                    disabled={!canEdit}
                    className={validityDateInputClassName}
                  />
                </label>
              </>
            ) : null}

            <div className="xl:col-span-2">
              <span className="mb-1 block text-sm text-slate-700">Дополнительно относится к</span>
              <div className="flex flex-wrap gap-2 rounded-lg border border-gray-200 bg-white p-2">
                {addressTypeOptions.map((option) => (
                  <label
                    key={option.value}
                    className="inline-flex items-center gap-1 text-xs text-gray-700"
                  >
                    <input
                      type="checkbox"
                      checked={entry.additionalTypes.includes(option.value)}
                      onChange={() => onToggleAdditionalType(index, option.value)}
                      disabled={!canEdit}
                    />
                    {option.label}
                  </label>
                ))}
              </div>
            </div>

            <label
              className={`text-sm text-slate-700 ${
                showReadonlyControls ? 'xl:col-span-3' : 'xl:col-span-4'
              }`}
            >
              <span className="mb-1 block">Полный адрес</span>
              <input
                value={entry.fullAddress}
                onChange={(event) => {
                  onUpdateEntry(index, 'fullAddress', event.target.value);
                  onScheduleSuggestions(index, event.target.value);
                }}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            {showReadonlyControls ? (
              <div className="flex items-end xl:col-span-1">
                <button
                  type="button"
                  onClick={() => onFindAddress(index)}
                  disabled={!canEdit}
                  className="w-full rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-60"
                >
                  Найти адрес
                </button>
              </div>
            ) : null}

            {showReadonlyControls && (suggestionsByIndex[index]?.length ?? 0) > 0 ? (
              <div className="rounded-lg border border-indigo-100 bg-indigo-50 p-3 xl:col-span-4">
                <p className="mb-2 text-xs font-medium uppercase tracking-wide text-indigo-600">
                  Подсказки адреса
                </p>
                <div className="flex flex-wrap gap-2">
                  {suggestionsByIndex[index].map((item) => (
                    <button
                      key={`${item.value}-${item.unrestrictedValue ?? ''}`}
                      type="button"
                      onClick={() => {
                        void onApplySuggestion(index, item);
                      }}
                      className="rounded-full border border-indigo-200 bg-white px-3 py-1 text-xs text-indigo-700 hover:bg-indigo-100"
                    >
                      {item.unrestrictedValue || item.value}
                    </button>
                  ))}
                </div>
              </div>
            ) : null}

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Индекс</span>
              <input
                value={entry.postalCode}
                onChange={(event) => onUpdateEntry(index, 'postalCode', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Область</span>
              <input
                value={entry.region}
                onChange={(event) => onUpdateEntry(index, 'region', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Район</span>
              <input
                value={entry.district}
                onChange={(event) => onUpdateEntry(index, 'district', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Город</span>
              <input
                value={entry.city}
                onChange={(event) => onUpdateEntry(index, 'city', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Населённый пункт</span>
              <input
                value={entry.settlement}
                onChange={(event) => onUpdateEntry(index, 'settlement', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Улица</span>
              <input
                value={entry.street}
                onChange={(event) => onUpdateEntry(index, 'street', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Дом</span>
              <input
                value={entry.house}
                onChange={(event) => onUpdateEntry(index, 'house', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>

            <label className="text-sm text-slate-700">
              <span className="mb-1 block">Квартира</span>
              <input
                value={entry.flat}
                onChange={(event) => onUpdateEntry(index, 'flat', event.target.value)}
                disabled={!canEdit}
                className="w-full rounded-lg border border-gray-300 px-3 py-2"
              />
            </label>
          </div>
        </div>
      ))}

      {showReadonlyControls ? (
        <button
          type="button"
          onClick={onAddEntry}
          disabled={!canEdit}
          className="text-sm text-emerald-600"
        >
          + Добавить адрес
        </button>
      ) : null}
    </div>
  );
}
