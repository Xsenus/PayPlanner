import { useEffect, useMemo, useRef, useState, useLayoutEffect } from 'react';
import { Calendar, X } from 'lucide-react';

type YM = string;
export type MonthRange = { from?: YM; to?: YM };

const ruMonths = [
  'янв',
  'фев',
  'мар',
  'апр',
  'май',
  'июн',
  'июл',
  'авг',
  'сен',
  'окт',
  'ноя',
  'дек',
];

function ymNow(): YM {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
function ymAdd(ym: YM, diff: number): YM {
  const [y, m] = ym.split('-').map(Number);
  const d = new Date(y, m - 1 + diff, 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
function ymMin(a: YM, b: YM) {
  return a < b ? a : b;
}
function ymMax(a: YM, b: YM) {
  return a > b ? a : b;
}

function formatYM(ym: YM): string {
  const [y, m] = ym.split('-').map(Number);
  return `${ruMonths[m - 1]} ${y}`;
}
function labelOfRangeCompact(r: MonthRange) {
  if (!r.from && !r.to) return 'Период не задан';
  if (r.from && !r.to) return `с ${formatYM(r.from)}`;
  if (!r.from && r.to) return `по ${formatYM(r.to)}`;
  const [yf, mf] = r.from!.split('-').map(Number);
  const [yt, mt] = r.to!.split('-').map(Number);
  if (yf === yt) return `${ruMonths[mf - 1]} — ${ruMonths[mt - 1]} ${yt}`;
  return `${formatYM(r.from!)} — ${formatYM(r.to!)}`;
}

type PopPos = { left: number; top: number; width: number };
const VIEWPORT_PADDING = 8;

export function MonthRangePicker({
  value,
  onChange,
  yearsBack = 6,
  yearsForward = 1,
  className = '',
}: {
  value: MonthRange;
  onChange: (next: MonthRange) => void;
  yearsBack?: number;
  yearsForward?: number;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const btnRef = useRef<HTMLButtonElement | null>(null);
  const popRef = useRef<HTMLDivElement | null>(null);

  const [draft, setDraft] = useState<MonthRange>(value);
  useEffect(() => setDraft(value), [value]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    const onClick = (e: MouseEvent) => {
      const isMobile = window.matchMedia('(max-width: 767px)').matches;
      if (isMobile) return;
      if (!popRef.current) return;
      if (popRef.current.contains(e.target as Node)) return;
      if (btnRef.current?.contains(e.target as Node)) return;
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

  const now = ymNow();
  const years = useMemo(() => {
    const y = Number(now.slice(0, 4));
    const arr: number[] = [];
    for (let i = y - yearsBack; i <= y + yearsForward; i++) arr.push(i);
    return arr;
  }, [now, yearsBack, yearsForward]);

  const [leftYear, setLeftYear] = useState<number>(Number(now.slice(0, 4)));
  const [rightYear, setRightYear] = useState<number>(Number(now.slice(0, 4)));

  function toggleMonth(which: 'from' | 'to', y: number, mIdx: number) {
    const ym = `${y}-${String(mIdx + 1).padStart(2, '0')}`;
    const next: MonthRange = { ...draft, [which]: ym };
    if (next.from && next.to && next.from > next.to) {
      const a = ymMin(next.from, next.to);
      const b = ymMax(next.from, next.to);
      next.from = a;
      next.to = b;
    }
    setDraft(next);
  }

  function apply() {
    onChange(draft);
    setOpen(false);
  }
  function clear() {
    onChange({});
    setOpen(false);
  }

  const presets = [
    { name: 'Текущий месяц', calc: () => ({ from: now, to: now }) },
    {
      name: 'Прошлый месяц',
      calc: () => {
        const prev = ymAdd(now, -1);
        return { from: prev, to: prev };
      },
    },
    { name: '3 месяца', calc: () => ({ from: ymAdd(now, -2), to: now }) },
    { name: '6 месяцев', calc: () => ({ from: ymAdd(now, -5), to: now }) },
    { name: '12 месяцев', calc: () => ({ from: ymAdd(now, -11), to: now }) },
    {
      name: 'Текущий год',
      calc: () => ({ from: `${now.slice(0, 4)}-01`, to: `${now.slice(0, 4)}-12` }),
    },
  ];

  const MonthCell = ({ y, idx }: { y: number; idx: number }) => {
    const ym = `${y}-${String(idx + 1).padStart(2, '0')}`;
    const active = (draft.from && ym === draft.from) || (draft.to && ym === draft.to);
    const inSpan = draft.from && draft.to && ym > draft.from && ym < draft.to;

    return (
      <button
        type="button"
        onClick={() => {
          if (!draft.from) toggleMonth('from', y, idx);
          else if (!draft.to) toggleMonth('to', y, idx);
          else setDraft({ from: ym, to: undefined });
        }}
        className={[
          'h-10 md:h-8 min-w-[72px] md:min-w-0 px-3 md:px-2 rounded-lg text-base md:text-sm transition',
          'outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-1',
          inSpan && 'bg-blue-50',
          active ? 'bg-blue-600 text-white' : 'hover:bg-slate-100',
        ]
          .filter(Boolean)
          .join(' ')}
        aria-pressed={!!active}>
        {ruMonths[idx]}
      </button>
    );
  };

  const [popPos, setPopPos] = useState<PopPos | null>(null);

  function computeDesktopPopoverPosition() {
    const btn = btnRef.current;
    const pop = popRef.current;
    if (!btn || !pop) return;

    const btnRect = btn.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    const width = Math.min(680, vw - VIEWPORT_PADDING * 2);

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

  const sheet = (
    <div className="fixed inset-0 z-[60] md:hidden">
      <button
        aria-label="Закрыть выбор периода"
        onClick={() => setOpen(false)}
        className="absolute inset-0 bg-black/30 backdrop-blur-[1px]"
      />
      <div
        ref={popRef}
        className="absolute inset-x-0 bottom-0 max-h[85vh] max-h-[85vh] rounded-t-2xl bg-white shadow-2xl"
        role="dialog"
        aria-modal="true">
        <div className="flex justify-center py-2">
          <div className="h-1.5 w-12 rounded-full bg-slate-200" />
        </div>

        <div className="px-4 pb-4 pt-1 overflow-y-auto">
          <div className="mb-3 flex items-center justify-between">
            <div className="text-sm text-slate-500">
              Выбрано:{' '}
              <span className="font-medium text-slate-700">{labelOfRangeCompact(draft)}</span>
            </div>
            <button
              className="inline-flex items-center gap-1 rounded-full border border-slate-300 px-3 py-1.5 text-sm hover:bg-slate-50"
              onClick={clear}
              title="Сбросить период">
              <X className="h-4 w-4" /> Сбросить
            </button>
          </div>

          <div className="space-y-5">
            <div>
              <div className="mb-2 flex items-center justify-between">
                <label className="text-xs text-slate-500">Начало</label>
                <select
                  value={leftYear}
                  onChange={(e) => setLeftYear(Number(e.target.value))}
                  className="rounded-md border border-slate-300 px-3 py-2 text-sm">
                  {years.map((y) => (
                    <option key={y} value={y}>
                      {y}
                    </option>
                  ))}
                </select>
              </div>
              <div className="grid grid-cols-3 gap-2">
                {ruMonths.map((_, i) => (
                  <MonthCell key={i} y={leftYear} idx={i} />
                ))}
              </div>
            </div>

            <div>
              <div className="mb-2 flex items-center justify-between">
                <label className="text-xs text-slate-500">Конец</label>
                <select
                  value={rightYear}
                  onChange={(e) => setRightYear(Number(e.target.value))}
                  className="rounded-md border border-slate-300 px-3 py-2 text-sm">
                  {years.map((y) => (
                    <option key={y} value={y}>
                      {y}
                    </option>
                  ))}
                </select>
              </div>
              <div className="grid grid-cols-3 gap-2">
                {ruMonths.map((_, i) => (
                  <MonthCell key={i} y={rightYear} idx={i} />
                ))}
              </div>
            </div>

            <div>
              <div className="mb-2 text-xs font-medium text-slate-500">Быстрый выбор</div>
              <div className="flex flex-wrap gap-2">
                {presets.map((p) => (
                  <button
                    key={p.name}
                    type="button"
                    className="rounded-full border border-slate-300 px-3 py-1.5 text-sm hover:bg-slate-50"
                    onClick={() => setDraft(p.calc())}>
                    {p.name}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </div>

        <div className="sticky bottom-0 border-t bg-white px-4 py-3">
          <button
            type="button"
            className="w-full rounded-xl bg-blue-600 px-4 py-3 text-base font-medium text-white hover:bg-blue-700"
            onClick={apply}>
            Применить
          </button>
        </div>
      </div>
    </div>
  );

  const popover = (
    <div
      ref={popRef}
      className="fixed z-50 hidden rounded-xl border border-slate-200 bg-white p-4 shadow-lg md:block"
      role="dialog"
      aria-modal="true"
      style={{
        left: popPos?.left ?? -9999,
        top: popPos?.top ?? -9999,
        width:
          popPos?.width ??
          Math.min(
            680,
            typeof window !== 'undefined' ? window.innerWidth - VIEWPORT_PADDING * 2 : 680,
          ),
        maxHeight: `calc(100vh - ${VIEWPORT_PADDING * 2}px)`,
        overflowY: 'auto',
      }}>
      <div className="flex items-start gap-4">
        <div className="flex-1">
          <div className="mb-2 flex items-center justify-between">
            <select
              value={leftYear}
              onChange={(e) => setLeftYear(Number(e.target.value))}
              className="rounded-md border border-slate-300 px-2 py-1 text-sm">
              {years.map((y) => (
                <option key={y} value={y}>
                  {y}
                </option>
              ))}
            </select>
            <span className="text-xs text-slate-500">Начало</span>
          </div>
          <div className="grid grid-cols-4 gap-2">
            {ruMonths.map((_, i) => (
              <MonthCell key={i} y={leftYear} idx={i} />
            ))}
          </div>
        </div>

        <div className="flex-1">
          <div className="mb-2 flex items-center justify-between">
            <select
              value={rightYear}
              onChange={(e) => setRightYear(Number(e.target.value))}
              className="rounded-md border border-slate-300 px-2 py-1 text-sm">
              {years.map((y) => (
                <option key={y} value={y}>
                  {y}
                </option>
              ))}
            </select>
            <span className="text-xs text-slate-500">Конец</span>
          </div>
          <div className="grid grid-cols-4 gap-2">
            {ruMonths.map((_, i) => (
              <MonthCell key={i} y={rightYear} idx={i} />
            ))}
          </div>
        </div>

        <div className="w-40 border-l pl-4">
          <div className="mb-2 text-xs font-medium text-slate-500">Быстрый выбор</div>
          <div className="space-y-2">
            {presets.map((p) => (
              <button
                key={p.name}
                type="button"
                className="w-full rounded-md border border-slate-200 px-2 py-1 text-sm hover:bg-slate-50"
                onClick={() => setDraft(p.calc())}>
                {p.name}
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="mt-4 flex items-center justify-between">
        <div className="text-xs text-slate-500">
          Выбрано: <span className="font-medium text-slate-700">{labelOfRangeCompact(draft)}</span>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            className="rounded-lg border border-slate-300 px-3 py-1.5 text-sm hover:bg-slate-50"
            onClick={clear}
            title="Сбросить период">
            <span className="inline-flex items-center gap-1">
              <X className="h-3.5 w-3.5" /> Сбросить
            </span>
          </button>
          <button
            type="button"
            className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700"
            onClick={apply}>
            Применить
          </button>
        </div>
      </div>
    </div>
  );

  return (
    <div className={`relative ${className} w-full md:w-auto min-w-0`}>
      <button
        ref={btnRef}
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="w-full md:w-auto inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm hover:bg-slate-50"
        title="Выбрать период по месяцам">
        <Calendar className="h-4 w-4 shrink-0" />
        <span className="flex-1 truncate text-slate-700 md:flex-none md:max-w-[420px]">
          {labelOfRangeCompact(value)}
        </span>
      </button>

      {open && (
        <>
          {sheet}
          {popover}
        </>
      )}
    </div>
  );
}
