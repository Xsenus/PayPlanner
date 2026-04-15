import type { ClientPassportEntryFormValue } from './ClientPassportEntriesForm';
import { PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE } from '../../config/features';

type PassportEntryWithValidity = Pick<ClientPassportEntryFormValue, 'validFrom' | 'validTo'>;

export function stripPassportEntryValidity<T extends PassportEntryWithValidity>(
  entry: T,
  keepValidityFields = PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE,
): T {
  if (keepValidityFields) {
    return { ...entry };
  }

  return {
    ...entry,
    validFrom: '',
    validTo: '',
  };
}

export function stripPassportEntryValidityList<T extends PassportEntryWithValidity>(
  entries: T[],
  keepValidityFields = PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE,
): T[] {
  return entries.map((entry) => stripPassportEntryValidity(entry, keepValidityFields));
}
