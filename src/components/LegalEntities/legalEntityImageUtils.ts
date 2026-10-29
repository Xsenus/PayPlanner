import type { LegalEntityInput } from '../../types';

export type LegalEntityImageKind = 'logo' | 'seal' | 'signature';

export interface LegalEntityImageUploadProfile {
  kind: LegalEntityImageKind;
  label: string;
  maxWidth: number;
  maxHeight: number;
  maxDataUrlLength: number;
}

export const LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH = 900_000;
export const LEGAL_ENTITY_SAFE_INLINE_ASSETS_TOTAL_MAX_LENGTH = 700_000;
export const LEGAL_ENTITY_LOGO_SOURCE_MAX_BYTES = 2 * 1024 * 1024;

export function getLegalEntityImageAccept(kind: LegalEntityImageKind): string {
  return kind === 'logo' ? 'image/png,image/jpeg' : 'image/png';
}

export function getLegalEntityImageSourceHint(kind: LegalEntityImageKind): string {
  return kind === 'logo'
    ? 'PNG или JPG, до 2 МБ.'
    : 'PNG с прозрачным фоном.';
}

// File metadata is an early UX check; it does not replace raster decoding or server validation.
export function validateLegalEntityImageSourceFile(
  file: Pick<File, 'type' | 'name' | 'size'>,
  kind: LegalEntityImageKind,
): void {
  const label = LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES[kind].label;
  if (!Number.isSafeInteger(file.size) || file.size <= 0) {
    throw new Error(`${label}: выбран пустой файл. Выберите изображение.`);
  }
  const mime = file.type.trim().toLowerCase();
  const format = mime || (/\.png$/i.test(file.name) ? 'image/png'
    : /\.jpe?g$/i.test(file.name) ? 'image/jpeg' : '');
  if (format !== 'image/png' && (kind !== 'logo' || format !== 'image/jpeg')) {
    throw new Error(`${label}: ${getLegalEntityImageSourceHint(kind)}`);
  }
  if (kind === 'logo' && file.size > LEGAL_ENTITY_LOGO_SOURCE_MAX_BYTES) {
    throw new Error(`${label}: исходный файл больше 2 МБ. Выберите файл меньшего размера.`);
  }
}

export const LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES: Record<LegalEntityImageKind, LegalEntityImageUploadProfile> = {
  logo: {
    kind: 'logo',
    label: 'Логотип компании',
    maxWidth: 1090,
    maxHeight: 370,
    maxDataUrlLength: 260_000,
  },
  seal: {
    kind: 'seal',
    label: 'Печать компании',
    maxWidth: 400,
    maxHeight: 400,
    maxDataUrlLength: 220_000,
  },
  signature: {
    kind: 'signature',
    label: 'Подпись руководителя',
    maxWidth: 330,
    maxHeight: 100,
    maxDataUrlLength: 120_000,
  },
};

export function getLegalEntityImageProfile(kind: LegalEntityImageKind): LegalEntityImageUploadProfile {
  return LEGAL_ENTITY_IMAGE_UPLOAD_PROFILES[kind];
}

export function sumLegalEntityInlineAssetLengths(
  payload: Pick<LegalEntityInput, 'logoDataUrl' | 'sealDataUrl' | 'directorSignatureDataUrl'>,
): number {
  return [
    payload.logoDataUrl,
    payload.sealDataUrl,
    payload.directorSignatureDataUrl,
  ].reduce((sum, value) => sum + (value?.length ?? 0), 0);
}

export function isLegalEntityRequestBodyLengthSafe(length: number): boolean {
  return length <= LEGAL_ENTITY_SAFE_REQUEST_BODY_MAX_LENGTH;
}

export function isLegalEntityInlineAssetPayloadSafe(
  payload: Pick<LegalEntityInput, 'logoDataUrl' | 'sealDataUrl' | 'directorSignatureDataUrl'>,
): boolean {
  return sumLegalEntityInlineAssetLengths(payload) <= LEGAL_ENTITY_SAFE_INLINE_ASSETS_TOTAL_MAX_LENGTH;
}

export function encodeLegalEntityImageCanvas(
  canvas: Pick<HTMLCanvasElement, 'toDataURL' | 'width' | 'height'>,
  context: Pick<CanvasRenderingContext2D, 'save' | 'restore' | 'globalCompositeOperation' | 'fillStyle' | 'fillRect'>,
  profile: LegalEntityImageUploadProfile,
): string {
  const pngCandidate = canvas.toDataURL('image/png');
  if (pngCandidate.length <= profile.maxDataUrlLength) {
    return pngCandidate;
  }

  if (profile.kind !== 'logo') {
    throw new Error(
      `${profile.label}: PNG слишком большой для сохранения. Выберите PNG меньшего размера с прозрачным фоном.`,
    );
  }

  const qualities = [0.9, 0.82, 0.74, 0.66, 0.58, 0.5, 0.42];
  for (const quality of qualities) {
    context.save();
    context.globalCompositeOperation = 'destination-over';
    context.fillStyle = '#ffffff';
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.restore();

    const jpegCandidate = canvas.toDataURL('image/jpeg', quality);
    if (jpegCandidate.length <= profile.maxDataUrlLength) {
      return jpegCandidate;
    }
  }

  throw new Error(
    `${profile.label}: изображение слишком большое даже после сжатия. Выберите файл поменьше.`,
  );
}
