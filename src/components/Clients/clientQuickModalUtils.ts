import type { Client, ClientInput, LegalEntityDadataDetails } from '../../types';

export const CLIENT_PROFILE_NOTES_PREFIX = '__PP_CLIENT_PROFILE_V2__:';

export type QuickClientKind = 'individual' | 'legal';

export type QuickClientModalField =
  | 'LastName'
  | 'FirstName'
  | 'MiddleName'
  | 'Phone'
  | 'Comment'
  | 'Inn'
  | 'ShortName'
  | 'FullName'
  | 'Kpp'
  | 'Ogrn'
  | 'Address'
  | 'Email'
  | 'Director'
  | 'Country'
  | 'OrganizationForm'
  | 'TaxSystem'
  | 'Okved'
  | 'Okpo'
  | 'Okato'
  | 'Oktmo'
  | 'Okogu'
  | 'Okfs';

export interface QuickClientModalFormData {
  kind: QuickClientKind;
  lastName: string;
  firstName: string;
  middleName: string;
  phone: string;
  comment: string;
  legalInn: string;
  legalName: string;
  legalFullName: string;
  legalKpp: string;
  legalOgrn: string;
  legalAddress: string;
  legalPhone: string;
  legalEmail: string;
  legalDirector: string;
  legalCountry: string;
  legalOrganizationForm: string;
  legalTaxSystem: string;
  legalOkved: string;
  legalOkpo: string;
  legalOkato: string;
  legalOktmo: string;
  legalOkogu: string;
  legalOkfs: string;
}

type StructuredClientProfile = Record<string, unknown> & {
  kind?: string;
  lastName?: string;
  firstName?: string;
  middleName?: string;
  phones?: unknown;
  comment?: string;
  legacyNotes?: string;
};

const emptyFormData = (): QuickClientModalFormData => ({
  kind: 'individual',
  lastName: '',
  firstName: '',
  middleName: '',
  phone: '',
  comment: '',
  legalInn: '',
  legalName: '',
  legalFullName: '',
  legalKpp: '',
  legalOgrn: '',
  legalAddress: '',
  legalPhone: '',
  legalEmail: '',
  legalDirector: '',
  legalCountry: '',
  legalOrganizationForm: '',
  legalTaxSystem: '',
  legalOkved: '',
  legalOkpo: '',
  legalOkato: '',
  legalOktmo: '',
  legalOkogu: '',
  legalOkfs: '',
});

const normalizeList = (values: string[]): string[] =>
  values
    .map((value) => value.trim())
    .filter(Boolean)
    .filter((value, index, source) => source.indexOf(value) === index);

const trimString = (value: unknown): string => (typeof value === 'string' ? value.trim() : '');

const toStringList = (value: unknown): string[] =>
  Array.isArray(value)
    ? normalizeList(value.map((item) => (typeof item === 'string' ? item : '')))
    : [];

const parseStructuredProfile = (notes?: string | null): StructuredClientProfile | null => {
  const rawNotes = notes?.trim() ?? '';
  if (!rawNotes.startsWith(CLIENT_PROFILE_NOTES_PREFIX)) return null;

  try {
    const parsed = JSON.parse(rawNotes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)) as Record<string, unknown>;
    return parsed as StructuredClientProfile;
  } catch {
    return null;
  }
};

const splitClientName = (name?: string | null) => {
  const [lastName = '', firstName = '', ...rest] = (name ?? '').trim().split(/\s+/).filter(Boolean);
  return {
    lastName,
    firstName,
    middleName: rest.join(' '),
  };
};

export const composeClientName = ({
  lastName,
  firstName,
  middleName,
}: Pick<QuickClientModalFormData, 'lastName' | 'firstName' | 'middleName'>): string =>
  [lastName, firstName, middleName].map((value) => value.trim()).filter(Boolean).join(' ');

