import { describe, expect, it, vi } from 'vitest';
import {
  LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES,
  LEGAL_ENTITY_SAFE_INLINE_ASSETS_TOTAL_MAX_LENGTH,
  LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH,
  getLegalEntityImageProfile,
  encodeLegalEntityImageCanvas,
  LEGAL_ENTITY_LOGO_SOURCE_MAX_BYTES,
  getLegalEntityImageAccept,
  getLegalEntityImageSourceHint,
  validateLegalEntityImageSourceFile,
  isLegalEntityInlineAssetPayloadSafe,
  isLegalEntityRequestBodyLengthSafe,
  sumLegalEntityInlineAssetLengths,
} from './legalEntityImageUtils';

describe('legalEntityImageUtils', () => {
  describe('source file preflight', () => {
    it.each(['logo', 'seal', 'signature'] as const)('accepts PNG metadata for %s, including missing browser MIME', kind => {
      expect(() => validateLegalEntityImageSourceFile({ type: 'image/png', name: 'scan.png', size: 1 }, kind)).not.toThrow();
      expect(() => validateLegalEntityImageSourceFile({ type: '', name: 'SCAN.PNG', size: 1 }, kind)).not.toThrow();
      expect(getLegalEntityImageAccept(kind)).toBe(kind === 'logo' ? 'image/png,image/jpeg' : 'image/png');
      expect(getLegalEntityImageSourceHint(kind)).toContain(kind === 'logo' ? '2 МБ' : 'прозрачным');
    });

    it('accepts a logo at the inclusive 2 MiB boundary and rejects one byte above it', () => {
      const file = { type: 'image/jpeg', name: 'logo.jpg', size: LEGAL_ENTITY_LOGO_SOURCE_MAX_BYTES };
      expect(() => validateLegalEntityImageSourceFile(file, 'logo')).not.toThrow();
      expect(() => validateLegalEntityImageSourceFile({ ...file, type: '', name: 'logo.JPEG' }, 'logo')).not.toThrow();
      expect(() => validateLegalEntityImageSourceFile({ ...file, size: file.size + 1 }, 'logo')).toThrow('исходный файл больше 2 МБ');
    });

    it.each(['seal', 'signature'] as const)('rejects JPEG metadata for %s even with a PNG extension', kind => {
      expect(() => validateLegalEntityImageSourceFile({ type: 'image/jpeg', name: 'renamed.png', size: 100 }, kind)).toThrow('PNG с прозрачным фоном');
      expect(() => validateLegalEntityImageSourceFile({ type: '', name: 'seal.jpg', size: 100 }, kind)).toThrow('PNG с прозрачным фоном');
    });

    it.each(['logo', 'seal', 'signature'] as const)('rejects empty and unsupported files for %s before reading them', kind => {
      expect(() => validateLegalEntityImageSourceFile({ type: 'image/png', name: 'empty.png', size: 0 }, kind)).toThrow('пустой файл');
      for (const type of ['image/svg+xml', 'image/gif', 'image/webp', 'application/octet-stream']) {
        expect(() => validateLegalEntityImageSourceFile({ type, name: 'renamed.png', size: 100 }, kind)).toThrow();
      }
      expect(() => validateLegalEntityImageSourceFile({ type: '', name: 'file.unknown', size: 100 }, kind)).toThrow();
    });
  });

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
