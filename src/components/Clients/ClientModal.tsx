import React, { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type { Client, ClientInput, LegalEntitySummary, ClientStatus } from '../../types';

interface ClientModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (client: ClientInput) => Promise<void>;
  onDelete?: (id: number) => Promise<void>;
  client?: Client;
  legalEntities: LegalEntitySummary[];
  clientStatuses: ClientStatus[];
}

export function ClientModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  client,
  legalEntities,
  clientStatuses,
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
    legalEntityId: '',
    isActive: true,
    clientStatusId: '',
  });

  const sortedLegalEntities = useMemo(
    () =>
      [...legalEntities].sort((a, b) =>
        a.shortName.localeCompare(b.shortName, undefined, { sensitivity: 'base' }),
      ),
    [legalEntities],
  );

  const selectedLegalEntity = useMemo(() => {
    if (!formData.legalEntityId) return null;
    return sortedLegalEntities.find((entity) => entity.id === Number(formData.legalEntityId)) ?? null;
  }, [formData.legalEntityId, sortedLegalEntities]);

  const sortedClientStatuses = useMemo(
    () =>
      [...clientStatuses].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      ),
    [clientStatuses],
  );

  const selectedClientStatus = useMemo(() => {
    if (!formData.clientStatusId) return null;
    return (
      sortedClientStatuses.find((status) => status.id === Number(formData.clientStatusId)) ?? null
    );
  }, [formData.clientStatusId, sortedClientStatuses]);

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
        legalEntityId: client.legalEntityId ? String(client.legalEntityId) : '',
        isActive: client.isActive,
        clientStatusId: client.clientStatusId ? String(client.clientStatusId) : '',
      });
    } else {
      setFormData({
        name: '',
        email: '',
        phone: '',
        company: '',
        address: '',
        notes: '',
        legalEntityId: '',
        isActive: true,
        clientStatusId: '',
      });
    }
  }, [client]);

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setLoading(true);
    try {
      const payload: ClientInput = {
        name: formData.name.trim(),
        email: formData.email.trim(),
        phone: formData.phone.trim(),
        company: formData.company.trim(),
        address: formData.address.trim(),
        notes: formData.notes.trim(),
        isActive: formData.isActive,
        legalEntityId: formData.legalEntityId ? Number(formData.legalEntityId) : null,
        clientStatusId: formData.clientStatusId ? Number(formData.clientStatusId) : null,
      };
      await onSubmit(payload);
      onClose();
    } catch (error) {
      console.error('Failed to save client:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const { name, value } = e.target;
    if (name === 'legalEntityId') {
      setFormData((prev) => ({ ...prev, legalEntityId: value }));
      return;
    }
    if (name === 'clientStatusId') {
      setFormData((prev) => ({ ...prev, clientStatusId: value }));
      return;
    }
    const el = e.target as HTMLInputElement;
    setFormData((prev) => ({
      ...prev,
      [name]: el.type === 'checkbox' ? el.checked : value,
    }));
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
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('clientStatus') || 'Статус клиента'}
              </label>
              <select
                name="clientStatusId"
                value={formData.clientStatusId}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('clientStatusNotSelected') || 'Без статуса'}</option>
                {sortedClientStatuses.map((status) => (
                  <option key={status.id} value={status.id}>
                    {status.name}
                  </option>
                ))}
              </select>
              {selectedClientStatus?.description && (
                <p className="mt-2 text-xs text-gray-500">{selectedClientStatus.description}</p>
              )}
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('legalEntity') || 'Юридическое лицо'}
              </label>
              <select
                name="legalEntityId"
                value={formData.legalEntityId}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">
                  {t('legalEntityNotSelected') || 'Без юридического лица'}
                </option>
                {sortedLegalEntities.map((entity) => (
                  <option key={entity.id} value={entity.id}>
                    {entity.shortName}
                    {entity.inn ? ` · ${entity.inn}` : ''}
                  </option>
                ))}
              </select>
              {selectedLegalEntity && (
                <div className="mt-3 rounded-lg bg-gray-50 p-3 text-sm text-gray-600">
                  {selectedLegalEntity.fullName && (
                    <p className="font-medium text-gray-700">{selectedLegalEntity.fullName}</p>
                  )}
                  <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500">
                    {selectedLegalEntity.inn && <span>ИНН: {selectedLegalEntity.inn}</span>}
                    {selectedLegalEntity.kpp && <span>КПП: {selectedLegalEntity.kpp}</span>}
                    {selectedLegalEntity.ogrn && <span>ОГРН: {selectedLegalEntity.ogrn}</span>}
                  </div>
                  {selectedLegalEntity.address && (
                    <p className="mt-2 text-xs text-gray-500">
                      {t('legalEntityAddress') || 'Адрес'}: {selectedLegalEntity.address}
                    </p>
                  )}
                </div>
              )}
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