export const parseQuickClientModalFormData = (client?: Client): QuickClientModalFormData => {
  if (!client) return emptyFormData();

  const profile = parseStructuredProfile(client.notes);
  const fallbackName = splitClientName(client.name);
  const profilePhones = toStringList(profile?.phones);
  const kind = client.legalEntityId || profile?.kind === 'legal' ? 'legal' : 'individual';

  return {
    kind,
    lastName: trimString(profile?.lastName) || fallbackName.lastName,
    firstName: trimString(profile?.firstName) || fallbackName.firstName,
    middleName: trimString(profile?.middleName) || fallbackName.middleName,
    phone: client.phone?.trim() || profilePhones[0] || '',
    comment:
      trimString(profile?.comment)
      || trimString(profile?.legacyNotes)
      || (profile ? '' : client.notes?.trim() || ''),
    legalInn: kind === 'legal' ? client.legalEntity?.inn ?? '' : '',
    legalName: kind === 'legal' ? client.legalEntity?.shortName ?? client.company ?? client.name ?? '' : '',
    legalFullName: kind === 'legal' ? client.legalEntity?.fullName ?? '' : '',
    legalKpp: kind === 'legal' ? client.legalEntity?.kpp ?? '' : '',
    legalOgrn: kind === 'legal' ? client.legalEntity?.ogrn ?? '' : '',
    legalAddress: kind === 'legal' ? client.legalEntity?.address ?? client.address ?? '' : '',
    legalPhone: kind === 'legal' ? client.legalEntity?.phone ?? client.phone ?? '' : '',
    legalEmail: kind === 'legal' ? client.legalEntity?.email ?? client.email ?? '' : '',
    legalDirector: kind === 'legal' ? client.legalEntity?.director ?? '' : '',
    legalCountry: kind === 'legal' ? client.legalEntity?.country ?? '' : '',
    legalOrganizationForm: kind === 'legal' ? client.legalEntity?.organizationForm ?? '' : '',
    legalTaxSystem: kind === 'legal' ? client.legalEntity?.taxSystem ?? '' : '',
    legalOkved: kind === 'legal' ? client.legalEntity?.okved ?? '' : '',
    legalOkpo: kind === 'legal' ? client.legalEntity?.okpo ?? '' : '',
    legalOkato: kind === 'legal' ? client.legalEntity?.okato ?? '' : '',
    legalOktmo: kind === 'legal' ? client.legalEntity?.oktmo ?? '' : '',
    legalOkogu: kind === 'legal' ? client.legalEntity?.okogu ?? '' : '',
    legalOkfs: kind === 'legal' ? client.legalEntity?.okfs ?? '' : '',
  };
};

export const applyLegalEntityDadataDetails = (
  formData: QuickClientModalFormData,
  details: LegalEntityDadataDetails,
): QuickClientModalFormData => ({
  ...formData,
  kind: 'legal',
  legalInn: trimString(details.inn) || formData.legalInn,
  legalName: trimString(details.shortName) || trimString(details.fullName) || formData.legalName,
  legalFullName: trimString(details.fullName) || formData.legalFullName,
  legalKpp: trimString(details.kpp) || formData.legalKpp,
  legalOgrn: trimString(details.ogrn) || formData.legalOgrn,
  legalAddress: trimString(details.address) || formData.legalAddress,
  legalPhone: details.phones?.[0]?.trim() || formData.legalPhone,
  legalEmail: details.emails?.[0]?.trim() || formData.legalEmail,
  legalDirector: trimString(details.director) || formData.legalDirector,
  legalCountry: trimString(details.country) || formData.legalCountry,
  legalOrganizationForm:
    trimString(details.opfShort) || trimString(details.opfFull) || formData.legalOrganizationForm,
  legalTaxSystem: trimString(details.taxSystem) || formData.legalTaxSystem,
  legalOkved: trimString(details.okved) || formData.legalOkved,
  legalOkpo: trimString(details.okpo) || formData.legalOkpo,
  legalOkato: trimString(details.okato) || formData.legalOkato,
  legalOktmo: trimString(details.oktmo) || formData.legalOktmo,
  legalOkogu: trimString(details.okogu) || formData.legalOkogu,
  legalOkfs: trimString(details.okfs) || formData.legalOkfs,
  phone: details.phones?.[0]?.trim() || formData.phone,
});

