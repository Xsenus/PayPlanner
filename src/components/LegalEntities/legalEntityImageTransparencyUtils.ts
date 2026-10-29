import type { LegalEntityImageUploadProfile } from './legalEntityImageUtils';

// Inspect only the fitted upload canvas, never existing images or full source-sized pixels.
export function validateLegalEntityImageTransparency(
  canvas: Pick<HTMLCanvasElement, 'width' | 'height'>,
  context: Pick<CanvasRenderingContext2D, 'getImageData'>,
  profile: LegalEntityImageUploadProfile,
): void {
  if (profile.kind === 'logo') return;
  const unreadable = () => new Error(`${profile.label}: не удалось проверить прозрачность. Выберите другой PNG.`);
  if (!Number.isInteger(canvas.width) || !Number.isInteger(canvas.height)
    || canvas.width <= 0 || canvas.height <= 0
    || canvas.width > profile.maxWidth || canvas.height > profile.maxHeight) throw unreadable();

  let pixels: Uint8ClampedArray;
  try { pixels = context.getImageData(0, 0, canvas.width, canvas.height).data; }
  catch { throw unreadable(); }
  // At most 640,000 bytes for a 400 x 400 seal, 132,000 for a signature.
  if (pixels.length !== canvas.width * canvas.height * 4) throw unreadable();
  for (let index = 3; index < pixels.length; index += 4) {
    if (pixels[index] < 255) return;
  }
  throw new Error(`${profile.label}: нужен PNG с прозрачными областями. Выберите файл без непрозрачной подложки.`);
}
