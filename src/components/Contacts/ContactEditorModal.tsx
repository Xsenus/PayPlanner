import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { useMemo, useState, useRef } from 'react';
import { Loader2, Save, X } from 'lucide-react';
import { apiService, type ContactDirectoryItemDto, type ContactRole, type ContactType, type UpsertExternalContactRequestDto, type UpdateClientContactRequestDto } from '../../services/api';
import type { ActResponsible, LegalEntitySummary } from '../../types';
import { useDocumentDeliveryOwner, type DocumentReadOwner } from '../../hooks/useDocumentDeliveryOwner';
import { captureAuthSessionIdentity } from '../../services/authService';
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
  organizationContacts?: ContactDirectoryItemDto[];
  canManageResponsible: boolean;
  ownerScope?: string;
  editorOwner?: DocumentReadOwner;
  allowSave?: boolean;
  canViewLegalEntities?: boolean;
  legalEntitiesLoading?: boolean;
  legalEntitiesError?: string | null;
  onRetryLegalEntities?: () => void;
  responsiblesLoading?: boolean;
  responsiblesError?: string | null;
  onRetryResponsibles?: () => void;

  onClose: () => void;
  onSaved: (contact: ContactDirectoryItemDto) => void;
}

type ContactFormState = {
  fullName: string;
  contactType: ContactType;
  inn: string;
  roles: ContactRole[];
  phonesText: string;
  emailsText: string;
  position: string;
  powerOfAttorney: string;
  notes: string;
  legalEntityId: string;
  parentContactId: string;
  syncToPhonebook: boolean;
  isActive: boolean;
  responsibleUserId: string;
};

