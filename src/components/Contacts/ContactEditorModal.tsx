import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { useEffect, useMemo, useState } from 'react';
import { Loader2, Save, X } from 'lucide-react';
import { apiService, type ContactDirectoryItemDto, type UpsertExternalContactRequestDto, type UpdateClientContactRequestDto } from '../../services/api';
import type { ActResponsible, LegalEntitySummary } from '../../types';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';

type ContactEditorMode = 'create-external' | 'edit-client' | 'edit-external';

interface ContactEditorModalProps {
  open: boolean;
  mode: ContactEditorMode;
  contact: ContactDirectoryItemDto | null;
  legalEntities: LegalEntitySummary[];
  responsibles: ActResponsible[];
  canManageResponsible: boolean;
  onClose: () => void;
  onSaved: (contact: ContactDirectoryItemDto) => void;
}

type ContactFormState = {
  fullName: string;
  phonesText: string;
  emailsText: string;
  position: string;
  powerOfAttorney: string;
  notes: string;
  legalEntityId: string;
  syncToPhonebook: boolean;
  isActive: boolean;
  responsibleUserId: string;
};

const DEFAULT_FORM: ContactFormState = {
  fullName: '',
  phonesText: '',
  emailsText: '',
  position: '',
  powerOfAttorney: '',
  notes: '',
  legalEntityId: '',
  syncToPhonebook: true,
  isActive: true,
  responsibleUserId: '',
};

function toMultiline(value: string[]) {
  return value.join('\n');
}

function parseMultiline(value: string) {
  return Array.from(
    new Set(
      value
        .split(/\r?\n|,|;/)
        .map((item) => item.trim())
        .filter(Boolean),
    ),
  );
}

function requiredMark(required: boolean) {
  return required ? <span className="ml-1 text-rose-500">*</span> : null;
}

