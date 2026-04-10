import type { LegalEntityAddressInput } from '../../types';
import type { ClientAddressEntryFormValue } from '../Clients/ClientAddressEntriesForm';
import { chooseBestDisplayAddress } from '../Clients/addressEntryUtils';

type ComparableAddress = {
  type?: string | number | null;
  fullAddress?: string | null;
  value?: string | null;
  unrestrictedValue?: string | null;
  postalCode?: string | null;
  region?: string | null;
  regionWithType?: string | null;
  city?: string | null;
  cityWithType?: string | null;
  settlement?: string | null;
  street?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  flat?: string | null;
  fiasId?: string | null;
};

function normalizeAddressToken(value?: string | number | null): string {
  if (value === null || value === undefined) return '';
  return String(value)
    .trim()
    .toLowerCase()
    .replace(/[.,]/g, ' ')
    .replace(/\b\d{6}\b/gu, ' ')
    .replace(/россия/giu, ' ')
    .replace(/рф/giu, ' ')
    .replace(/город/giu, ' ')
    .replace(/гор/giu, ' ')
    .replace(/(^|\s)г(?=\s|$)/giu, ' ')
    .replace(/улица/giu, ' ')
    .replace(/(^|\s)ул(?=\s|$)/giu, ' ')
    .replace(/дом/giu, ' ')
    .replace(/(^|\s)д(?=\s|$)/giu, ' ')
    .replace(/квартира/giu, ' ')
    .replace(/(^|\s)кв(?=\s|$)/giu, ' ')
    .replace(/область/giu, ' ')
    .replace(/(^|\s)обл(?=\s|$)/giu, ' ')
    .replace(/\s+/g, ' ')
    .replace(/[^\p{L}\p{N}]+/gu, '');
}

function buildStructuredLegalEntityAddressKey(address: ComparableAddress): string {
  return [
    address.regionWithType ?? address.region,
    address.cityWithType ?? address.city ?? address.settlement,
    address.streetWithType ?? address.street,
    address.house,
    address.flat,
  ]
    .map((item) => normalizeAddressToken(item))
    .filter(Boolean)
    .join('|');
}

function buildTextLegalEntityAddressKey(address: ComparableAddress): string {
  return normalizeAddressToken(
    address.unrestrictedValue ?? address.value ?? address.fullAddress,
  );
}

function buildLegalEntityAddressKeys(address: ComparableAddress): string[] {
  const typeKey = normalizeAddressToken(address.type);
  const fiasKey = normalizeAddressToken(address.fiasId);
  const keys: string[] = [];

  if (fiasKey) {
    keys.push(`${typeKey}|fias:${fiasKey}`);
  }

  const structuredKey = buildStructuredLegalEntityAddressKey(address);
  if (structuredKey) {
    keys.push(`${typeKey}|structured:${structuredKey}`);
  }

  const textKey = buildTextLegalEntityAddressKey(address);
  if (textKey) {
    keys.push(`${typeKey}|text:${textKey}`);
  }

  return keys;
}

export function buildLegalEntityAddressKey(address: ComparableAddress): string {
  const keys = buildLegalEntityAddressKeys(address);
  return keys[0] ?? `${normalizeAddressToken(address.type)}|`;
}

function preferIncoming(current: string, incoming?: string | null): string {
  const normalizedIncoming = (incoming ?? '').trim();
  return normalizedIncoming || current;
}

function fillIfEmpty(current: string, incoming?: string | null): string {
  if (current.trim()) return current;
  return (incoming ?? '').trim();
}

export function mergeLegalEntityAddressEntry(
  current: ClientAddressEntryFormValue,
  incoming: Partial<ClientAddressEntryFormValue>,
): ClientAddressEntryFormValue {
  const mergedParts = {
    postalCode: current.postalCode || incoming.postalCode,
    country: current.country || incoming.country,
    region: current.region || incoming.region,
    district: current.district || incoming.district,
    city: current.city || incoming.city,
    settlement: current.settlement || incoming.settlement,
    street: current.street || incoming.street,
    house: current.house || incoming.house,
    flat: current.flat || incoming.flat,
  };

  return {
    ...current,
    fullAddress: chooseBestDisplayAddress(
      mergedParts,
      current.fullAddress,
      incoming.fullAddress,
    ),
    postalCode: fillIfEmpty(current.postalCode, incoming.postalCode),
    country: fillIfEmpty(current.country ?? '', incoming.country),
    region: fillIfEmpty(current.region, incoming.region),
    district: fillIfEmpty(current.district, incoming.district),
    city: fillIfEmpty(current.city, incoming.city),
    settlement: fillIfEmpty(current.settlement, incoming.settlement),
    street: fillIfEmpty(current.street, incoming.street),
    house: fillIfEmpty(current.house, incoming.house),
    flat: fillIfEmpty(current.flat, incoming.flat),
    fiasId: fillIfEmpty(current.fiasId ?? '', incoming.fiasId),
    kladrId: fillIfEmpty(current.kladrId ?? '', incoming.kladrId),
    geoLat: fillIfEmpty(current.geoLat ?? '', incoming.geoLat),
    geoLon: fillIfEmpty(current.geoLon ?? '', incoming.geoLon),
    validFrom: fillIfEmpty(current.validFrom, incoming.validFrom),
    validTo: fillIfEmpty(current.validTo, incoming.validTo),
    additionalTypes: Array.from(
      new Set([...(current.additionalTypes ?? []), ...(incoming.additionalTypes ?? [])]),
    ),
  };
}

