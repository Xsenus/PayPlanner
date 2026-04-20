import type { Client, ClientInput } from '../../types';

export const CLIENT_PROFILE_NOTES_PREFIX = '__PP_CLIENT_PROFILE_V2__:';

export type QuickClientModalField = 'LastName' | 'FirstName' | 'MiddleName' | 'Phone' | 'Comment';

export interface QuickClientModalFormData {
  lastName: string;
  firstName: string;
  middleName: string;
  phone: string;
  comment: string;
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
  lastName: '',
  firstName: '',
  middleName: '',
  phone: '',
  comment: '',
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

  return {
    lastName: trimString(profile?.lastName) || fallbackName.lastName,
    firstName: trimString(profile?.firstName) || fallbackName.firstName,
    middleName: trimString(profile?.middleName) || fallbackName.middleName,
    phone: client.phone?.trim() || profilePhones[0] || '',
    comment:
      trimString(profile?.comment)
      || trimString(profile?.legacyNotes)
      || (profile ? '' : client.notes?.trim() || ''),
  };
};

export const buildQuickClientPayload = (
  formData: QuickClientModalFormData,
  client?: Client,
): ClientInput => {
  const existingProfile = parseStructuredProfile(client?.notes);
  const phone = formData.phone.trim();
  const existingPhones = toStringList(existingProfile?.phones);
  const remainingPhones = existingPhones.slice(1);
  const nextProfile: StructuredClientProfile = {
    ...(existingProfile ?? {}),
    kind:
      trimString(existingProfile?.kind)
      || (client?.legalEntityId ? 'legal' : 'individual'),
    lastName: formData.lastName.trim(),
    firstName: formData.firstName.trim(),
    middleName: formData.middleName.trim(),
    phones: normalizeList(phone ? [phone, ...remainingPhones] : remainingPhones),
    comment: formData.comment.trim(),
    legacyNotes: trimString(existingProfile?.legacyNotes),
  };

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
    default:
      return false;
  }
};
