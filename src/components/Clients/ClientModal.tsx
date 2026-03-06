import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useDialog } from '../../hooks/useDialog';
import type { Client, ClientInput } from '../../types';
import { useFormRequirements } from '../../hooks/useFormRequirements';

interface ClientModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (client: ClientInput) => Promise<void>;
  onDelete?: (id: number) => Promise<void>;
  client?: Client;
}

export function ClientModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  client,
}: ClientModalProps) {
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const { isRequired } = useFormRequirements('clients-modal', isOpen);
  const [formData, setFormData] = useState({
    name: '',
    email: '',
    phone: '',
    notes: '',
  });

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
        notes: client.notes || '',
      });
    } else {
      setFormData({
        name: '',
        email: '',
        phone: '',
        notes: '',
      });
    }
    setErrorMessage(null);
  }, [client]);

  const fieldLabels: Record<string, string> = {
    Name: 'Имя',
    Email: 'Email',
    Phone: 'Телефон',
    Notes: 'Комментарий',
  };

  const extractErrorMessage = (error: unknown, fallback: string) => {
    const raw = error instanceof Error ? error.message : String(error);
    const marker = ' - ';
    const markerIndex = raw.indexOf(marker);
    const payload = markerIndex >= 0 ? raw.slice(markerIndex + marker.length).trim() : raw;
    try {
      const parsed = JSON.parse(payload) as { message?: string; missingFields?: string[] };
      const base = parsed.message?.trim() || fallback;
      const missing = Array.isArray(parsed.missingFields) ? parsed.missingFields : [];
      if (missing.length === 0) return base;
      const mapped = missing.map((f) => fieldLabels[f] ?? f).join(', ');
      return `${base}: ${mapped}`;
    } catch {
      return raw || fallback;
    }
  };

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setLoading(true);
    setErrorMessage(null);
    try {
      const baseClient = client ?? ({} as Client);
      const payload: ClientInput = {
        name: formData.name.trim(),
        email: formData.email.trim(),
        phone: formData.phone.trim(),
        notes: formData.notes.trim(),
        company: baseClient.company ?? '',
        address: baseClient.address ?? '',
        isActive: baseClient.isActive ?? true,
        legalEntityId: baseClient.legalEntityId ?? null,
        clientStatusId: baseClient.clientStatusId ?? null,
      };
      await onSubmit(payload);
      onClose();
    } catch (error) {
      setErrorMessage(extractErrorMessage(error, 'Не удалось сохранить клиента'));
    } finally {
      setLoading(false);
    }
  };

  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const { name, value } = e.target;
    const el = e.target as HTMLInputElement;
    setFormData((prev) => ({
      ...prev,
      [name]: el.type === 'checkbox' ? el.checked : value,
    }));
  };

  const handleDelete = async () => {
    if (!client || !onDelete) return;
    const ok = await confirm({
      title: 'Подтверждение удаления',
      description: t('confirmDeleteClient') || 'Удалить клиента? Это действие необратимо.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
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
            {errorMessage && (
              <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                {errorMessage}
              </div>
            )}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('name')} {isRequired('Name') ? '*' : ''}</label>
              <input
                type="text"
                name="name"
                value={formData.name}
                onChange={handleChange}
                required
                className={`w-full px-3 py-2 border rounded-lg focus:ring-2 ${isRequired('Name') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('email')} {isRequired('Email') ? '*' : ''}</label>
              <input
                type="email"
                name="email"
                value={formData.email}
                onChange={handleChange}
                className={`w-full px-3 py-2 border rounded-lg focus:ring-2 ${isRequired('Email') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('phone')} {isRequired('Phone') ? '*' : ''}</label>
              <input
                type="tel"
                name="phone"
                value={formData.phone}
                onChange={handleChange}
                className={`w-full px-3 py-2 border rounded-lg focus:ring-2 ${isRequired('Phone') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">Комментарий</label>
              <textarea
                name="notes"
                value={formData.notes}
                onChange={handleChange}
                rows={3}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={t('additionalInfo')}
              />
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
