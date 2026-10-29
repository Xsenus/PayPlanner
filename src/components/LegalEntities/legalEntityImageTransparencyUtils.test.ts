import { describe, expect, it, vi } from 'vitest';
import { getLegalEntityImageProfile } from './legalEntityImageUtils';
import { validateLegalEntityImageTransparency } from './legalEntityImageTransparencyUtils';

describe('fitted upload canvas transparency', () => {
  function drawing(alpha: number) {
    return { canvas: { width: 2, height: 1 }, context: { getImageData: vi.fn(() => ({
      data: Uint8ClampedArray.from([5, 10, 20, 255, 0, 0, 0, alpha]),
    } as ImageData)) } };
  }

  it.each(['seal', 'signature'] as const)('rejects opaque %s before encoding', kind => {
    const { canvas, context } = drawing(255);
    expect(() => validateLegalEntityImageTransparency(canvas, context, getLegalEntityImageProfile(kind))).toThrow('PNG с прозрачными областями');
    expect(context.getImageData).toHaveBeenCalledExactlyOnceWith(0, 0, 2, 1);
  });

  it.each(['seal', 'signature'] as const)('accepts transparent and partially transparent pixels in %s', kind => {
    for (const alpha of [0, 1, 128, 254]) {
      const { canvas, context } = drawing(alpha);
      expect(() => validateLegalEntityImageTransparency(canvas, context, getLegalEntityImageProfile(kind))).not.toThrow();
    }
  });

  it('allows an opaque logo without reading pixels', () => {
    const { canvas, context } = drawing(255);
    expect(() => validateLegalEntityImageTransparency(canvas, context, getLegalEntityImageProfile('logo'))).not.toThrow();
    expect(context.getImageData).not.toHaveBeenCalled();
  });

  it.each(['seal', 'signature'] as const)('reports failed or incomplete pixel reads for %s', kind => {
    const { canvas, context } = drawing(0);
    const profile = getLegalEntityImageProfile(kind);
    context.getImageData.mockImplementationOnce(() => { throw new DOMException('tainted', 'SecurityError'); });
    expect(() => validateLegalEntityImageTransparency(canvas, context, profile)).toThrow('не удалось проверить прозрачность');
    context.getImageData.mockReturnValueOnce({ data: new Uint8ClampedArray(4) } as ImageData);
    expect(() => validateLegalEntityImageTransparency(canvas, context, profile)).toThrow('не удалось проверить прозрачность');
  });

  it.each(['seal', 'signature'] as const)('bounds readback to the existing fitted %s profile', kind => {
    const profile = getLegalEntityImageProfile(kind);
    for (const canvas of [{ width: 0, height: 1 }, { width: 1.5, height: 1 },
      { width: profile.maxWidth + 1, height: 1 }, { width: 1, height: profile.maxHeight + 1 }]) {
      const { context } = drawing(0);
      expect(() => validateLegalEntityImageTransparency(canvas, context, profile)).toThrow('не удалось проверить прозрачность');
      expect(context.getImageData).not.toHaveBeenCalled();
    }
    const canvas = { width: profile.maxWidth, height: profile.maxHeight };
    const pixels = new Uint8ClampedArray(canvas.width * canvas.height * 4).fill(255);
    pixels[pixels.length - 1] = 0;
    const context = { getImageData: vi.fn(() => ({ data: pixels } as ImageData)) };
    expect(() => validateLegalEntityImageTransparency(canvas, context, profile)).not.toThrow();
    expect(context.getImageData).toHaveBeenCalledExactlyOnceWith(0, 0, canvas.width, canvas.height);
  });
});
