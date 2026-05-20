// Product switches that are useful to change without hunting through components.
export function parseFeatureFlag(value: unknown, fallback = false): boolean {
  if (typeof value !== 'string') {
    return fallback;
  }

  const normalized = value.trim().toLowerCase();
  if (['1', 'true', 'yes', 'on', 'enabled'].includes(normalized)) {
    return true;
  }

  if (['0', 'false', 'no', 'off', 'disabled'].includes(normalized)) {
    return false;
  }

  return fallback;
}

function readViteFeatureFlag(key: string, fallback = false): boolean {
  return parseFeatureFlag(import.meta.env[key], fallback);
}

export const ADDRESS_ENTRY_VALIDITY_FIELDS_VISIBLE = false;
export const PASSPORT_ENTRY_VALIDITY_FIELDS_VISIBLE = false;

export const LEGAL_EXTERNAL_COURT_SYNC_ENABLED = readViteFeatureFlag('VITE_LEGAL_EXTERNAL_COURT_SYNC_ENABLED');
export const LEGAL_EXTERNAL_CALENDAR_SYNC_ENABLED = readViteFeatureFlag('VITE_LEGAL_EXTERNAL_CALENDAR_SYNC_ENABLED');
export const LEGAL_COURT_EVENTS_ENABLED = readViteFeatureFlag('VITE_LEGAL_COURT_EVENTS_ENABLED');
export const LEGAL_DOCUMENT_TEMPLATE_LIBRARY_ENABLED = readViteFeatureFlag('VITE_LEGAL_DOCUMENT_TEMPLATE_LIBRARY_ENABLED');
