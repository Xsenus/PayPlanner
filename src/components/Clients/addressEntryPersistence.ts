import type { ClientAddressEntryFormValue } from './ClientAddressEntriesForm';
import { ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE } from '../../config/features';

type AddressEntryWithValidity = Pick<ClientAddressEntryFormValue, 'validFrom' | 'validTo'>;

export function stripAddressEntryValidity<T extends AddressEntryWithValidity>(
  entry: T,
  keepValidityFields = ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE,
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

export function stripAddressEntryValidityList<T extends AddressEntryWithValidity>(
  entries: T[],
  keepValidityFields = ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE,
): T[] {
  return entries.map((entry) => stripAddressEntryValidity(entry, keepValidityFields));
}
