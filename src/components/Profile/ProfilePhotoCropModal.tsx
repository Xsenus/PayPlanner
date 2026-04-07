import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type WheelEvent as ReactWheelEvent } from 'react';
import { Check, Loader2, Move, RotateCcw, X, ZoomIn, ZoomOut } from 'lucide-react';

const CROP_BOX_SIZE = 320;
const PREVIEW_LARGE_SIZE = 104;
const PREVIEW_SMALL_SIZE = 48;
const OUTPUT_SIZE = 512;

type ProfilePhotoCropModalProps = {
  open: boolean;
  imageSrc: string | null;
  fileName: string;
  mimeType: string;
  submitting?: boolean;
  onClose: () => void;
  onConfirm: (file: File) => Promise<void> | void;
};

type ImageSize = {
  width: number;
  height: number;
};

type Offset = {
  x: number;
  y: number;
};

type DragState = {
  pointerId: number;
  startX: number;
  startY: number;
  startOffset: Offset;
};

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function normalizeMimeType(mimeType: string) {
  if (mimeType === 'image/jpeg' || mimeType === 'image/png' || mimeType === 'image/webp') {
    return mimeType;
  }

  return 'image/png';
}

function extensionFromMimeType(mimeType: string) {
  switch (mimeType) {
    case 'image/jpeg':
      return 'jpg';
    case 'image/webp':
      return 'webp';
    default:
      return 'png';
  }
}

function baseNameFromFileName(fileName: string) {
  const trimmed = fileName.trim();
  if (!trimmed) return 'profile-photo';
  const lastDot = trimmed.lastIndexOf('.');
  return lastDot > 0 ? trimmed.slice(0, lastDot) : trimmed;
}