export function findMatchingLegalEntityAddressEntryIndex(
  entries: ClientAddressEntryFormValue[],
  incoming: Partial<ClientAddressEntryFormValue>,
): number {
  const incomingKey = buildLegalEntityAddressKey({
    type: incoming.type,
    fullAddress: incoming.fullAddress,
    postalCode: incoming.postalCode,
    region: incoming.region,
    city: incoming.city,
    settlement: incoming.settlement,
    street: incoming.street,
    house: incoming.house,
    flat: incoming.flat,
    fiasId: incoming.fiasId,
  });

  const incomingKeys = new Set(buildLegalEntityAddressKeys({
    type: incoming.type,
    fullAddress: incoming.fullAddress,
    postalCode: incoming.postalCode,
    region: incoming.region,
    city: incoming.city,
    settlement: incoming.settlement,
    street: incoming.street,
    house: incoming.house,
    flat: incoming.flat,
    fiasId: incoming.fiasId,
  }));

  if (incomingKeys.size === 0 || !incomingKey.replace(/\|/g, '')) return -1;

  return entries.findIndex((entry) => {
    const entryKeys = buildLegalEntityAddressKeys({
      type: entry.type,
      fullAddress: entry.fullAddress,
      postalCode: entry.postalCode,
      region: entry.region,
      city: entry.city,
      settlement: entry.settlement,
      street: entry.street,
      house: entry.house,
      flat: entry.flat,
      fiasId: entry.fiasId,
    });

    return entryKeys.some((key) => incomingKeys.has(key));
  });
}

export function upsertLegalEntityAddressEntry(
  entries: ClientAddressEntryFormValue[],
  incoming: ClientAddressEntryFormValue,
): ClientAddressEntryFormValue[] {
  const matchIndex = findMatchingLegalEntityAddressEntryIndex(entries, incoming);
  if (matchIndex < 0) {
    return [incoming, ...entries];
  }

  return entries.map((entry, index) =>
    index === matchIndex ? mergeLegalEntityAddressEntry(entry, incoming) : entry,
  );
}

function mergeLegalEntityAddressInput(
  current: LegalEntityAddressInput,
  incoming: LegalEntityAddressInput,
): LegalEntityAddressInput {
  return {
    ...current,
    value: preferIncoming(current.value, incoming.value),
    unrestrictedValue: preferIncoming(
      current.unrestrictedValue ?? current.value,
      incoming.unrestrictedValue ?? incoming.value,
    ),
    postalCode: current.postalCode || incoming.postalCode || null,
    country: current.country || incoming.country || null,
    regionWithType: current.regionWithType || incoming.regionWithType || null,
    cityWithType: current.cityWithType || incoming.cityWithType || null,
    streetWithType: current.streetWithType || incoming.streetWithType || null,
    house: current.house || incoming.house || null,
    flat: current.flat || incoming.flat || null,
    fiasId: current.fiasId || incoming.fiasId || null,
    kladrId: current.kladrId || incoming.kladrId || null,
    geoLat: current.geoLat || incoming.geoLat || null,
    geoLon: current.geoLon || incoming.geoLon || null,
    isPrimary: Boolean(current.isPrimary || incoming.isPrimary),
  };
}

export function dedupeLegalEntityAddressInputs(
  addresses: LegalEntityAddressInput[],
): LegalEntityAddressInput[] {
  const deduped = new Map<string, LegalEntityAddressInput>();
  const order: string[] = [];

  for (const address of addresses) {
    const key = buildLegalEntityAddressKey({
      type: address.type,
      value: address.value,
      unrestrictedValue: address.unrestrictedValue,
      postalCode: address.postalCode,
      regionWithType: address.regionWithType,
      cityWithType: address.cityWithType,
      streetWithType: address.streetWithType,
      house: address.house,
      flat: address.flat,
      fiasId: address.fiasId,
    });

    if (!key.replace(/\|/g, '')) continue;

    if (!deduped.has(key)) {
      deduped.set(key, { ...address });
      order.push(key);
      continue;
    }

    deduped.set(key, mergeLegalEntityAddressInput(deduped.get(key)!, address));
  }

  const result = order.map((key) => deduped.get(key)!);
  const primaryIndex = result.findIndex((item) => item.isPrimary);

  return result.map((item, index) => ({
    ...item,
    isPrimary: primaryIndex >= 0 ? index === primaryIndex : index === 0,
  }));
}
