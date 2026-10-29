import type { LegalEntityDetail, LegalEntityInput } from '../../types';
import type { CompanyDocumentImagePrintDefaults } from '../../types/documentImagePrint';

export const companyPrintDocumentKinds = ['invoice', 'act', 'contract'] as const;
const imageKeys = ['includeLogo', 'includeSeal', 'includeSignature'] as const;

export interface CompanyImagePrintDraft {
  value: CompanyDocumentImagePrintDefaults | null;
  baseline: CompanyDocumentImagePrintDefaults | null;
  version: string;
  invalid: boolean;
  touched: boolean;
}

export function normalizeCompanyImagePrintDefaults(value: CompanyDocumentImagePrintDefaults | null | undefined): CompanyDocumentImagePrintDefaults | null {
  if (!value || companyPrintDocumentKinds.every(kind => !value[kind])) return null;
  return { invoice: value.invoice ? { ...value.invoice } : null, act: value.act ? { ...value.act } : null, contract: value.contract ? { ...value.contract } : null };
}

export function companyImagePrintDraft(profile?: LegalEntityDetail | null): CompanyImagePrintDraft {
  return {
    value: normalizeCompanyImagePrintDefaults(profile?.documentImagePrintDefaults),
    baseline: normalizeCompanyImagePrintDefaults(profile?.documentImagePrintDefaults),
    version: profile?.documentImagePrintDefaultsVersion ?? '',
    invalid: profile?.documentImagePrintDefaultsInvalid === true,
    touched: false,
  };
}

export function companyImagePrintDraftChanged(draft: CompanyImagePrintDraft): boolean {
  if (!draft.touched) return false;
  if (draft.invalid) return true;
  return companyPrintDocumentKinds.some(kind => {
    const before = draft.baseline?.[kind] ?? null;
    const after = draft.value?.[kind] ?? null;
    return before === null || after === null ? before !== after : imageKeys.some(key => before[key] !== after[key]);
  });
}

export function hasCompanyImagePrintVersion(draft: CompanyImagePrintDraft): boolean {
  return /^[a-f\d]{64}$/i.test(draft.version);
}

export function companyImagePrintPayload(draft: CompanyImagePrintDraft): Pick<LegalEntityInput, 'documentImagePrintDefaults' | 'documentImagePrintDefaultsChanged' | 'documentImagePrintDefaultsVersion'> {
  if (!companyImagePrintDraftChanged(draft)) return {};
  if (!hasCompanyImagePrintVersion(draft)) throw new Error('Откройте карточку заново, чтобы изменить настройки изображений.');
  return { documentImagePrintDefaultsChanged: true, documentImagePrintDefaults: normalizeCompanyImagePrintDefaults(draft.value), documentImagePrintDefaultsVersion: draft.version };
}
