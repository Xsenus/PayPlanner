import React, { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type { Client, ClientPayload, Company } from '../../types';

interface ClientModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (client: ClientPayload) => Promise<void>;
  onDelete?: (id: number) => Promise<void>;
  client?: Client;
  availableCompanies: Company[];
}

export function ClientModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  client,
  availableCompanies,
}: ClientModalProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [formData, setFormData] = useState({
    name: '',
    email: '',
    phone: '',
    company: '',
    address: '',
    notes: '',
    isActive: true,
  });
  const [selectedCompanyIds, setSelectedCompanyIds] = useState<number[]>([]);

  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen, onClose]);

  useEffect(() => {
    if (client) {
      setFormData({
        name: client.name,
        email: client.email || '',
        phone: client.phone || '',
        company: client.company || '',
        address: client.address || '',
        notes: client.notes || '',
        isActive: client.isActive,
      });
      setSelectedCompanyIds((client.companies ?? []).map((c) => c.id));
    } else {
      setFormData({
        name: '',
        email: '',
        phone: '',
        company: '',
        address: '',
        notes: '',
        isActive: true,
      });
      setSelectedCompanyIds([]);
    }
  }, [client]);

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setLoading(true);
    try {
      await onSubmit({ ...formData, companyIds: selectedCompanyIds });
      onClose();
    } catch (error) {
      console.error('Failed to save client:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    const { name, value } = e.target;
    const el = e.target as HTMLInputElement;
    setFormData((prev) => ({
      ...prev,
      [name]: el.type === 'checkbox' ? el.checked : value,
    }));
  };

  const toggleCompany = (companyId: number) => {
    setSelectedCompanyIds((prev) =>
      prev.includes(companyId)
        ? prev.filter((id) => id !== companyId)
        : [...prev, companyId],
    );
  };

  const handleDelete = async () => {
    if (!client || !onDelete) return;
    const ok = window.confirm(
      t('confirmDeleteClient') || 'Удалить клиента? Это действие необратимо.',
    );
    if (!ok) return;

    try {
      setDeleting(true);
      await onDelete(client.id);
      // onClose вызывается снаружи (в потоке родителя после успешного удаления)
    } catch (e) {
      console.error('Failed to delete client:', e);
    } finally {
      setDeleting(false);
    }
  };

  if (!isOpen) return null;

  const modal = (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center p-4 z-[1000]">
      <div
        className="bg-white rounded-xl max-w-md w-full max-h-[90vh] overflow-y-auto"
        role="dialog"
        aria-modal="true">
        <div className="p-6">
          <div className="flex items-center justify-between mb-6">
            <h2 className="text-xl font-semibold text-gray-900">
              {client ? t('editClient') : t('addClient')}
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
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('name')} *</label>
              <input
                type="text"
                name="name"
                value={formData.name}
                onChange={handleChange}
                required
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('email')}</label>
              <input
                type="email"
                name="email"
                value={formData.email}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('phone')}</label>
              <input
                type="tel"
                name="phone"
                value={formData.phone}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('company')}</label>
              <input
                type="text"
                name="company"
                value={formData.company}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('address')}</label>
              <input
                type="text"
                name="address"
                value={formData.address}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('notes')}</label>
              <textarea
                name="notes"
                value={formData.notes}
                onChange={handleChange}
                rows={3}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={t('additionalInfo')}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('companies')}</label>
              {availableCompanies.length === 0 ? (
                <p className="text-sm text-gray-500 bg-gray-50 border border-dashed border-gray-300 rounded-lg p-3">
                  {t('noCompaniesYet')}
                </p>
              ) : (
                <div className="max-h-40 overflow-y-auto border border-gray-200 rounded-lg divide-y">
                  {availableCompanies.map((company) => {
                    const checked = selectedCompanyIds.includes(company.id);
                    return (
                      <label
                        key={company.id}
                        className="flex items-start gap-3 px-3 py-2 hover:bg-gray-50 cursor-pointer">
                        <input
                          type="checkbox"
                          className="mt-1 h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                          checked={checked}
                          onChange={() => toggleCompany(company.id)}
                        />
                        <span className="text-sm text-gray-700">
                          <span className="font-medium text-gray-900 block">{company.name}</span>
                          {company.email ? (
                            <span className="text-xs text-gray-500 block">{company.email}</span>
                          ) : null}
                          {company.phone ? (
                            <span className="text-xs text-gray-500 block">{company.phone}</span>
                          ) : null}
                        </span>
                      </label>
                    );
                  })}
                </div>
              )}
              <p className="text-xs text-gray-500 mt-1">{t('selectCompanies')}</p>
            </div>

            <div className="flex items-center">
              <input
                type="checkbox"
                name="isActive"
                checked={formData.isActive}
                onChange={handleChange}
                className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                id="client-is-active"
              />
              <label htmlFor="client-is-active" className="ml-2 block text-sm text-gray-700">
                {t('activeClient')}
              </label>
            </div>

            <div className="flex gap-3 pt-4">
              {client && onDelete && (
                <button
                  type="button"
                  onClick={handleDelete}
                  disabled={deleting || loading}
                  className="px-4 py-2 bg-red-600 text-white rounded-lg font-medium hover:bg-red-700 transition-colors disabled:opacity-50">
                  {deleting ? t('deleting') || 'Удаление…' : t('delete') || 'Удалить'}
                </button>
              )}
              <button
                type="submit"
                disabled={loading}
                className="flex-1 py-2 px-4 bg-blue-600 text-white rounded-lg font-medium hover:bg-blue-700 transition-colors disabled:opacity-50">
                {loading ? t('saving') : client ? t('update') : t('addClient')}
              </button>
              <button
                type="button"
                onClick={onClose}
                className="flex-1 py-2 px-4 bg-gray-200 text-gray-800 rounded-lg font-medium hover:bg-gray-300 transition-colors">
                {t('cancel')}
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );

  return createPortal(modal, document.body);
}
