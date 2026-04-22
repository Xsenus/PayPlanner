import type { ClientAddressEntryFormValue } from "./ClientAddressEntriesForm";
import { ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE } from "../../config/features";

type AddressEntryWithValidity = Pick<
  ClientAddressEntryFormValue,
  "validFrom" | "validTo"
>;
type AddressEntryWithPrimary = Pick<ClientAddressEntryFormValue, "isPrimary">;

export function stripAddressEntryValidity<T extends AddressEntryWithValidity>(
  entry: T,
  keepValidityFields = ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE,
): T {
  if (keepValidityFields) {
    return { ...entry };
  }

  return {
    ...entry,
    validFrom: "",
    validTo: "",
  };
}

export function stripAddressEntryValidityList<
  T extends AddressEntryWithValidity,
>(
  entries: T[],
  keepValidityFields = ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE,
): T[] {
  return entries.map((entry) =>
    stripAddressEntryValidity(entry, keepValidityFields),
  );
}

export function ensureSinglePrimaryAddressEntryList<
  T extends AddressEntryWithPrimary,
>(entries: T[]): T[] {
  if (entries.length === 0) {
    return entries;
  }

  const primaryIndex = entries.findIndex((entry) => entry.isPrimary);

  return entries.map((entry, index) => ({
    ...entry,
    isPrimary: primaryIndex >= 0 ? index === primaryIndex : index === 0,
  }));
}

export function setPrimaryAddressEntry<T extends AddressEntryWithPrimary>(
  entries: T[],
  primaryIndex: number,
): T[] {
  return entries.map((entry, index) => ({
    ...entry,
    isPrimary: index === primaryIndex,
  }));
}
