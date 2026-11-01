import { apiService, type InvoiceReferenceDictionaries } from '../services/api';
import { assertDictionaryRows, useDictionarySnapshot, type DictionaryReadOptions } from './useDictionarySnapshot';

const empty: InvoiceReferenceDictionaries = { invoiceUnits: [], services: [], serviceCategories: [] };

async function load(signal: AbortSignal): Promise<InvoiceReferenceDictionaries> {
  const response = await apiService.getInvoiceReferenceDictionaries({ signal });
  for (const group of ['invoiceUnits', 'services', 'serviceCategories'] as const) assertDictionaryRows(response?.[group]);
  if (response.services.some(item => (item.defaultPrice != null && (typeof item.defaultPrice !== 'number' || !Number.isFinite(item.defaultPrice)))
    || (item.categoryId != null && (!Number.isSafeInteger(item.categoryId) || item.categoryId <= 0))
    || (item.unit != null && typeof item.unit !== 'string') || (item.description != null && typeof item.description !== 'string'))) {
    throw new Error('Не удалось подтвердить данные услуг. Повторите загрузку справочника.');
  }
  return { invoiceUnits: response.invoiceUnits.filter(item => item.isActive !== false),
    services: response.services.filter(item => item.isActive !== false), serviceCategories: response.serviceCategories.filter(item => item.isActive !== false) };
}

export function useInvoiceReferenceDictionaries(options?: DictionaryReadOptions) {
  const state = useDictionarySnapshot(load, empty, options);
  return { ...state.value, loading: state.loading, error: state.error, refresh: state.refresh, capture: state.capture };
}
