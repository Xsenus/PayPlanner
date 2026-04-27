import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { Loader2, Search, X } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useDialog } from '../../hooks/useDialog';
import type { Client, ClientInput, LegalEntityInput } from '../../types';
import { apiService } from '../../services/api';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import {
  applyLegalEntityDadataDetails,
  buildQuickClientPayload,
  isQuickClientModalFieldRequired,
  parseQuickClientModalFormData,
  type QuickClientKind,
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
  const [dadataLoading, setDadataLoading] = useState(false);
  const [dadataMessage, setDadataMessage] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [formData, setFormData] = useState(() => parseQuickClientModalFormData());
  const requirementsSection = formData.kind === 'legal' ? 'clients-modal-legal' : 'clients-modal';
  const { isRequired } = useFormRequirements(requirementsSection, isOpen);

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
    setDadataMessage(null);
  }, [client, isOpen]);

  const fieldLabels: Record<string, string> = {
    Name: 'ФИО',
    LastName: 'Фамилия',
    FirstName: 'Имя',
    MiddleName: 'Отчество',
    Phone: 'Телефон',
    Notes: 'Комментарий',
    Comment: 'Комментарий',
    ShortName: 'Наименование',
    Inn: 'ИНН',
    FullName: 'Полное наименование',
    Kpp: 'КПП',
    Ogrn: 'ОГРН',
    Address: 'Адрес',
    Email: 'Email',
    Director: 'Руководитель',
    Country: 'Страна',
    OrganizationForm: 'Форма организации',
    TaxSystem: 'Система налогообложения',
    Okved: 'ОКВЭД',
    Okpo: 'ОКПО',
    Okato: 'ОКАТО',
    Oktmo: 'ОКТМО',
    Okogu: 'ОКОГУ',
    Okfs: 'ОКФС',
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

  const buildLegalEntityPayload = (): LegalEntityInput => ({
    formRequirementsSection: 'clients-modal-legal',
    shortName: formData.legalName.trim() || formData.legalFullName.trim() || 'Юридическое лицо',
    fullName: formData.legalFullName.trim() || null,
    inn: formData.legalInn.trim() || null,
    kpp: formData.legalKpp.trim() || null,
    ogrn: formData.legalOgrn.trim() || null,
    address: formData.legalAddress.trim() || null,
    phone: formData.legalPhone.trim() || null,
    email: formData.legalEmail.trim() || null,
    director: formData.legalDirector.trim() || null,
    country: formData.legalCountry.trim() || null,
    organizationForm: formData.legalOrganizationForm.trim() || null,
    taxSystem: formData.legalTaxSystem.trim() || null,
    okved: formData.legalOkved.trim() || null,
    okpo: formData.legalOkpo.trim() || null,
    okato: formData.legalOkato.trim() || null,
    oktmo: formData.legalOktmo.trim() || null,
    okogu: formData.legalOkogu.trim() || null,
    okfs: formData.legalOkfs.trim() || null,
    notes: formData.comment.trim() || null,
    clientIds: [],
  });

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setLoading(true);
    setErrorMessage(null);

    try {
      let legalEntityId = client?.legalEntityId ?? null;
      if (formData.kind === 'legal' && !legalEntityId) {
        const createdLegalEntity = await apiService.createLegalEntity(buildLegalEntityPayload());
        legalEntityId = createdLegalEntity.id;
      }

      await onSubmit(buildQuickClientPayload(formData, client, legalEntityId));
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

  const handleKindChange = (kind: QuickClientKind) => {
    setFormData((prev) => ({ ...prev, kind }));
    setDadataMessage(null);
  };

  const handleFindLegalEntity = async () => {
    const inn = formData.legalInn.trim();
    if (!inn) {
      setDadataMessage('Введите ИНН для поиска.');
      return;
    }

    setDadataLoading(true);
    setDadataMessage(null);
    try {
      const details = await apiService.findLegalEntityById({ query: inn });
      setFormData((prev) => applyLegalEntityDadataDetails(prev, details));
      setDadataMessage('Данные найдены и подставлены.');
    } catch (error) {
      setDadataMessage(extractErrorMessage(error, 'Не удалось найти организацию по ИНН'));
    } finally {
      setDadataLoading(false);
    }
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
  const legalInnRequired = isQuickClientModalFieldRequired('Inn', isRequired);
  const legalNameRequired = isQuickClientModalFieldRequired('ShortName', isRequired);

  const inputClass = 'w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500';

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
              <label className="mb-2 block text-sm font-medium text-gray-700">Тип клиента</label>
              <div className="grid grid-cols-2 gap-2">
                <button
                  type="button"
                  onClick={() => handleKindChange('individual')}
                  className={`rounded-lg border px-3 py-2 text-sm font-medium ${
                    formData.kind === 'individual'
                      ? 'border-blue-500 bg-blue-50 text-blue-700'
                      : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  Физическое лицо
                </button>
                <button
                  type="button"
                  onClick={() => handleKindChange('legal')}
                  className={`rounded-lg border px-3 py-2 text-sm font-medium ${
                    formData.kind === 'legal'
                      ? 'border-blue-500 bg-blue-50 text-blue-700'
                      : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  Юридическое лицо
                </button>
              </div>
            </div>

            {formData.kind === 'individual' ? (
              <>
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
                    className={inputClass}
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
                    className={inputClass}
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
                    className={inputClass}
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
                    className={inputClass}
                  />
                </div>
              </>
            ) : (
              <>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    ИНН {legalInnRequired ? '*' : ''}
                  </label>
                  <div className="flex gap-2">
                    <input
                      type="text"
                      name="legalInn"
                      value={formData.legalInn}
                      onChange={handleChange}
                      required={legalInnRequired}
                      inputMode="numeric"
                      className="min-w-0 flex-1 rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                    />
                    <button
                      type="button"
                      onClick={() => void handleFindLegalEntity()}
                      disabled={dadataLoading}
                      className="inline-flex items-center justify-center rounded-lg border border-blue-200 bg-blue-50 px-3 text-blue-700 hover:bg-blue-100 disabled:opacity-60"
                      title="Найти по ИНН"
                    >
                      {dadataLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                    </button>
                  </div>
                  {dadataMessage ? <div className="mt-1 text-xs text-gray-500">{dadataMessage}</div> : null}
                </div>

                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    Наименование {legalNameRequired ? '*' : ''}
                  </label>
                  <input
                    type="text"
                    name="legalName"
                    value={formData.legalName}
                    onChange={handleChange}
                    required={legalNameRequired}
                    className={inputClass}
                  />
                </div>

                {(formData.legalKpp || formData.legalOgrn || formData.legalAddress || formData.legalOkved) && (
                  <div className="rounded-lg border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-600">
                    {formData.legalKpp ? <div>КПП: {formData.legalKpp}</div> : null}
                    {formData.legalOgrn ? <div>ОГРН: {formData.legalOgrn}</div> : null}
                    {formData.legalOkved ? <div>ОКВЭД: {formData.legalOkved}</div> : null}
                    {formData.legalAddress ? <div>Адрес: {formData.legalAddress}</div> : null}
                  </div>
                )}
              </>
            )}

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
                className={inputClass}
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
                  {deleting ? t('deleting') || 'Удаление...' : t('delete') || 'Удалить'}
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
