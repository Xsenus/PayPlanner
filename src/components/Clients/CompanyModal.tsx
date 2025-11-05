import { useEffect, useMemo, useState } from 'react';
import { X, Sparkles, Loader2, ChevronDown, ChevronUp, Check } from 'lucide-react';
import { createPortal } from 'react-dom';
import { useTranslation } from '../../hooks/useTranslation';
import type { Company, CompanyPayload, Client } from '../../types';
import { apiService } from '../../services/api';

interface CompanyModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (company: CompanyPayload) => Promise<void>;
  onDelete?: (id: number) => Promise<void>;
  company?: Company;
  clients: Client[];
}

export function CompanyModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  company,
  clients,
}: CompanyModalProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [formData, setFormData] = useState({
    fullName: '',
    shortName: '',
    inn: '',
    kpp: '',
    actualAddress: '',
    legalAddress: '',
    notes: '',
    isActive: true,
    email: '',
    phone: '',
  });
  const [selectedClientIds, setSelectedClientIds] = useState<number[]>([]);
  const [showClientsDictionary, setShowClientsDictionary] = useState(false);
  const [lookupLoading, setLookupLoading] = useState(false);
  const [lookupError, setLookupError] = useState<string | null>(null);

  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen, onClose]);

  useEffect(() => {
    if (company) {
      setFormData({
        fullName: company.fullName || company.name || '',
        shortName: company.shortName || company.name || '',
        inn: company.inn || '',
        kpp: company.kpp || '',
        actualAddress: company.actualAddress || '',
        legalAddress: company.legalAddress || company.actualAddress || '',
        notes: company.notes || '',
        isActive: company.isActive,
        email: company.email || '',
        phone: company.phone || '',
      });
      setSelectedClientIds((company.members ?? []).map((m) => m.id));
    } else {
      setFormData({
        fullName: '',
        shortName: '',
        inn: '',
        kpp: '',
        actualAddress: '',
        legalAddress: '',
        notes: '',
        isActive: true,
        email: '',
        phone: '',
      });
      setSelectedClientIds([]);
    }
    setShowClientsDictionary(false);
    setLookupError(null);
  }, [company]);

  if (!isOpen) return null;

  const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    const { name, value, type, checked } = e.target as HTMLInputElement;
    setFormData((prev) => ({
      ...prev,
      [name]: type === 'checkbox' ? checked : value,
    }));
    if (name === 'inn') {
      setLookupError(null);
    }
  };

  const toggleClient = (clientId: number) => {
    setSelectedClientIds((prev) =>
      prev.includes(clientId) ? prev.filter((id) => id !== clientId) : [...prev, clientId],
    );
  };

  const sortedClients = useMemo(
    () =>
      [...clients].sort((a, b) => (a.name || '').localeCompare(b.name || '', undefined, { sensitivity: 'base' })),
    [clients],
  );

  const selectedClients = useMemo(
    () =>
      selectedClientIds
        .map((id) => clients.find((clientItem) => clientItem.id === id))
        .filter((clientItem): clientItem is Client => Boolean(clientItem)),
    [clients, selectedClientIds],
  );

  const handleLookupByInn = async () => {
    const inn = formData.inn.trim();
    if (!inn) {
      setLookupError(t('enterInnToSearch') || 'Введите ИНН, чтобы выполнить поиск.');
      return;
    }

    setLookupLoading(true);
    setLookupError(null);
    try {
      const suggestion = await apiService.lookupCompanyByInn(inn);
      if (!suggestion) {
        setLookupError(t('dadataNotFound') || 'Компания не найдена в Dadata.');
        return;
      }

      setFormData((prev) => ({
        ...prev,
        fullName: suggestion.fullName || prev.fullName,
        shortName: suggestion.shortName || prev.shortName,
        inn: suggestion.inn || prev.inn,
        kpp: suggestion.kpp || prev.kpp,
        actualAddress: suggestion.actualAddress || prev.actualAddress,
        legalAddress: suggestion.legalAddress || prev.legalAddress,
      }));
    } catch (err) {
      console.error('Failed to lookup company by INN', err);
      setLookupError(t('dadataUnavailable') || 'Не удалось получить данные из Dadata.');
    } finally {
      setLookupLoading(false);
    }
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLoading(true);
    try {
      const shortName = (formData.shortName || formData.fullName).trim();
      const payload: CompanyPayload = {
        name: shortName,
        fullName: formData.fullName.trim(),
        shortName,
        inn: formData.inn.trim(),
        kpp: formData.kpp.trim(),
        email: formData.email.trim(),
        phone: formData.phone.trim(),
        actualAddress: formData.actualAddress.trim(),
        legalAddress: formData.legalAddress.trim() || formData.actualAddress.trim(),
        notes: formData.notes,
        isActive: formData.isActive,
        clientIds: selectedClientIds,
      };

      await onSubmit(payload);
      onClose();
    } catch (err) {
      console.error('Failed to save company', err);
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async () => {
    if (!company || !onDelete) return;
    const ok = window.confirm(t('confirmDelete') || 'Удалить запись?');
    if (!ok) return;
    try {
      setDeleting(true);
      await onDelete(company.id);
      onClose();
    } catch (err) {
      console.error('Failed to delete company', err);
    } finally {
      setDeleting(false);
    }
  };

  const modal = (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center p-4 z-[1000]">
      <div className="bg-white rounded-xl max-w-lg w-full max-h-[90vh] overflow-y-auto" role="dialog" aria-modal="true">
        <div className="p-6 space-y-6">
          <div className="flex items-center justify-between">
            <h2 className="text-xl font-semibold text-gray-900">
              {company ? t('editCompany') : t('addCompany')}
            </h2>
            <button
              type="button"
              onClick={onClose}
              className="p-2 hover:bg-gray-100 rounded-lg transition-colors"
              aria-label={t('cancel') || 'Закрыть'}>
              <X size={20} />
            </button>
          </div>

          <form onSubmit={handleSubmit} className="space-y-4" action="#">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('fullName')} *</label>
              <input
                type="text"
                name="fullName"
                required
                value={formData.fullName}
                onChange={handleChange}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('shortName')} *</label>
              <input
                type="text"
                name="shortName"
                required
                value={formData.shortName}
                onChange={handleChange}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('inn')}</label>
              <div className="flex flex-col gap-2 sm:flex-row sm:items-start">
                <input
                  type="text"
                  name="inn"
                  value={formData.inn}
                  onChange={handleChange}
                  className="flex-1 rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                />
                <button
                  type="button"
                  onClick={handleLookupByInn}
                  disabled={lookupLoading || !formData.inn.trim()}
                  className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-60">
                  {lookupLoading ? (
                    <Loader2 className="h-4 w-4 animate-spin" />
                  ) : (
                    <Sparkles className="h-4 w-4" />
                  )}
                  <span>{t('lookupFromDadata') || 'Заполнить по ИНН'}</span>
                </button>
              </div>
              <p className={`mt-1 text-xs ${lookupError ? 'text-red-600' : 'text-gray-500'}`}>
                {lookupError ??
                  (t('dadataFillHint') ||
                    'Данные будут подставлены в форму, сохраните карточку для фиксации.')}
              </p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('kpp')}</label>
              <input
                type="text"
                name="kpp"
                value={formData.kpp}
                onChange={handleChange}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('actualAddress')}</label>
              <textarea
                name="actualAddress"
                value={formData.actualAddress}
                onChange={handleChange}
                rows={2}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('legalAddress')}</label>
              <textarea
                name="legalAddress"
                value={formData.legalAddress}
                onChange={handleChange}
                rows={2}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('notes')}</label>
              <textarea
                name="notes"
                value={formData.notes}
                onChange={handleChange}
                rows={3}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                placeholder={t('additionalInfo')}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('clients')}</label>
              {selectedClients.length > 0 ? (
                <div className="mb-3 flex flex-wrap gap-2">
                  {selectedClients.map((clientItem) => (
                    <span
                      key={clientItem.id}
                      className="inline-flex items-center gap-1 rounded-full border border-blue-100 bg-blue-50 px-3 py-1 text-sm text-blue-700">
                      <span>{clientItem.name}</span>
                      <button
                        type="button"
                        onClick={() => toggleClient(clientItem.id)}
                        className="rounded-full p-1 hover:bg-blue-100"
                        aria-label={t('delete') || 'Удалить'}>
                        <X size={12} />
                      </button>
                    </span>
                  ))}
                </div>
              ) : (
                <p className="mb-3 text-sm text-gray-500">
                  {t('noClientsSelected') || t('selectClients')}
                </p>
              )}

              <div className="flex flex-col gap-2">
                <button
                  type="button"
                  onClick={() => setShowClientsDictionary((prev) => !prev)}
                  className="inline-flex items-center gap-2 self-start rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50">
                  {showClientsDictionary ? (
                    <ChevronUp className="h-4 w-4" />
                  ) : (
                    <ChevronDown className="h-4 w-4" />
                  )}
                  {showClientsDictionary
                    ? t('hideDictionary') || 'Скрыть справочник'
                    : t('openClientsDictionary') || 'Открыть справочник'}
                </button>

                {showClientsDictionary && (
                  <div className="max-h-48 overflow-y-auto rounded-lg border border-gray-200 bg-white shadow-sm">
                    {sortedClients.length === 0 ? (
                      <p className="p-3 text-sm text-gray-500">{t('noClientsYet')}</p>
                    ) : (
                      sortedClients.map((clientItem) => {
                        const isSelected = selectedClientIds.includes(clientItem.id);
                        return (
                          <button
                            type="button"
                            key={clientItem.id}
                            onClick={() => toggleClient(clientItem.id)}
                            className={`flex w-full items-center justify-between gap-3 px-4 py-2 text-left text-sm transition ${
                              isSelected ? 'bg-blue-50 text-blue-700' : 'hover:bg-gray-50'
                            }`}>
                            <span>
                              <span className="block font-medium text-gray-900">{clientItem.name}</span>
                              {clientItem.phone ? (
                                <span className="text-xs text-gray-500">{clientItem.phone}</span>
                              ) : null}
                              {clientItem.email ? (
                                <span className="text-xs text-gray-500 block">{clientItem.email}</span>
                              ) : null}
                            </span>
                            {isSelected ? <Check className="h-4 w-4 text-blue-600" /> : null}
                          </button>
                        );
                      })
                    )}
                  </div>
                )}
              </div>

              <p className="mt-1 text-xs text-gray-500">
                {t('selectClientsFromDictionaryHint') || t('selectClients')}
              </p>
            </div>

            <div className="flex items-center">
              <input
                type="checkbox"
                name="isActive"
                checked={formData.isActive}
                onChange={handleChange}
                className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
              />
              <span className="ml-2 text-sm text-gray-700">{t('activeCompany') ?? t('activeClient')}</span>
            </div>

            <div className="flex justify-between items-center pt-4">
              {company && onDelete ? (
                <button
                  type="button"
                  onClick={handleDelete}
                  disabled={deleting}
                  className="px-4 py-2 text-sm font-medium text-red-600 hover:text-red-700 disabled:opacity-60">
                  {deleting ? t('deleting') : t('delete')}
                </button>
              ) : (
                <span />
              )}
              <div className="flex gap-3">
                <button
                  type="button"
                  onClick={onClose}
                  className="px-4 py-2 text-sm font-medium text-gray-600 hover:text-gray-800">
                  {t('cancel')}
                </button>
                <button
                  type="submit"
                  disabled={loading}
                  className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-60">
                  {loading ? t('saving') : company ? t('update') : t('addCompany')}
                </button>
              </div>
            </div>
          </form>
        </div>
      </div>
    </div>
  );

  return createPortal(modal, document.body);
}