export function ProfilePhotoCropModal({
  open,
  imageSrc,
  fileName,
  mimeType,
  submitting = false,
  onClose,
  onConfirm,
}: ProfilePhotoCropModalProps) {
  const [imageSize, setImageSize] = useState<ImageSize | null>(null);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState<Offset>({ x: 0, y: 0 });
  const dragStateRef = useRef<DragState | null>(null);

  useEffect(() => {
    if (!open) return;
    setZoom(1);
    setOffset({ x: 0, y: 0 });
    setImageSize(null);
  }, [imageSrc, open]);

  useEffect(() => {
    if (!open) return;

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !submitting) {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [onClose, open, submitting]);

  const baseScale = useMemo(() => {
    if (!imageSize) return 1;
    return Math.max(CROP_BOX_SIZE / imageSize.width, CROP_BOX_SIZE / imageSize.height);
  }, [imageSize]);

  const displayScale = useMemo(() => baseScale * zoom, [baseScale, zoom]);

  const renderedImageSize = useMemo(() => {
    if (!imageSize) return { width: CROP_BOX_SIZE, height: CROP_BOX_SIZE };
    return {
      width: imageSize.width * displayScale,
      height: imageSize.height * displayScale,
    };
  }, [displayScale, imageSize]);

  const maxOffset = useMemo(
    () => ({
      x: Math.max(0, (renderedImageSize.width - CROP_BOX_SIZE) / 2),
      y: Math.max(0, (renderedImageSize.height - CROP_BOX_SIZE) / 2),
    }),
    [renderedImageSize.height, renderedImageSize.width],
  );

  const clampOffset = useCallback(
    (value: Offset) => ({
      x: clamp(value.x, -maxOffset.x, maxOffset.x),
      y: clamp(value.y, -maxOffset.y, maxOffset.y),
    }),
    [maxOffset.x, maxOffset.y],
  );

  useEffect(() => {
    setOffset((current) => clampOffset(current));
  }, [clampOffset]);

  const imageStyle = useMemo(
    () => ({
      width: `${renderedImageSize.width}px`,
      height: `${renderedImageSize.height}px`,
      left: `calc(50% - ${renderedImageSize.width / 2}px + ${offset.x}px)`,
      top: `calc(50% - ${renderedImageSize.height / 2}px + ${offset.y}px)`,
    }),
    [offset.x, offset.y, renderedImageSize.height, renderedImageSize.width],
  );

  const handleZoomChange = useCallback(
    (nextZoom: number) => {
      setZoom(clamp(nextZoom, 1, 3));
    },
    [],
  );

  const handleWheel = useCallback(
    (event: ReactWheelEvent<HTMLDivElement>) => {
      event.preventDefault();
      const nextZoom = zoom - event.deltaY * 0.0015;
      handleZoomChange(nextZoom);
    },
    [handleZoomChange, zoom],
  );

  const handlePointerDown = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    dragStateRef.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      startOffset: offset,
    };
    event.currentTarget.setPointerCapture(event.pointerId);
  }, [offset]);

  const handlePointerMove = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      const dragState = dragStateRef.current;
      if (!dragState || dragState.pointerId !== event.pointerId) return;

      const deltaX = event.clientX - dragState.startX;
      const deltaY = event.clientY - dragState.startY;

      setOffset(
        clampOffset({
          x: dragState.startOffset.x + deltaX,
          y: dragState.startOffset.y + deltaY,
        }),
      );
    },
    [clampOffset],
  );

  const stopDragging = useCallback((event?: ReactPointerEvent<HTMLDivElement>) => {
    if (event && dragStateRef.current?.pointerId === event.pointerId && event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    dragStateRef.current = null;
  }, []);

  const buildCroppedFile = useCallback(async () => {
    if (!imageSrc || !imageSize) return null;

    const image = new Image();
    image.src = imageSrc;
    await image.decode();

    const canvas = document.createElement('canvas');
    canvas.width = OUTPUT_SIZE;
    canvas.height = OUTPUT_SIZE;

    const context = canvas.getContext('2d');
    if (!context) return null;

    const factor = OUTPUT_SIZE / CROP_BOX_SIZE;
    const drawWidth = renderedImageSize.width * factor;
    const drawHeight = renderedImageSize.height * factor;
    const drawX = (CROP_BOX_SIZE / 2 - renderedImageSize.width / 2 + offset.x) * factor;
    const drawY = (CROP_BOX_SIZE / 2 - renderedImageSize.height / 2 + offset.y) * factor;

    context.imageSmoothingEnabled = true;
    context.imageSmoothingQuality = 'high';
    context.clearRect(0, 0, OUTPUT_SIZE, OUTPUT_SIZE);
    context.drawImage(image, drawX, drawY, drawWidth, drawHeight);

    const nextMimeType = normalizeMimeType(mimeType);
    const blob = await new Promise<Blob | null>((resolve) => {
      canvas.toBlob(resolve, nextMimeType, nextMimeType === 'image/png' ? undefined : 0.92);
    });

    if (!blob) return null;

    const extension = extensionFromMimeType(nextMimeType);
    return new File([blob], `${baseNameFromFileName(fileName)}.${extension}`, { type: nextMimeType });
  }, [fileName, imageSize, imageSrc, mimeType, offset.x, offset.y, renderedImageSize.height, renderedImageSize.width]);

  const handleConfirm = useCallback(async () => {
    const file = await buildCroppedFile();
    if (!file) return;
    await onConfirm(file);
  }, [buildCroppedFile, onConfirm]);

  if (!open || !imageSrc) return null;

  return (
    <div className="fixed inset-0 z-[2500] flex items-center justify-center bg-slate-950/55 p-4 backdrop-blur-[2px]">
      <div className="relative flex max-h-[92vh] w-full max-w-6xl flex-col overflow-hidden rounded-[32px] border border-slate-200 bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-5">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">Кадрирование фото</div>
            <h2 className="mt-2 text-2xl font-semibold text-slate-900">Настройте область аватара</h2>
            <p className="mt-2 max-w-2xl text-sm leading-6 text-slate-600">
              Перетащите изображение внутри круга и подберите масштаб. Справа показано, как фото будет выглядеть в круглых аватарах интерфейса.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={submitting}
            className="inline-flex h-11 w-11 items-center justify-center rounded-2xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
            aria-label="Закрыть окно кадрирования"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="grid gap-6 overflow-y-auto px-6 py-6 lg:grid-cols-[minmax(360px,1fr)_320px]">
          <div className="space-y-5">
            <div
              className="relative mx-auto h-[320px] w-[320px] overflow-hidden rounded-[36px] bg-slate-100 shadow-inner ring-1 ring-slate-200 sm:h-[360px] sm:w-[360px]"
              onPointerDown={handlePointerDown}
              onPointerMove={handlePointerMove}
              onPointerUp={stopDragging}
              onPointerCancel={stopDragging}
              onWheel={handleWheel}
              style={{ touchAction: 'none' }}
            >
              <div className="absolute inset-0 flex items-center justify-center">
                <img
                  src={imageSrc}
                  alt="Подготовка фото профиля"
                  className="pointer-events-none absolute select-none object-cover"
                  draggable={false}
                  onLoad={(event) =>
                    setImageSize({
                      width: event.currentTarget.naturalWidth,
                      height: event.currentTarget.naturalHeight,
                    })
                  }
                  style={imageStyle}
                />
              </div>

              <div className="pointer-events-none absolute inset-0 bg-slate-950/28 [mask-image:radial-gradient(circle_at_center,transparent_0,transparent_45%,black_46%)]" />
              <div className="pointer-events-none absolute left-1/2 top-1/2 h-[78%] w-[78%] -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-white shadow-[0_0_0_9999px_rgba(15,23,42,0.18)]" />
              <div className="pointer-events-none absolute bottom-4 left-1/2 flex -translate-x-1/2 items-center gap-2 rounded-full bg-white/90 px-3 py-2 text-xs font-medium text-slate-700 shadow-sm">
                <Move className="h-3.5 w-3.5" />
                Потяните фото мышкой, чтобы выбрать нужную область
              </div>
            </div>

            <div className="rounded-3xl border border-slate-200 bg-slate-50 p-5">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <div className="text-sm font-semibold text-slate-900">Масштаб</div>
                  <div className="mt-1 text-sm text-slate-500">Увеличьте фото, если нужно приблизить лицо или логотип.</div>
                </div>
                <button
                  type="button"
                  onClick={() => {
                    setZoom(1);
                    setOffset({ x: 0, y: 0 });
                  }}
                  className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-100"
                >
                  <RotateCcw className="h-4 w-4" />
                  Сбросить
                </button>
              </div>

              <div className="mt-5 flex items-center gap-4">
                <button
                  type="button"
                  onClick={() => handleZoomChange(zoom - 0.1)}
                  className="inline-flex h-11 w-11 items-center justify-center rounded-2xl border border-slate-300 bg-white text-slate-700 transition hover:bg-slate-100"
                  aria-label="Уменьшить масштаб"
                >
                  <ZoomOut className="h-4 w-4" />
                </button>

                <input
                  type="range"
                  min={1}
                  max={3}
                  step={0.01}
                  value={zoom}
                  onChange={(event) => handleZoomChange(Number(event.target.value))}
                  className="h-2 w-full cursor-pointer appearance-none rounded-full bg-slate-200 accent-blue-600"
                />

                <button
                  type="button"
                  onClick={() => handleZoomChange(zoom + 0.1)}
                  className="inline-flex h-11 w-11 items-center justify-center rounded-2xl border border-slate-300 bg-white text-slate-700 transition hover:bg-slate-100"
                  aria-label="Увеличить масштаб"
                >
                  <ZoomIn className="h-4 w-4" />
                </button>
              </div>

              <div className="mt-3 text-sm font-medium text-slate-600">Текущий масштаб: {Math.round(zoom * 100)}%</div>
            </div>
          </div>

          <div className="space-y-5">
            <div className="rounded-3xl border border-slate-200 bg-slate-50 p-5">
              <div className="text-sm font-semibold text-slate-900">Предпросмотр</div>
              <div className="mt-1 text-sm text-slate-500">Так фотография будет выглядеть в круглых аватарах CRM.</div>

              <div className="mt-6 flex items-end gap-5">
                {[PREVIEW_LARGE_SIZE, PREVIEW_SMALL_SIZE].map((size) => (
                  <div key={size} className="space-y-2 text-center">
                    <div
                      className="relative overflow-hidden rounded-full border border-slate-200 bg-white shadow-sm"
                      style={{ width: `${size}px`, height: `${size}px` }}
                    >
                      <img
                        src={imageSrc}
                        alt="Предпросмотр аватара"
                        className="pointer-events-none absolute select-none object-cover"
                        draggable={false}
                        style={{
                          width: `${(renderedImageSize.width / CROP_BOX_SIZE) * size}px`,
                          height: `${(renderedImageSize.height / CROP_BOX_SIZE) * size}px`,
                          left: `${size / 2 - ((renderedImageSize.width / CROP_BOX_SIZE) * size) / 2 + (offset.x / CROP_BOX_SIZE) * size}px`,
                          top: `${size / 2 - ((renderedImageSize.height / CROP_BOX_SIZE) * size) / 2 + (offset.y / CROP_BOX_SIZE) * size}px`,
                        }}
                      />
                    </div>
                    <div className="text-xs font-medium text-slate-500">{size === PREVIEW_LARGE_SIZE ? 'Крупный' : 'Малый'}</div>
                  </div>
                ))}
              </div>
            </div>

            <div className="rounded-3xl border border-slate-200 bg-white p-5">
              <div className="text-sm font-semibold text-slate-900">Подсказка</div>
              <ul className="mt-3 space-y-2 text-sm leading-6 text-slate-600">
                <li>Старайтесь держать лицо или ключевую часть изображения ближе к центру круга.</li>
                <li>Если объект крупный, чуть уменьшите масштаб и подвиньте его внутри круга.</li>
                <li>Если объект мелкий, приблизьте изображение ползунком масштаба.</li>
              </ul>
            </div>
          </div>
        </div>

        <div className="flex flex-wrap items-center justify-end gap-3 border-t border-slate-200 px-6 py-5">
          <button
            type="button"
            onClick={onClose}
            disabled={submitting}
            className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => void handleConfirm()}
            disabled={submitting || !imageSize}
            className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
            Сохранить фото
          </button>
        </div>
      </div>
    </div>
  );
}
