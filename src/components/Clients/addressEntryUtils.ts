export interface AddressDisplayParts {
  postalCode?: string | null;
  country?: string | null;
  region?: string | null;
  district?: string | null;
  city?: string | null;
  settlement?: string | null;
  street?: string | null;
  house?: string | null;
  flat?: string | null;
}

function normalizeAddressPart(value?: string | null): string {
  return (value ?? '').trim();
}

export function composeAddressFromParts(parts: AddressDisplayParts): string {
  const values = [
    normalizeAddressPart(parts.postalCode),
    normalizeAddressPart(parts.country),
    normalizeAddressPart(parts.region),
    normalizeAddressPart(parts.district),
    normalizeAddressPart(parts.city),
    normalizeAddressPart(parts.settlement),
    normalizeAddressPart(parts.street),
    normalizeAddressPart(parts.house) && `д ${normalizeAddressPart(parts.house)}`,
    normalizeAddressPart(parts.flat) && `кв ${normalizeAddressPart(parts.flat)}`,
  ]
    .filter(Boolean)
    .filter((value, index, source) =>
      source.findIndex((item) => item.toLowerCase() === value.toLowerCase()) === index,
    );

  return values.join(', ');
}

function scoreAddressCandidate(value: string): number {
  const trimmed = value.trim();
  if (!trimmed) {
    return -1;
  }

  let score = trimmed.length;
  score += (trimmed.match(/,/g) ?? []).length * 4;

  if (/\b\d{6}\b/u.test(trimmed)) score += 18;
  if (/\b(россия|рф)\b/iu.test(trimmed)) score += 8;
  if (/\b(обл|область|край|респ|республика|ао|автономный округ)\b/iu.test(trimmed)) score += 10;
  if (/\b(район|р-н)\b/iu.test(trimmed)) score += 8;
  if (/\b(г|город|пос|поселок|посёлок|с|село|дер|деревня)\b/iu.test(trimmed)) score += 10;
  if (/\b(ул|улица|пр-кт|проспект|пер|переулок|мкр|микрорайон|пл|площадь|наб|набережная|ш|шоссе)\b/iu.test(trimmed)) score += 16;
  if (/\b(д|дом)\b/iu.test(trimmed) || /,\s*\d+[а-яa-z]?\b/iu.test(trimmed)) score += 18;
  if (/\b(кв|квартира|оф|офис)\b/iu.test(trimmed)) score += 14;

  return score;
}

export function chooseBestDisplayAddress(
  parts: AddressDisplayParts,
  ...candidates: Array<string | null | undefined>
): string {
  const unique = new Map<string, string>();

  for (const candidate of [...candidates, composeAddressFromParts(parts)]) {
    const normalized = (candidate ?? '').trim();
    if (!normalized) continue;
    const key = normalized.toLowerCase();
    if (!unique.has(key)) {
      unique.set(key, normalized);
    }
  }

  const values = Array.from(unique.values());
  if (values.length === 0) {
    return '';
  }

  return values.sort((left, right) => {
    const scoreDiff = scoreAddressCandidate(right) - scoreAddressCandidate(left);
    if (scoreDiff !== 0) {
      return scoreDiff;
    }

    return right.length - left.length;
  })[0];
}
