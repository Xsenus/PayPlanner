import { describe, expect, it, vi } from 'vitest';
import {
  LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES,
  LEGAL_ENTITY_SAFE_INLINE_ASSETS_TOTAL_MAX_LENGTH,
  LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH,
  getLegalEntityImageProfile,
  encodeLegalEntityImageCanvas,
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


describe('encodeLegalEntityImageCanvas', () => {
  function drawing(kind: 'logo' | 'seal' | 'signature') {
    const profile = getLegalEntityImageProfile(kind);
    const canvas = { width: profile.maxWidth, height: profile.maxHeight, toDataURL: vi.fn() };
    const context = {
      save: vi.fn(), restore: vi.fn(), fillRect: vi.fn(),
      globalCompositeOperation: 'source-over' as GlobalCompositeOperation, fillStyle: '',
    };
    return { profile, canvas, context };
  }

  it.each(['logo', 'seal', 'signature'] as const)('keeps the PNG for %s without painting a background', kind => {
    const { profile, canvas, context } = drawing(kind);
    const png = 'data:image/png;base64,encoded-pixels';
    canvas.toDataURL.mockReturnValue(png);
    expect(encodeLegalEntityImageCanvas(canvas, context, profile)).toBe(png);
    expect(canvas.toDataURL).toHaveBeenCalledExactlyOnceWith('image/png');
    expect(context.fillRect).not.toHaveBeenCalled();
  });

  it.each(['seal', 'signature'] as const)('rejects oversized %s without turning transparent pixels white or encoding JPEG', kind => {
    const { profile, canvas, context } = drawing(kind);
    canvas.toDataURL.mockImplementation(mime => mime === 'image/png'
      ? 'P'.repeat(profile.maxDataUrlLength + 1) : 'data:image/jpeg;base64,white-background');
    expect(() => encodeLegalEntityImageCanvas(canvas, context, profile)).toThrow(/PNG.*прозрачн/);
    expect(canvas.toDataURL).toHaveBeenCalledExactlyOnceWith('image/png');
    expect(context.fillRect).not.toHaveBeenCalled();
    expect(context.globalCompositeOperation).toBe('source-over');
  });

  it('preserves the existing JPEG fallback for an oversized logo', () => {
    const { profile, canvas, context } = drawing('logo');
    canvas.toDataURL.mockImplementation(mime => mime === 'image/png'
      ? 'P'.repeat(profile.maxDataUrlLength + 1) : 'data:image/jpeg;base64,logo-pixels');
    expect(encodeLegalEntityImageCanvas(canvas, context, profile)).toBe('data:image/jpeg;base64,logo-pixels');
    expect(canvas.toDataURL.mock.calls).toEqual([['image/png'], ['image/jpeg', 0.9]]);
    expect(context.fillRect).toHaveBeenCalledExactlyOnceWith(0, 0, canvas.width, canvas.height);
  });

  it('keeps the bounded logo compression attempts and reports a size error if none fit', () => {
    const { profile, canvas, context } = drawing('logo');
    canvas.toDataURL.mockReturnValue('P'.repeat(profile.maxDataUrlLength + 1));
    expect(() => encodeLegalEntityImageCanvas(canvas, context, profile)).toThrow(/Логотип компании.*слишком большое/);
    expect(canvas.toDataURL).toHaveBeenCalledTimes(8);
    expect(canvas.toDataURL).toHaveBeenLastCalledWith('image/jpeg', 0.42);
  });
});
