import { describe, expect, it } from 'vitest';
import { companyImagePrintDraft, companyImagePrintDraftChanged, companyImagePrintPayload } from './companyImagePrintDefaults';
import type { LegalEntityDetail } from '../../types';

const version = 'A'.repeat(64);
const profile = (extra: Partial<LegalEntityDetail> = {}) => ({ documentImagePrintDefaultsVersion: version, ...extra }) as LegalEntityDetail;

describe('company image print draft', () => {
  it('omits untouched settings including a reported invalid stored value', () => {
    expect(companyImagePrintPayload(companyImagePrintDraft(profile()))).toEqual({});
    expect(companyImagePrintPayload(companyImagePrintDraft(profile({ documentImagePrintDefaultsInvalid: true })))).toEqual({});
  });
  it('preserves independent false choices, null kinds and the captured version', () => {
    const draft = companyImagePrintDraft(profile());
    draft.value = { invoice: { includeLogo: false, includeSeal: true, includeSignature: false }, act: null, contract: { includeLogo: false, includeSeal: false, includeSignature: false } };
    draft.touched = true;
    expect(companyImagePrintPayload(draft)).toEqual({ documentImagePrintDefaultsChanged: true, documentImagePrintDefaults: draft.value, documentImagePrintDefaultsVersion: version });
  });
  it('omits a reverted edit without sharing the original options object', () => {
    const original = { invoice: { includeLogo: true, includeSeal: false, includeSignature: false }, act: null, contract: null };
    const draft = companyImagePrintDraft(profile({ documentImagePrintDefaults: original }));
    draft.touched = true; draft.value!.invoice!.includeLogo = false;
    expect(companyImagePrintDraftChanged(draft)).toBe(true);
    expect(original.invoice.includeLogo).toBe(true); expect(draft.baseline!.invoice!.includeLogo).toBe(true);
    draft.value!.invoice!.includeLogo = true;
    expect(companyImagePrintPayload(draft)).toEqual({});
  });
  it('treats all null kinds as no setting and makes invalid reset explicit', () => {
    const draft = companyImagePrintDraft(profile({ documentImagePrintDefaults: { invoice: null, act: null, contract: null } }));
    draft.touched = true; expect(companyImagePrintPayload(draft)).toEqual({});
    draft.invalid = true;
    expect(companyImagePrintPayload(draft)).toEqual({ documentImagePrintDefaultsChanged: true, documentImagePrintDefaults: null, documentImagePrintDefaultsVersion: version });
  });
  it.each(['', 'bad', 'g'.repeat(64)])('blocks a changed draft with unavailable version %s', token => {
    const draft = companyImagePrintDraft(profile({ documentImagePrintDefaultsVersion: token, documentImagePrintDefaultsInvalid: true }));
    draft.touched = true;
    expect(() => companyImagePrintPayload(draft)).toThrow('Откройте карточку заново');
    draft.touched = false; expect(companyImagePrintPayload(draft)).toEqual({});
  });
});