const DEFAULT_FORM: ContactFormState = {
  fullName: '',
  contactType: 'Person',
  inn: '',
  roles: ['Contact'],
  phonesText: '',
  emailsText: '',
  position: '',
  powerOfAttorney: '',
  notes: '',
  legalEntityId: '',
  parentContactId: '',
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

function initialContactForm(contact: ContactDirectoryItemDto | null): ContactFormState {
  if (!contact) return { ...DEFAULT_FORM };
  return {
    fullName: contact.fullName ?? '',
    contactType: contact.contactType ?? 'Person',
    inn: contact.inn ?? '',
    roles: contact.roles?.length ? contact.roles : ['Contact'],
    phonesText: toMultiline(contact.phones ?? []),
    emailsText: toMultiline(contact.emails ?? []),
    position: contact.position ?? '',
    powerOfAttorney: contact.powerOfAttorney ?? '',
    notes: contact.notes ?? '',
    legalEntityId: contact.legalEntityId ? String(contact.legalEntityId) : '',
    parentContactId: contact.parentContactId ? String(contact.parentContactId) : '',
    syncToPhonebook: contact.syncToPhonebook,
    isActive: contact.isActive,
    responsibleUserId: contact.responsibleUserId ? String(contact.responsibleUserId) : '',
  };
}

export function ContactEditorModal(props: ContactEditorModalProps) {
  const identity = useRef({ contact: props.contact, revision: 0 });
  if (identity.current.contact !== props.contact) identity.current = { contact: props.contact, revision: identity.current.revision + 1 };
  const key = JSON.stringify([props.open, props.mode, props.ownerScope, props.editorOwner?.scope, props.allowSave,
    props.canManageResponsible, props.canViewLegalEntities, identity.current.revision, captureAuthSessionIdentity()]);
  return <ContactEditorScreen key={key} {...props} />;
}

function ContactEditorScreen({
  open,
  mode,
  contact,
  legalEntities,
  responsibles,
  organizationContacts = [],
  canManageResponsible,
  ownerScope,
  editorOwner,
  allowSave = true,
  canViewLegalEntities = true,
  legalEntitiesLoading = false,
  legalEntitiesError = null,
  onRetryLegalEntities,
  responsiblesLoading = false,
  responsiblesError = null,
  onRetryResponsibles,
  onClose,
  onSaved,
}: ContactEditorModalProps) {
  const toast = useToast();
  const mutationOwner = useDocumentDeliveryOwner(JSON.stringify([open, ownerScope, editorOwner?.scope, mode, contact?.sourceType, contact?.sourceId, allowSave, canManageResponsible, canViewLegalEntities]));
  const captureCurrent = () => {
    const local = mutationOwner.capture(); const parent = editorOwner?.capture();
    if (!open || !allowSave || !local || (editorOwner && !parent)) return null;
    return { isCurrent: () => local.isCurrent() && (!parent || parent.isCurrent()) };
  };
  const handleClose = () => { if (!open || !mutationOwner.capture() || (editorOwner && !editorOwner.capture())) return; mutationOwner.invalidate(); onClose(); };

  const requirementsSection = mode === 'edit-client' ? 'contacts-client' : 'contacts-external';
  const { isRequired } = useFormRequirements(requirementsSection, open, {
    ownerScope: JSON.stringify([ownerScope, mode, contact?.sourceType, contact?.sourceId, canManageResponsible]),
  });
  const [form, setForm] = useState<ContactFormState>(() => initialContactForm(contact));
  const saving = mutationOwner.busy;
  const [error, setError] = useState<string | null>(null);

  const title = useMemo(() => {
    if (mode === 'create-external') {
      return 'Новый контакт';
    }

    return mode === 'edit-client' ? 'Контакт клиента' : 'Контакт';
  }, [mode]);

  if (!open) {
    return null;
  }

  const phones = parseMultiline(form.phonesText);
  const emails = parseMultiline(form.emailsText);

  const save = async () => {
    const owner = captureCurrent(); if (!owner) return;
    if (!form.fullName.trim()) {
      const message = 'Укажите ФИО контакта';
      setError(message);
      toast.error(message, 'Контакты');
      return;
    }

    const operation = mutationOwner.begin('save'); if (!operation) return;
    const current = () => owner.isCurrent() && operation.isCurrent();
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
          contactType: form.contactType,
          inn: form.contactType === 'Organization' ? form.inn.trim() || null : null,
          roles: form.roles.length ? form.roles : ['Contact'],
          phone: phones[0] ?? null,
          email: emails[0] ?? null,
          phones,
          emails,
          position: form.position.trim() || null,
          powerOfAttorney: form.powerOfAttorney.trim() || null,
          notes: form.notes.trim() || null,
          legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
          parentContactId: form.contactType === 'Person' && form.parentContactId ? Number(form.parentContactId) : null,
          responsibleUserId: form.responsibleUserId ? Number(form.responsibleUserId) : null,
          isActive: form.isActive,
          syncToPhonebook: form.syncToPhonebook,
        };

        saved =
          mode === 'edit-external' && contact
            ? await apiService.updateExternalContact(contact.sourceId, payload)
            : await apiService.createExternalContact(payload);
      }

      if (!current()) return;
      onSaved(saved);
      if (!current()) return;
      toast.success('Контакт сохранён', 'Контакты');
      handleClose();
    } catch (nextError) {
      if (!current()) return;
      const message = extractDisplayErrorMessage(nextError, 'Не удалось сохранить контакт');
      setError(message);
      toast.error(message, 'Контакты');
    } finally {
      operation.finish();
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
            onClick={handleClose}
            className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-900"
            aria-label="Закрыть"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="min-h-0 overflow-y-auto px-6 py-6">
          <fieldset data-testid="contact-editor-fields" disabled={saving || !allowSave || !mutationOwner.capture()} className="m-0 min-w-0 border-0 p-0">
            <div className="grid gap-5 md:grid-cols-2">
              {mode !== 'edit-client' ? (
                <label className="block">
                  <span className="text-sm font-medium text-slate-700">Тип контакта</span>
                  <SystemSelect
                    value={form.contactType}
                    disabled={saving || !allowSave}
                    onChange={(event) => { if (saving || !captureCurrent()) return; setForm((prev) => ({
                      ...prev,
                      contactType: event.target.value as ContactType,
                      parentContactId: event.target.value === 'Organization' ? '' : prev.parentContactId,
                      syncToPhonebook: event.target.value === 'Organization' ? false : prev.syncToPhonebook,
                    })); }}
                    className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm"
                  >
                    <option value="Person">Физическое лицо</option>
                    <option value="Organization">Организация</option>
                  </SystemSelect>
                </label>
              ) : null}

              <label className="block">
                <span className="text-sm font-medium text-slate-700">
                  {form.contactType === 'Organization' ? 'Название организации' : 'ФИО'}
                  {requiredMark(isRequired('FullName'))}
                </span>
                <input
                  type="text"
                  value={form.fullName}
                  onChange={(event) => setForm((prev) => ({ ...prev, fullName: event.target.value }))}
                  className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                  placeholder={form.contactType === 'Organization' ? 'Например: ООО «Ромашка»' : 'Например: Иванов Иван Иванович'}
                />
              </label>

              {mode !== 'edit-client' && form.contactType === 'Organization' ? (
                <label className="block">
                  <span className="text-sm font-medium text-slate-700">ИНН</span>
                  <input
                    type="text"
                    value={form.inn}
                    onChange={(event) => setForm((prev) => ({ ...prev, inn: event.target.value }))}
                    className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm"
                    placeholder="10 или 12 цифр"
                  />
                </label>
              ) : null}

              {form.contactType === 'Person' ? <label className="block">
                <span className="text-sm font-medium text-slate-700">
                  Организация
                  {requiredMark(isRequired('LegalEntityId'))}
                </span>
                <SystemSelect
                  value={form.legalEntityId}
                  data-testid="contact-legal-entity-select"
                  disabled={saving || !allowSave || !canViewLegalEntities || legalEntitiesLoading || Boolean(legalEntitiesError)}
                  onChange={(event) => {
                    if (saving || !canViewLegalEntities || legalEntitiesLoading || legalEntitiesError || !captureCurrent()) return;
                    const value = event.target.value;
                    if (value && !legalEntities.some(item => String(item.id) === value)) return;
                    setForm(prev => ({ ...prev, legalEntityId: value }));
                  }}
                  className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                >
                  <option value="">Без привязки</option>
                  {form.legalEntityId && !legalEntities.some(item => String(item.id) === form.legalEntityId) ? <option value={form.legalEntityId}>Связанная организация</option> : null}
                  {legalEntities.map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.shortName}
                    </option>
                  ))}
                </SystemSelect>
                {legalEntitiesError ? <span role="alert" className="mt-1 block text-xs text-rose-700">{extractDisplayErrorMessage(new Error(legalEntitiesError), 'Не удалось загрузить организации.')} {onRetryLegalEntities ? <button type="button" onClick={onRetryLegalEntities} className="underline">Повторить загрузку организаций</button> : null}</span> : null}
              </label> : null}

              {mode !== 'edit-client' && form.contactType === 'Person' ? (
                <label className="block">
                  <span className="text-sm font-medium text-slate-700">Организация из быстрых контактов</span>
                  <SystemSelect
                    value={form.parentContactId}
                    disabled={saving || !allowSave}
                    onChange={(event) => {
                      if (saving || !captureCurrent()) return;
                      const value = event.target.value;
                      if (value && !organizationContacts.some(item => String(item.sourceId) === value)) return;
                      setForm(prev => ({ ...prev, parentContactId: value }));
                    }}
                    className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm"
                  >
                    <option value="">Без привязки</option>
                    {organizationContacts.filter((item) => item.sourceType === 'external' && item.sourceId !== contact?.sourceId).map((item) => (
                      <option key={item.sourceId} value={item.sourceId}>{item.fullName}</option>
                    ))}
                  </SystemSelect>
                </label>
              ) : null}

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
                    data-testid="contact-responsible-select"
                    disabled={saving || !allowSave || responsiblesLoading || Boolean(responsiblesError)}
                    onChange={(event) => {
                      if (saving || responsiblesLoading || responsiblesError || !captureCurrent()) return;
                      const value = event.target.value;
                      if (value && !responsibles.some(item => String(item.id) === value)) return;
                      setForm(prev => ({ ...prev, responsibleUserId: value }));
                    }}
                    className="mt-1 w-full rounded-xl border border-slate-300 px-4 py-2.5 text-sm outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                  >
                    <option value="">Текущий пользователь</option>
                    {form.responsibleUserId && !responsibles.some(item => String(item.id) === form.responsibleUserId) ? <option value={form.responsibleUserId}>Назначенный ответственный</option> : null}
                    {responsibles.map((item) => (
                      <option key={item.id} value={item.id}>
                        {item.fullName}
                      </option>
                    ))}
                  </SystemSelect>
                  {responsiblesError ? <span role="alert" className="mt-1 block text-xs text-rose-700">{extractDisplayErrorMessage(new Error(responsiblesError), 'Не удалось загрузить ответственных.')} {onRetryResponsibles ? <button type="button" onClick={onRetryResponsibles} className="underline">Повторить загрузку ответственных</button> : null}</span> : null}
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

            {mode !== 'edit-client' ? (
              <div className="mt-5">
                <div className="text-sm font-medium text-slate-700">Роли контакта</div>
                <div className="mt-2 flex flex-wrap gap-2">
                  {([
                    ['Contact', 'Контакт'],
                    ['Client', 'Клиент'],
                    ['Supplier', 'Поставщик'],
                    ['Agent', 'Агент'],
                  ] as Array<[ContactRole, string]>).map(([role, label]) => (
                    <label key={role} className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm">
                      <SystemCheckbox
                        checked={form.roles.includes(role)}
                        onChange={(event) => setForm((prev) => ({
                          ...prev,
                          roles: event.target.checked
                            ? Array.from(new Set([...prev.roles, role]))
                            : prev.roles.filter((item) => item !== role),
                        }))}
                      />
                      {label}
                    </label>
                  ))}
                </div>
              </div>
            ) : null}

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

              {form.contactType === 'Person' ? <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                <SystemCheckbox
                  type="checkbox"
                  checked={form.syncToPhonebook}
                  onChange={(event) => setForm((prev) => ({ ...prev, syncToPhonebook: event.target.checked }))}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                Участвует в синхронизации Yealink
              </label> : null}
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
          </fieldset>
        </div>

        <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-4">
          <button
            type="button"
            onClick={handleClose}
            className="rounded-xl border border-slate-300 px-4 py-2.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={save}
            disabled={saving || !allowSave}
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
