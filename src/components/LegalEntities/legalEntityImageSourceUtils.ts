import {
  getLegalEntityImageProfile,
  LEGAL_ENTITY_SOURCE_MAX_DIMENSION,
  LEGAL_ENTITY_SOURCE_MAX_PIXELS,
  validateLegalEntityImageSourceFile,
  type LegalEntityImageKind,
} from './legalEntityImageUtils';

export const LEGAL_ENTITY_IMAGE_HEADER_MAX_BYTES = 256 * 1024;
export interface LegalEntityImageHeader { format: 'png' | 'jpeg'; width: number; height: number }
const pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
const jpegStartOfFrame = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);

export function validateLegalEntityImageDimensions(width: number, height: number, kind: LegalEntityImageKind): void {
  const label = getLegalEntityImageProfile(kind).label;
  if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width <= 0 || height <= 0)
    throw new Error(`${label}: не удалось определить размер изображения.`);
  if (width > LEGAL_ENTITY_SOURCE_MAX_DIMENSION || height > LEGAL_ENTITY_SOURCE_MAX_DIMENSION || width * height > LEGAL_ENTITY_SOURCE_MAX_PIXELS)
    throw new Error(`${label}: изображение превышает 4096 пикселей по стороне или 4 мегапикселя. Уменьшите его.`);
}

// Container metadata only, not a raster decoder. Native Image still validates and decodes the image.
export function inspectLegalEntityImageHeader(bytes: Uint8Array, totalBytes: number, kind: LegalEntityImageKind): LegalEntityImageHeader {
  const label = getLegalEntityImageProfile(kind).label;
  const damaged = () => new Error(`${label}: не удалось обработать изображение. Заголовок повреждён или файл неполный.`);
  const unavailable = () => new Error(`${label}: заголовок изображения слишком большой или повреждён. Пересохраните файл в PNG или JPG.`);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (bytes.length >= 8 && pngSignature.every((value, index) => bytes[index] === value)) {
    if (bytes.length < 33 || view.getUint32(8) !== 13 || String.fromCharCode(...bytes.subarray(12, 16)) !== 'IHDR') throw damaged();
    const header: LegalEntityImageHeader = { format: 'png', width: view.getUint32(16), height: view.getUint32(20) };
    validateLegalEntityImageDimensions(header.width, header.height, kind);
    for (let offset = 33; offset + 8 <= bytes.length;) {
      const length = view.getUint32(offset);
      const type = String.fromCharCode(...bytes.subarray(offset + 4, offset + 8));
      if (length > totalBytes - offset - 12) throw damaged();
      if (type === 'acTL') throw new Error(`${label}: анимированный PNG не поддерживается. Выберите обычный PNG.`);
      if (type === 'IDAT') return header;
      if (type === 'IEND') throw damaged();
      if (length > bytes.length - offset - 12) throw unavailable();
      offset += length + 12;
    }
    throw unavailable();
  }
  if (bytes[0] === 0xff && bytes[1] === 0xd8) {
    if (kind !== 'logo') throw new Error(`${label}: для печати и подписи нужен PNG с прозрачным фоном.`);
    for (let offset = 2; offset < bytes.length;) {
      if (bytes[offset++] !== 0xff) throw damaged();
      while (offset < bytes.length && bytes[offset] === 0xff) offset++;
      if (offset >= bytes.length) throw unavailable();
      const marker = bytes[offset++];
      if (marker === 0xda || marker === 0xd9 || marker === 0 || marker === 0xd8) throw damaged();
      if (marker === 1 || (marker >= 0xd0 && marker <= 0xd7)) continue;
      if (offset + 2 > bytes.length) throw unavailable();
      const length = view.getUint16(offset);
      if (length < 2 || length > totalBytes - offset) throw damaged();
      if (jpegStartOfFrame.has(marker)) {
        if (length < 8 || offset + 8 > bytes.length) throw damaged();
        const header: LegalEntityImageHeader = { format: 'jpeg', width: view.getUint16(offset + 5), height: view.getUint16(offset + 3) };
        validateLegalEntityImageDimensions(header.width, header.height, kind);
        return header;
      }
      if (length > bytes.length - offset) throw unavailable();
      offset += length;
    }
    throw unavailable();
  }
  throw new Error(`${label}: выберите настоящий файл PNG${kind === 'logo' ? ' или JPG' : ''}.`);
}

export async function inspectLegalEntityImageSource(file: File, kind: LegalEntityImageKind, signal?: AbortSignal): Promise<LegalEntityImageHeader> {
  signal?.throwIfAborted();
  validateLegalEntityImageSourceFile(file, kind);
  let buffer: ArrayBuffer;
  try { buffer = await file.slice(0, LEGAL_ENTITY_IMAGE_HEADER_MAX_BYTES).arrayBuffer(); }
  catch {
    signal?.throwIfAborted();
    throw new Error(`${getLegalEntityImageProfile(kind).label}: не удалось прочитать заголовок изображения.`);
  }
  signal?.throwIfAborted();
  const header = inspectLegalEntityImageHeader(new Uint8Array(buffer), file.size, kind);
  const expectedMime = header.format === 'png' ? 'image/png' : 'image/jpeg';
  if (file.type && file.type.toLowerCase() !== expectedMime)
    throw new Error(`${getLegalEntityImageProfile(kind).label}: содержимое файла не соответствует указанному формату.`);
  return header;
}
