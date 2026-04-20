import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useDialog } from '../../hooks/useDialog';
import type { Client, ClientInput } from '../../types';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import {
  buildQuickClientPayload,
  isQuickClientModalFieldRequired,
  parseQuickClientModalFormData,
} from './clientQuickModalUtils';

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
  const [formData, setFormData] = useState(() => parseQuickClientModalFormData());

  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen, onClose]);

  useEffect(() => {
    setFormData(parseQuickClientModalFormData(client));
    setErrorMessage(null);
  }, [client, isOpen]);

  const fieldLabels: Record<string, string> = {
    Name: 'ФИО',
    LastName: 'Фамилия',
    FirstName: 'Имя',
    MiddleName: 'Отчество',
    Phone: 'Телефон',
    Notes: 'Комментарий',
    Comment: 'Комментарий',
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
      const mapped = missing.map((field) => fieldLabels[field] ?? field).join(', ');
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
      await onSubmit(buildQuickClientPayload(formData, client));
      onClose();
    } catch (error) {
      setErrorMessage(extractErrorMessage(error, 'Не удалось сохранить клиента'));
    } finally {
      setLoading(false);
    }
  };

  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>,
  ) => {
    const { name, value } = e.target;
    setFormData((prev) => ({
      ...prev,
      [name]: value,
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
    } catch (error) {
      console.error('Failed to delete client:', error);
    } finally {
      setDeleting(false);
    }
  };

  if (!isOpen) return null;

  const lastNameRequired = isQuickClientModalFieldRequired('LastName', isRequired);
  const firstNameRequired = isQuickClientModalFieldRequired('FirstName', isRequired);
  const middleNameRequired = isQuickClientModalFieldRequired('MiddleName', isRequired);
  const phoneRequired = isQuickClientModalFieldRequired('Phone', isRequired);
  const commentRequired = isQuickClientModalFieldRequired('Comment', isRequired);

  const modal = (
    <div className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/50 p-4">
      <div
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-xl bg-white"
        role="dialog"
        aria-modal="true"
      >
        <div className="p-6">
          <div className="mb-6 flex items-center justify-between">
            <h2 className="text-xl font-semibold text-gray-900">
              {client ? t('editClient') : t('addClient')}
            </h2>
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg p-2 transition-colors hover:bg-gray-100"
              aria-label={t('cancel') || 'Закрыть'}
            >
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
              <label className="mb-2 block text-sm font-medium text-gray-700">
                Фамилия {lastNameRequired ? '*' : ''}
              </label>
              <input
                type="text"
                name="lastName"
                value={formData.lastName}
                onChange={handleChange}
                required={lastNameRequired}
                className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${
                  lastNameRequired
                    ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-gray-700">
                Имя {firstNameRequired ? '*' : ''}
              </label>
              <input
                type="text"
                name="firstName"
                value={formData.firstName}
                onChange={handleChange}
                required={firstNameRequired}
                className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${
                  firstNameRequired
                    ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-gray-700">
                Отчество {middleNameRequired ? '*' : ''}
              </label>
              <input
                type="text"
                name="middleName"
                value={formData.middleName}
                onChange={handleChange}
                required={middleNameRequired}
                className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${
                  middleNameRequired
                    ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-gray-700">
                {t('phone')} {phoneRequired ? '*' : ''}
              </label>
              <input
                type="tel"
                name="phone"
                value={formData.phone}
                onChange={handleChange}
                required={phoneRequired}
                inputMode="tel"
                className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${
                  phoneRequired
                    ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-gray-700">
                Комментарий {commentRequired ? '*' : ''}
              </label>
              <textarea
                name="comment"
                value={formData.comment}
                onChange={handleChange}
                rows={3}
                required={commentRequired}
                className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${
                  commentRequired
                    ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder={t('additionalInfo')}
              />
            </div>

            <div className="flex gap-3 pt-4">
              {client && onDelete && (
                <button
                  type="button"
                  onClick={handleDelete}
                  disabled={deleting || loading}
                  className="rounded-lg bg-red-600 px-4 py-2 font-medium text-white transition-colors hover:bg-red-700 disabled:opacity-50"
                >
                  {deleting ? t('deleting') || 'Удаление…' : t('delete') || 'Удалить'}
                </button>
              )}
              <button
                type="submit"
                disabled={loading}
                className="flex-1 rounded-lg bg-blue-600 px-4 py-2 font-medium text-white transition-colors hover:bg-blue-700 disabled:opacity-50"
              >
                {loading ? t('saving') : client ? t('update') : t('addClient')}
              </button>
              <button
                type="button"
                onClick={onClose}
                className="flex-1 rounded-lg bg-gray-200 px-4 py-2 font-medium text-gray-800 transition-colors hover:bg-gray-300"
              >
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
