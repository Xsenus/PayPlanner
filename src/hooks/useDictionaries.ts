import { useCallback } from 'react';
import { apiService } from '../services/api';
import { assertDictionaryRows, useDictionarySnapshot, type DictionaryReadOptions } from './useDictionarySnapshot';
import type { ClientStatus, DealType, IncomeType, PaymentSource, PaymentStatusEntity, ServiceDictionaryItem, ServiceCategoryDictionaryItem } from '../types';
import type { BaseDictItem } from '../types/dictionaries';

type DictionarySnapshot = {
  dealTypes: DealType[]; incomeTypes: IncomeType[]; paymentSources: PaymentSource[];
  services: ServiceDictionaryItem[]; serviceCategories: ServiceCategoryDictionaryItem[];
  paymentStatuses: PaymentStatusEntity[]; clientStatuses: ClientStatus[];
  countries: BaseDictItem[]; baseCurrencies: BaseDictItem[]; serviceLanguages: BaseDictItem[];
  organizationForms: BaseDictItem[]; timezones: BaseDictItem[]; taxSystems: BaseDictItem[];
  taxSystemModes: BaseDictItem[]; invoiceUnits: BaseDictItem[]; invoiceItemNames: BaseDictItem[];
};
export type DictionaryGroup = keyof DictionarySnapshot;
export type DictionariesOptions = DictionaryReadOptions & { include?: readonly DictionaryGroup[] };
const empty: DictionarySnapshot = { dealTypes: [], incomeTypes: [], paymentSources: [], services: [], serviceCategories: [],
  paymentStatuses: [], clientStatuses: [], countries: [], baseCurrencies: [], serviceLanguages: [], organizationForms: [], timezones: [],
  taxSystems: [], taxSystemModes: [], invoiceUnits: [], invoiceItemNames: [] };
const loaders: { [K in DictionaryGroup]: (signal: AbortSignal) => Promise<DictionarySnapshot[K]> } = {
  dealTypes: signal => apiService.getDict('deal-types', { signal }),
  incomeTypes: async signal => {
    const parts = await Promise.all([apiService.getIncomeTypes('Income', undefined, { signal }), apiService.getIncomeTypes('Expense', undefined, { signal })]);
    parts.forEach(assertDictionaryRows);
    const byId = new Map<number, IncomeType>();
    for (const part of parts) for (const item of part) byId.set(item.id, item);
    return Array.from(byId.values());
  },
  paymentSources: signal => apiService.getDict('payment-sources', { signal }),
  services: signal => apiService.getDict('services', { signal }),
  serviceCategories: signal => apiService.getDict('service-categories', { signal }),
  paymentStatuses: signal => apiService.getDict('payment-statuses', { signal }),
  clientStatuses: signal => apiService.getDict('client-statuses', { signal }),
  countries: signal => apiService.getDict('countries', { signal }),
  baseCurrencies: signal => apiService.getDict('base-currencies', { signal }),
  serviceLanguages: signal => apiService.getDict('service-languages', { signal }),
  organizationForms: signal => apiService.getDict('organization-forms', { signal }),
  timezones: signal => apiService.getDict('timezones', { signal }),
  taxSystems: signal => apiService.getDict('tax-systems', { signal }),
  taxSystemModes: signal => apiService.getDict('tax-system-modes', { signal }),
  invoiceUnits: signal => apiService.getDict('invoice-units', { signal }),
  invoiceItemNames: signal => apiService.getDict('invoice-item-names', { signal }),
};
const groups = Object.keys(loaders) as DictionaryGroup[];
const includeInactive = new Set<DictionaryGroup>(['dealTypes', 'incomeTypes', 'paymentSources', 'paymentStatuses']);

export function useDictionaries(options?: DictionariesOptions) {
  // Canonical order prevents refetching for equivalent inline selections or duplicates.
  const includeKey = groups.filter(group => !options?.include || options.include.includes(group)).join('|');
  const load = useCallback(async (signal: AbortSignal): Promise<DictionarySnapshot> => {
    const entries = await Promise.all((includeKey.split('|').filter(Boolean) as DictionaryGroup[]).map(async group => {
      const rows = await loaders[group](signal);
      assertDictionaryRows(rows);
      return [group, includeInactive.has(group) ? rows : rows.filter(item => item.isActive !== false)];
    }));
    return { ...empty, ...Object.fromEntries(entries) };
  }, [includeKey]);
  const state = useDictionarySnapshot(load, empty, { enabled: (options?.enabled ?? true) && Boolean(includeKey),
    ownerScope: JSON.stringify([options?.ownerScope ?? '', includeKey]) });
  return { ...state.value, loading: state.loading, error: state.error, refresh: state.refresh };
}
