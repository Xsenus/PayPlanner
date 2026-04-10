import type { LegalEntitySuggestion } from '../../types';

function normalizeDigits(value?: string | null): string {
  return (value ?? '').replace(/\D+/g, '');
}

function normalizeText(value?: string | null): string {
  return (value ?? '')
    .trim()
    .toLowerCase()
    .replace(/ё/gu, 'е')
    .replace(/\s+/g, ' ')
    .replace(/[^\p{L}\p{N}]+/gu, '');
}

export function getPreferredLegalEntityAddress(
  value?: string | null,
  unrestrictedValue?: string | null,
): string {
  return (unrestrictedValue ?? '').trim() || (value ?? '').trim();
}

export function resolveInitialLegalEntityDadataQuery(inn?: string | null): string {
  return (inn ?? '').trim();
}

export function buildLegalEntitySuggestionKey(
  suggestion: Pick<LegalEntitySuggestion, 'shortName' | 'fullName' | 'inn' | 'kpp' | 'ogrn' | 'address'>,
): string {
  const inn = normalizeDigits(suggestion.inn);
  if (inn) {
    const kpp = normalizeDigits(suggestion.kpp);
    return kpp ? `inn:${inn}|kpp:${kpp}` : `inn:${inn}`;
  }

  const ogrn = normalizeDigits(suggestion.ogrn);
  if (ogrn) {
    return `ogrn:${ogrn}`;
  }

  const name = normalizeText(suggestion.fullName ?? suggestion.shortName);
  const address = normalizeText(suggestion.address);
  return [name, address].filter(Boolean).join('|');
}

export function dedupeLegalEntitySuggestions(
  suggestions: LegalEntitySuggestion[],
  limit = suggestions.length,
): LegalEntitySuggestion[] {
  const normalizedLimit = limit > 0 ? limit : suggestions.length;
  const deduped: LegalEntitySuggestion[] = [];
  const seen = new Set<string>();

  for (const suggestion of suggestions) {
    const key = buildLegalEntitySuggestionKey(suggestion);
    if (key && seen.has(key)) {
      continue;
    }

    if (key) {
      seen.add(key);
    }

    deduped.push(suggestion);
    if (deduped.length >= normalizedLimit) {
      break;
    }
  }

  return deduped;
}