export const buildQuickClientPayload = (
  formData: QuickClientModalFormData,
  client?: Client,
  legalEntityId?: number | null,
): ClientInput => {
  const existingProfile = parseStructuredProfile(client?.notes);
  const kind = formData.kind;
  const phone = formData.phone.trim();
  const existingPhones = toStringList(existingProfile?.phones);
  const remainingPhones = existingPhones.slice(1);
  const nextProfile: StructuredClientProfile = {
    ...(existingProfile ?? {}),
    kind,
    lastName: formData.lastName.trim(),
    firstName: formData.firstName.trim(),
    middleName: formData.middleName.trim(),
    phones: normalizeList(phone ? [phone, ...remainingPhones] : remainingPhones),
    comment: formData.comment.trim(),
    legacyNotes: trimString(existingProfile?.legacyNotes),
  };

  if (kind === 'legal') {
    const legalName = formData.legalName.trim() || formData.legalFullName.trim() || 'Юридическое лицо';
    return {
      name: legalName,
      email: formData.legalEmail.trim() || client?.email?.trim() || '',
      phone: formData.legalPhone.trim() || phone,
      notes: `${CLIENT_PROFILE_NOTES_PREFIX}${JSON.stringify({
        ...(existingProfile ?? {}),
        kind: 'legal',
        lastName: '',
        firstName: legalName,
        middleName: '',
        inn: formData.legalInn.trim(),
        phones: normalizeList((formData.legalPhone.trim() || phone) ? [formData.legalPhone.trim() || phone] : []),
        shortName: legalName,
        fullName: formData.legalFullName.trim(),
        kpp: formData.legalKpp.trim(),
        ogrn: formData.legalOgrn.trim(),
        address: formData.legalAddress.trim(),
        email: formData.legalEmail.trim(),
        director: formData.legalDirector.trim(),
        country: formData.legalCountry.trim(),
        organizationForm: formData.legalOrganizationForm.trim(),
        taxSystem: formData.legalTaxSystem.trim(),
        okved: formData.legalOkved.trim(),
        okpo: formData.legalOkpo.trim(),
        okato: formData.legalOkato.trim(),
        oktmo: formData.legalOktmo.trim(),
        okogu: formData.legalOkogu.trim(),
        okfs: formData.legalOkfs.trim(),
        comment: formData.comment.trim(),
        legacyNotes: trimString(existingProfile?.legacyNotes),
      })}`,
      company: legalName,
      address: formData.legalAddress.trim() || (client?.address ?? ''),
      isActive: client?.isActive ?? true,
      legalEntityId: legalEntityId ?? client?.legalEntityId ?? null,
      responsibleUserId: client?.responsibleUserId ?? null,
      clientStatusId: client?.clientStatusId ?? null,
    };
  }

  return {
    name: composeClientName(formData),
    email: client?.email?.trim() ?? '',
    phone,
    notes: `${CLIENT_PROFILE_NOTES_PREFIX}${JSON.stringify(nextProfile)}`,
    company: client?.company ?? '',
    address: client?.address ?? '',
    isActive: client?.isActive ?? true,
    legalEntityId: client?.legalEntityId ?? null,
    responsibleUserId: client?.responsibleUserId ?? null,
    clientStatusId: client?.clientStatusId ?? null,
  };
};

export const isQuickClientModalFieldRequired = (
  field: QuickClientModalField,
  isRequired: (key: string) => boolean,
): boolean => {
  const legacyNameRequired = isRequired('Name');
  const legacyCommentRequired = isRequired('Notes');

  switch (field) {
    case 'LastName':
    case 'MiddleName':
      return isRequired(field);
    case 'FirstName':
      return isRequired(field) || legacyNameRequired;
    case 'Comment':
      return isRequired('Comment') || legacyCommentRequired;
    case 'Phone':
      return isRequired('Phone');
    case 'Inn':
    case 'ShortName':
    case 'FullName':
    case 'Kpp':
    case 'Ogrn':
    case 'Address':
    case 'Email':
    case 'Director':
    case 'Country':
    case 'OrganizationForm':
    case 'TaxSystem':
    case 'Okved':
    case 'Okpo':
    case 'Okato':
    case 'Oktmo':
    case 'Okogu':
    case 'Okfs':
      return isRequired(field);
    default:
      return false;
  }
};
