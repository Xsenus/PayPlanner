const COMPANY_PROFILE_SELECTED_COMPANY_ID_KEY = 'pp.companyProfileSelectedCompanyId';
const COMPANY_PROFILE_MODE_KEY = 'pp.companyProfileMode';

export type CompanyProfileMode = 'view' | 'edit';

function normalizeCompanyId(value: number | string | null | undefined): string | null {
  if (value == null) {
    return null;
  }

  const normalized = String(value).trim();
  if (!normalized) {
    return null;
  }

  const parsed = Number(normalized);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    return null;
  }

  return String(parsed);
}

export function getStoredCompanyProfileCompanyId(): string | null {
  if (typeof window === 'undefined') {
    return null;
  }

  try {
    return normalizeCompanyId(window.localStorage.getItem(COMPANY_PROFILE_SELECTED_COMPANY_ID_KEY));
  } catch {
    return null;
  }
}

export function setStoredCompanyProfileCompanyId(value: number | string | null | undefined) {
  if (typeof window === 'undefined') {
    return;
  }

  const normalized = normalizeCompanyId(value);

  try {
    if (!normalized) {
      window.localStorage.removeItem(COMPANY_PROFILE_SELECTED_COMPANY_ID_KEY);
      return;
    }

    window.localStorage.setItem(COMPANY_PROFILE_SELECTED_COMPANY_ID_KEY, normalized);
  } catch {
    // ignore storage issues
  }
}

function normalizeCompanyProfileMode(value: string | null | undefined): CompanyProfileMode | null {
  if (value === 'view' || value === 'edit') {
    return value;
  }

  return null;
}

export function getStoredCompanyProfileMode(): CompanyProfileMode | null {
  if (typeof window === 'undefined') {
    return null;
  }

  try {
    return normalizeCompanyProfileMode(window.localStorage.getItem(COMPANY_PROFILE_MODE_KEY));
  } catch {
    return null;
  }
}

export function setStoredCompanyProfileMode(value: CompanyProfileMode | null | undefined) {
  if (typeof window === 'undefined') {
    return;
  }

  const normalized = normalizeCompanyProfileMode(value ?? null);

  try {
    if (!normalized) {
      window.localStorage.removeItem(COMPANY_PROFILE_MODE_KEY);
      return;
    }

    window.localStorage.setItem(COMPANY_PROFILE_MODE_KEY, normalized);
  } catch {
    // ignore storage issues
  }
}
