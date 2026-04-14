import { useState, useEffect, useCallback } from 'react';
import { apiService } from '../services/api';
import type {
  ClientStatus,
  DealType,
  IncomeType,
  PaymentSource,
  PaymentStatusEntity,
  ServiceDictionaryItem,
  ServiceCategoryDictionaryItem,
} from '../types';
import type { BaseDictItem } from '../types/dictionaries';

const DICTS_CHANGED = 'dicts:changed';

export function useDictionaries() {
  const [dealTypes, setDealTypes] = useState<DealType[]>([]);
  const [incomeTypes, setIncomeTypes] = useState<IncomeType[]>([]);
  const [paymentSources, setPaymentSources] = useState<PaymentSource[]>([]);
  const [services, setServices] = useState<ServiceDictionaryItem[]>([]);
  const [serviceCategories, setServiceCategories] = useState<ServiceCategoryDictionaryItem[]>([]);
  const [paymentStatuses, setPaymentStatuses] = useState<PaymentStatusEntity[]>([]);
  const [clientStatuses, setClientStatuses] = useState<ClientStatus[]>([]);
  const [countries, setCountries] = useState<BaseDictItem[]>([]);
  const [baseCurrencies, setBaseCurrencies] = useState<BaseDictItem[]>([]);
  const [serviceLanguages, setServiceLanguages] = useState<BaseDictItem[]>([]);
  const [organizationForms, setOrganizationForms] = useState<BaseDictItem[]>([]);
  const [timezones, setTimezones] = useState<BaseDictItem[]>([]);
  const [taxSystems, setTaxSystems] = useState<BaseDictItem[]>([]);
  const [taxSystemModes, setTaxSystemModes] = useState<BaseDictItem[]>([]);
  const [invoiceUnits, setInvoiceUnits] = useState<BaseDictItem[]>([]);
  const [invoiceItemNames, setInvoiceItemNames] = useState<BaseDictItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [deal, incIncome, incExpense, sources, servicesResp, serviceCategoriesResp, statuses, clientStatusesResp, countriesResp, baseCurrenciesResp, serviceLanguagesResp, organizationFormsResp, timezonesResp, taxSystemsResp, taxSystemModesResp, invoiceUnitsResp, invoiceItemNamesResp] = await Promise.all([
        apiService.getDict('deal-types'),
        apiService.getIncomeTypes('Income'),
        apiService.getIncomeTypes('Expense'),
        apiService.getDict('payment-sources'),
        apiService.getDict('services'),
        apiService.getDict('service-categories'),
        apiService.getDict('payment-statuses'),
        apiService.getDict('client-statuses'),
        apiService.getDict('countries'),
        apiService.getDict('base-currencies'),
        apiService.getDict('service-languages'),
        apiService.getDict('organization-forms'),
        apiService.getDict('timezones'),
        apiService.getDict('tax-systems'),
        apiService.getDict('tax-system-modes'),
        apiService.getDict('invoice-units'),
        apiService.getDict('invoice-item-names'),
      ]);

      setDealTypes(deal ?? []);

      const merged = [...(incIncome ?? []), ...(incExpense ?? [])];
      const byId = new Map<number, IncomeType>();
      for (const it of merged) byId.set(it.id, it);
      setIncomeTypes(Array.from(byId.values()));

      setPaymentSources(sources ?? []);
      setServices((servicesResp ?? []).filter((service) => service.isActive !== false));
      setServiceCategories((serviceCategoriesResp ?? []).filter((item) => item.isActive !== false));
      setPaymentStatuses(statuses ?? []);
      setClientStatuses((clientStatusesResp ?? []).filter((status) => status.isActive !== false));
      setCountries((countriesResp ?? []).filter((item) => item.isActive !== false));
      setBaseCurrencies((baseCurrenciesResp ?? []).filter((item) => item.isActive !== false));
      setServiceLanguages((serviceLanguagesResp ?? []).filter((item) => item.isActive !== false));
      setOrganizationForms((organizationFormsResp ?? []).filter((item) => item.isActive !== false));
      setTimezones((timezonesResp ?? []).filter((item) => item.isActive !== false));
      setTaxSystems((taxSystemsResp ?? []).filter((item) => item.isActive !== false));
      setTaxSystemModes((taxSystemModesResp ?? []).filter((item) => item.isActive !== false));
      setInvoiceUnits((invoiceUnitsResp ?? []).filter((item) => item.isActive !== false));
      setInvoiceItemNames((invoiceItemNamesResp ?? []).filter((item) => item.isActive !== false));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load dictionaries');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const handler = () => void refresh();
    window.addEventListener(DICTS_CHANGED, handler);
    return () => window.removeEventListener(DICTS_CHANGED, handler);
  }, [refresh]);

  return {
    dealTypes,
    incomeTypes,
    paymentSources,
    services,
    serviceCategories,
    paymentStatuses,
    clientStatuses,
    countries,
    baseCurrencies,
    serviceLanguages,
    organizationForms,
    timezones,
    taxSystems,
    taxSystemModes,
    invoiceUnits,
    invoiceItemNames,
    loading,
    error,
    refresh,
  };
}
