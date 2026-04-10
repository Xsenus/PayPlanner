import { Calendar, X } from 'lucide-react';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  PERIOD_PRESET_LABELS,
  PERIOD_PRESETS,
  formatPeriodLabel,
  resolvePeriod,
  type PeriodFilter,
  type PeriodPreset,
} from '../../utils/periodUtils';

type PopPos = { left: number; top: number; width: number };
const VIEWPORT_PADDING = 8;

type Accent = 'blue' | 'indigo';

const ACCENT_STYLES: Record<Accent, { ring: string; button: string; pill: string }> = {
  blue: {
    ring: 'focus:ring-blue-200 focus:border-blue-500',
    button: 'bg-blue-600 hover:bg-blue-500',
    pill: 'text-blue-700 border-blue-200 bg-blue-50',
  },
  indigo: {
    ring: 'focus:ring-indigo-200 focus:border-indigo-500',
    button: 'bg-indigo-600 hover:bg-indigo-500',
    pill: 'text-indigo-700 border-indigo-200 bg-indigo-50',
  },
};

export function DatePeriodPicker({
  value,
  onChange,
  className = '',
  accent = 'blue',
  label,
  emptyLabel,
}: {
  value: PeriodFilter;
  onChange: (next: PeriodFilter) => void;
  className?: string;
  accent?: Accent;
  label?: string;
  emptyLabel?: string;
}) {
  const [open, setOpen] = useState(false);
  const btnRef = useRef<HTMLButtonElement | null>(null);
  const popRef = useRef<HTMLDivElement | null>(null);

  const [draft, setDraft] = useState<PeriodFilter>(value);
  const [customRange, setCustomRange] = useState({ from: '', to: '' });

  useEffect(() => {
    setDraft(value);
    if (value.mode === 'custom') {
      setCustomRange({ from: value.from ?? '', to: value.to ?? '' });
    }
  }, [value]);

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => event.key === 'Escape' && setOpen(false);
    const onClick = (event: MouseEvent) => {
      const isMobile = window.matchMedia('(max-width: 767px)').matches;
      if (isMobile) return;
      if (!popRef.current) return;
      if (popRef.current.contains(event.target as Node)) return;
      if (btnRef.current?.contains(event.target as Node)) return;
      setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onClick);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onClick);
    };
  }, [open]);

  useEffect(() => {
    const isMobile = window.matchMedia('(max-width: 767px)').matches;
    if (open && isMobile) {
      const prev = document.body.style.overflow;
      document.body.style.overflow = 'hidden';
      return () => {
        document.body.style.overflow = prev;
      };
    }
  }, [open]);

  const resolvedRange = useMemo(() => resolvePeriod(draft), [draft]);
  const labelText = label ?? 'Период';
  const displayText = formatPeriodLabel(draft, new Date(), emptyLabel);

  const [popPos, setPopPos] = useState<PopPos | null>(null);

  function computeDesktopPopoverPosition() {
    const btn = btnRef.current;
    const pop = popRef.current;
    if (!btn || !pop) return;

    const btnRect = btn.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const width = Math.min(640, vw - VIEWPORT_PADDING * 2);

    const left = Math.min(Math.max(VIEWPORT_PADDING, btnRect.left), vw - width - VIEWPORT_PADDING);
    let top = btnRect.bottom + 8;
    const height = pop.getBoundingClientRect().height;

    if (top + height > vh - VIEWPORT_PADDING) {
      top = Math.max(VIEWPORT_PADDING, btnRect.top - height - 8);
    }

    setPopPos({ left, top, width });
  }

  useLayoutEffect(() => {
    if (!open) return;
    const isMobile = window.matchMedia('(max-width: 767px)').matches;
    if (isMobile) return;

    const raf = requestAnimationFrame(computeDesktopPopoverPosition);
    const onResize = () => computeDesktopPopoverPosition();
    const onScroll = () => computeDesktopPopoverPosition();

    window.addEventListener('resize', onResize);
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener('resize', onResize);
      window.removeEventListener('scroll', onScroll);
    };
  }, [open]);

  const accentStyles = ACCENT_STYLES[accent];

  const handleApply = () => {
    onChange(draft);
    setOpen(false);
  };

  const handleClear = () => {
    const cleared: PeriodFilter = { mode: 'custom', from: '', to: '' };
    setDraft(cleared);
    setCustomRange({ from: '', to: '' });
    onChange(cleared);
    setOpen(false);
  };

  const handlePresetSelect = (preset: PeriodPreset) => {
    setDraft({ mode: 'preset', preset });
  };

  const handleCustomChange = (next: Partial<typeof customRange>) => {
    const updated = { ...customRange, ...next };
    setCustomRange(updated);
    setDraft({ mode: 'custom', from: updated.from || undefined, to: updated.to || undefined });
  };

  const presetButtons = (
    <div className="grid gap-2 sm:grid-cols-2">
      {PERIOD_PRESETS.map((preset) => {
        const isActive = draft.mode === 'preset' && draft.preset === preset;
        return (
          <button
            key={preset}
            type="button"
            onClick={() => handlePresetSelect(preset)}
            className={[
              'rounded-lg border px-3 py-2 text-left text-sm font-medium transition',
              isActive ? `border-transparent ${accentStyles.pill}` : 'border-slate-200 bg-white text-slate-600 hover:bg-slate-50',
            ]
              .filter(Boolean)
              .join(' ')}
          >
            {PERIOD_PRESET_LABELS[preset]}
          </button>
        );
      })}
    </div>
  );

  const customInputs = (
    <div className="grid gap-3 sm:grid-cols-2">
      <label className="flex flex-col gap-1 text-xs text-slate-500">
        С
        <input
          type="date"
          value={customRange.from}
          onChange={(event) => handleCustomChange({ from: event.target.value })}
          className={`w-full rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700 focus:outline-none focus:ring-2 ${accentStyles.ring}`}
        />
      </label>
      <label className="flex flex-col gap-1 text-xs text-slate-500">
        По
        <input
          type="date"
          value={customRange.to}
          onChange={(event) => handleCustomChange({ to: event.target.value })}
          className={`w-full rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700 focus:outline-none focus:ring-2 ${accentStyles.ring}`}
        />
      </label>
    </div>
  );

  const panel = (
    <div
      ref={popRef}
      className="w-full rounded-2xl border border-slate-200 bg-white p-4 shadow-xl"
      role="dialog"
      aria-modal="true"
    >
      <div className="mb-3 flex items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="text-sm font-semibold text-slate-800">{labelText}</div>
          <div className="text-xs text-slate-500">Сейчас: {formatPeriodLabel(draft, new Date(), emptyLabel)}</div>
        </div>
        <button
          type="button"
          onClick={() => setOpen(false)}
          className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-slate-200 text-slate-500 hover:bg-slate-50"
          aria-label="Закрыть"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="mb-4">
        <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Быстрый выбор</div>
        {presetButtons}
      </div>

      <div className="mb-4">
        <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Пользовательский период</div>
        {customInputs}
      </div>

      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="text-xs text-slate-500">
          {resolvedRange.from || resolvedRange.to ? `Выбрано: ${displayText}` : 'Диапазон не выбран'}
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={handleClear}
            className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 hover:bg-slate-50"
          >
            Сбросить
          </button>
          <button
            type="button"
            onClick={handleApply}
            className={`rounded-lg px-4 py-2 text-sm font-semibold text-white ${accentStyles.button}`}
          >
            Применить
          </button>
        </div>
      </div>
    </div>
  );

  return (
    <div className={`flex flex-col gap-1 ${className}`}>
      {label ? <span className="text-sm font-medium text-gray-700">{label}</span> : null}
      <button
        ref={btnRef}
        type="button"
        onClick={() => setOpen((prev) => !prev)}
        className={`inline-flex w-full items-center justify-between gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 shadow-sm transition hover:bg-slate-50 focus:outline-none focus:ring-2 ${accentStyles.ring}`}
      >
        <span className="flex min-w-0 items-center gap-2">
          <Calendar className="h-4 w-4 text-slate-400" />
          <span className="truncate">{displayText}</span>
        </span>
        <span className="rounded-full border border-slate-200 px-2 py-0.5 text-[11px] uppercase text-slate-500">
          {draft.mode === 'preset' ? 'Авто' : 'Ручной'}
        </span>
      </button>

      {open ? (
        <div className="fixed inset-0 z-[70]">
          <button
            type="button"
            className="absolute inset-0 bg-black/20 backdrop-blur-[1px] md:hidden"
            aria-label="Закрыть"
            onClick={() => setOpen(false)}
          />
          <div
            className="absolute inset-0 hidden md:block"
            onClick={() => setOpen(false)}
          />
          <div
            className="absolute left-0 right-0 top-auto md:hidden"
            style={{ bottom: 0 }}
          >
            <div className="rounded-t-2xl bg-white p-4 shadow-2xl">{panel}</div>
          </div>
          <div
            className="absolute hidden md:block"
            style={popPos ? { left: popPos.left, top: popPos.top, width: popPos.width } : undefined}
          >
            {panel}
          </div>
        </div>
      ) : null}
    </div>
  );
}