export function ContactEditorModal({
  open,
  mode,
  contact,
  legalEntities,
  responsibles,
  canManageResponsible,
  onClose,
  onSaved,
}: ContactEditorModalProps) {
  const toast = useToast();
  const requirementsSection = mode === 'edit-client' ? 'contacts-client' : 'contacts-external';
  const { isRequired } = useFormRequirements(requirementsSection, open);
  const [form, setForm] = useState<ContactFormState>(DEFAULT_FORM);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    if (!contact) {
      setForm(DEFAULT_FORM);
      setError(null);
      return;
    }

    setForm({
      fullName: contact.fullName ?? '',
      phonesText: toMultiline(contact.phones ?? []),
      emailsText: toMultiline(contact.emails ?? []),
      position: contact.position ?? '',
      powerOfAttorney: contact.powerOfAttorney ?? '',
      notes: contact.notes ?? '',
      legalEntityId: contact.legalEntityId ? String(contact.legalEntityId) : '',
      syncToPhonebook: contact.syncToPhonebook,
      isActive: contact.isActive,
      responsibleUserId: contact.responsibleUserId ? String(contact.responsibleUserId) : '',
    });
    setError(null);
  }, [contact, open]);

  const title = useMemo(() => {
    if (mode === 'create-external') {
      return 'Новый сторонний контакт';
    }

    return mode === 'edit-client' ? 'Контакт клиента' : 'Сторонний контакт';
  }, [mode]);

  if (!open) {
    return null;
  }

  const phones = parseMultiline(form.phonesText);
  const emails = parseMultiline(form.emailsText);

  const save = async () => {
    if (!form.fullName.trim()) {
      const message = 'Укажите ФИО контакта';
      setError(message);
      toast.error(message, 'Контакты');
      return;
    }

    setSaving(true);
    setError(null);

    try {
      let saved: ContactDirectoryItemDto;

      if (mode === 'edit-client' && contact) {
        const payload: UpdateClientContactRequestDto = {
          fullName: form.fullName.trim(),
          phone: phones[0] ?? null,
          email: emails[0] ?? null,
          phones,
          emails,
          position: form.position.trim() || null,
          powerOfAttorney: form.powerOfAttorney.trim() || null,
          notes: form.notes.trim() || null,
          legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
          syncToPhonebook: form.syncToPhonebook,
        };

        saved = await apiService.updateClientContact(contact.sourceId, payload);
      } else {
        const payload: UpsertExternalContactRequestDto = {
          fullName: form.fullName.trim(),
          phone: phones[0] ?? null,
          email: emails[0] ?? null,
          phones,
          emails,
          position: form.position.trim() || null,
          powerOfAttorney: form.powerOfAttorney.trim() || null,
          notes: form.notes.trim() || null,
          legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
          responsibleUserId: form.responsibleUserId ? Number(form.responsibleUserId) : null,
          isActive: form.isActive,
          syncToPhonebook: form.syncToPhonebook,
        };

        saved =
          mode === 'edit-external' && contact
            ? await apiService.updateExternalContact(contact.sourceId, payload)
            : await apiService.createExternalContact(payload);
      }

      onSaved(saved);
      toast.success('Контакт сохранён', 'Контакты');
      onClose();
    } catch (nextError) {
      const message = extractDisplayErrorMessage(nextError, 'Не удалось сохранить контакт');
      setError(message);
      toast.error(message, 'Контакты');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-[120] flex items-center justify-center bg-slate-950/45 px-4 py-8">
      <div className="flex max-h-[90vh] w-full max-w-4xl flex-col overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 px-6 py-5">
          <div>
            <h3 className="text-2xl font-semibold text-slate-900">{title}</h3>
            <p className="mt-1 text-sm text-slate-500">
              Редактирование контактных данных и параметров синхронизации с телефонией.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-900"
            aria-label="Закрыть"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="overflow-y-auto px-6 py-6">
          <div className="grid gap-5 md:grid-cols-2">
            <label className="block">
              <span className="text-sm font-medium text-slate-700">
                ФИО
                {requiredMark(isRequired('FullName'))}
              </span>
              <input
                type="text"
                value={form.fullName}
                onChange={(event) => setForm((prev) => ({ ...prev, fullName: event.target.value }))}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                placeholder="Например: Иванов Иван Иванович"
              />
            </label>

            <label className="block">
              <span className="text-sm font-medium text-slate-700">
                Организация
                {requiredMark(isRequired('LegalEntityId'))}
              </span>
              <SystemSelect
                value={form.legalEntityId}
                onChange={(event) => setForm((prev) => ({ ...prev, legalEntityId: event.target.value }))}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
              >
                <option value="">Без привязки</option>
                {legalEntities.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.shortName}
                  </option>
                ))}
              </SystemSelect>
            </label>

            <label className="block">
              <span className="text-sm font-medium text-slate-700">
                Телефоны
                {requiredMark(isRequired('Phones'))}
              </span>
              <textarea
                value={form.phonesText}
                onChange={(event) => setForm((prev) => ({ ...prev, phonesText: event.target.value }))}
                rows={4}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                placeholder="По одному номеру в строке"
              />
            </label>

            <label className="block">
              <span className="text-sm font-medium text-slate-700">
                Email
                {requiredMark(isRequired('Emails'))}
              </span>
              <textarea
                value={form.emailsText}
                onChange={(event) => setForm((prev) => ({ ...prev, emailsText: event.target.value }))}
                rows={4}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                placeholder="По одному email в строке"
              />
            </label>

            <label className="block">
              <span className="text-sm font-medium text-slate-700">
                Должность
                {requiredMark(isRequired('Position'))}
              </span>
              <input
                type="text"
                value={form.position}
                onChange={(event) => setForm((prev) => ({ ...prev, position: event.target.value }))}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                placeholder="Например: директор"
              />
            </label>

            <label className="block">
              <span className="text-sm font-medium text-slate-700">
                Доверенность
                {requiredMark(isRequired('PowerOfAttorney'))}
              </span>
              <input
                type="text"
                value={form.powerOfAttorney}
                onChange={(event) => setForm((prev) => ({ ...prev, powerOfAttorney: event.target.value }))}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                placeholder="Сведения о доверенности"
              />
            </label>

            {mode !== 'edit-client' && canManageResponsible ? (
              <label className="block">
                <span className="text-sm font-medium text-slate-700">
                  Ответственный
                  {requiredMark(isRequired('ResponsibleUserId'))}
                </span>
                <SystemSelect
                  value={form.responsibleUserId}
                  onChange={(event) => setForm((prev) => ({ ...prev, responsibleUserId: event.target.value }))}
                  className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                >
                  <option value="">Текущий пользователь</option>
                  {responsibles.map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.fullName}
                    </option>
                  ))}
                </SystemSelect>
              </label>
            ) : (
              <div className="hidden md:block" />
            )}

            <label className="block md:col-span-2">
              <span className="text-sm font-medium text-slate-700">Комментарий</span>
              <textarea
                value={form.notes}
                onChange={(event) => setForm((prev) => ({ ...prev, notes: event.target.value }))}
                rows={3}
                className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                placeholder="Дополнительные примечания по контакту"
              />
            </label>
          </div>

          <div className="mt-6 grid gap-3 md:grid-cols-2">
            {mode !== 'edit-client' ? (
              <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                <SystemCheckbox
                  type="checkbox"
                  checked={form.isActive}
                  onChange={(event) => setForm((prev) => ({ ...prev, isActive: event.target.checked }))}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                Активный контакт
              </label>
            ) : null}

            <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
              <SystemCheckbox
                type="checkbox"
                checked={form.syncToPhonebook}
                onChange={(event) => setForm((prev) => ({ ...prev, syncToPhonebook: event.target.checked }))}
                className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
              />
              Участвует в синхронизации Yealink
            </label>
          </div>

          {contact ? (
            <div className="mt-5">
              <ObjectAuditLauncher
                objectType={contact.sourceType === 'client' ? 'client' : 'contact'}
                objectId={contact.sourceId}
                createdAt={contact.createdAt}
                createdByUserName={contact.auditCreatedByUserName}
                updatedAt={contact.auditUpdatedAt}
                updatedByUserName={contact.auditUpdatedByUserName}
                isDeleted={contact.auditIsDeleted}
                deletedAt={contact.auditDeletedAt}
                deletedByUserName={contact.auditDeletedByUserName}
                triggerTitle="Аудит контакта"
                triggerDescription="Паспорт контакта и история изменений открываются в отдельном окне."
                title="История контакта"
              />
            </div>
          ) : null}

          {error ? (
            <div className="mt-5 rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-4">
          <button
            type="button"
            onClick={onClose}
            className="rounded-xl border border-slate-300 px-4 py-2.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={save}
            disabled={saving}
            className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Сохранить
          </button>
        </div>
      </div>
    </div>
  );
}
