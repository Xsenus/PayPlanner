import { describe, expect, it } from 'vitest';
import {
  LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES,
  LEGAL_ENTITY_SAFE_INLINE_ASSETS_TOTAL_MAX_LENGTH,
  LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH,
  getLegalEntityImageProfile,
  isLegalEntityInlineAssetPayloadSafe,
  isLegalEntityRequestBodyLengthSafe,
  sumLegalEntityInlineAssetLengths,
} from './legalEntityImageUtils';

describe('legalEntityImageUtils', () => {
  it('returns stable upload profiles', () => {
    expect(getLegalEntityImageProfile('logo')).toEqual(LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES.logo);
    expect(getLegalEntityImageProfile('seal')).toEqual(LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES.seal);
    expect(getLegalEntityImageProfile('signature')).toEqual(LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES.signature);
  });

  it('sums inline asset payload lengths', () => {
    const total = sumLegalEntityInlineAssetLengths({
      logoDataUrl: 'data:image/png;base64,AAAA',
      sealDataUrl: 'data:image/png;base64,BBBBBB',
      directorSignatureDataUrl: null,
    });

    expect(total).toBe('data:image/png;base64,AAAA'.length + 'data:image/png;base64,BBBBBB'.length);
  });

  it('checks safe inline asset payload budget', () => {
    expect(isLegalEntityInlineAssetPayloadSafe({
      logoDataUrl: 'A'.repeat(200_000),
      sealDataUrl: 'B'.repeat(200_000),
      directorSignatureDataUrl: 'C'.repeat(100_000),
    })).toBe(true);

    expect(isLegalEntityInlineAssetPayloadSafe({
      logoDataUrl: 'A'.repeat(300_000),
      sealDataUrl: 'B'.repeat(300_000),
      directorSignatureDataUrl: 'C'.repeat(200_000),
    })).toBe(false);

    expect(LEGAL_ENTITY_SAFE_INLINE_ASSETS_TOTAL_MAX_LENGTH).toBeGreaterThan(0);
  });

  it('checks safe request body length', () => {
    expect(isLegalEntityRequestBodyLengthSafe(LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH)).toBe(true);
    expect(isLegalEntityRequestBodyLengthSafe(LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH + 1)).toBe(false);
  });
});
