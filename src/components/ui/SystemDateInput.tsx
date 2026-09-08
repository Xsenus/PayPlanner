import { CalendarDays, ChevronLeft, ChevronRight, Clock3, X } from 'lucide-react';
import {
  forwardRef,
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type InputHTMLAttributes,
} from 'react';
import { createPortal } from 'react-dom';

type SupportedDateInputType = 'date' | 'datetime-local' | 'time';

export interface SystemDateInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  type?: SupportedDateInputType;
  emptyLabel?: string;
  displayClassName?: string;
  showInlineClear?: boolean;
}

type MenuPosition = {
  left: number;
  top: number;
  width: number;
};

type CalendarView = 'days' | 'monthYear';

const MONTHS = [
  'Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь',
  'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь',
];
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
const VIEWPORT_GAP = 8;
const PANEL_WIDTH = 320;
const MIN_CALENDAR_YEAR = 1000;
const MAX_CALENDAR_YEAR = 9999;

// eslint-disable-next-line react-refresh/only-export-components -- расчёт позиции экспортируется для граничных responsive-тестов.
export function resolveDateMenuTop(
  fieldTop: number,
  fieldBottom: number,
  panelHeight: number,
  viewportHeight: number,
) {
  const spaceBelow = viewportHeight - fieldBottom - VIEWPORT_GAP;
  const spaceAbove = fieldTop - VIEWPORT_GAP;
  const preferredTop = spaceBelow >= panelHeight || spaceBelow >= spaceAbove
    ? fieldBottom + 6
    : fieldTop - panelHeight - 6;
  const maximumTop = Math.max(VIEWPORT_GAP, viewportHeight - panelHeight - VIEWPORT_GAP);

  return Math.min(Math.max(VIEWPORT_GAP, preferredTop), maximumTop);
}

function pad(value: number) {
  return String(value).padStart(2, '0');
}

function localDateValue(date: Date) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function validDateParts(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(year, month - 1, day);
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
    ? { year, month, day }
    : null;
}

function splitValue(value: string, type: SupportedDateInputType) {
  if (type === 'time') {
    const [hour = '00', minute = '00'] = value.split(':');
    return { date: '', hour: hour.slice(0, 2), minute: minute.slice(0, 2) };
  }
  const [date = '', time = ''] = value.split('T');
  const [hour = '09', minute = '00'] = time.split(':');
  return { date, hour: hour.slice(0, 2), minute: minute.slice(0, 2) };
}

function formatDisplayValue(value: string, type: SupportedDateInputType, emptyLabel: string) {
  if (!value) return emptyLabel;
  if (type === 'time') return value.slice(0, 5);
  const parts = splitValue(value, type);
  const date = validDateParts(parts.date);
  if (!date) return value;
  const formattedDate = `${pad(date.day)}.${pad(date.month)}.${date.year}`;
  return type === 'datetime-local' ? `${formattedDate}, ${parts.hour}:${parts.minute}` : formattedDate;
}

function clampNumber(value: string, min: number, max: number) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return min;
  return Math.min(max, Math.max(min, Math.trunc(parsed)));
}

function isValueWithinBounds(value: string, min?: string | number, max?: string | number) {
  const minimum = min == null ? '' : String(min);
  const maximum = max == null ? '' : String(max);
  return (!minimum || value >= minimum) && (!maximum || value <= maximum);
}

export const SystemDateInput = forwardRef<HTMLInputElement, SystemDateInputProps>(function SystemDateInput(
  {
    type = 'date',
    value,
    defaultValue,
    onChange,
    className = '',
    disabled = false,
    readOnly = false,
    min,
    max,
    id,
    name,
    required,
    autoFocus = false,
    emptyLabel = 'Выберите дату',
    displayClassName = '',
    showInlineClear = true,
    'aria-label': ariaLabel,
    ...inputProps
  },
  forwardedRef,
) {
  const generatedId = useId();
  const controlId = `system-date-${generatedId.replace(/:/g, '')}`;
  const nativeRef = useRef<HTMLInputElement | null>(null);
  const buttonRef = useRef<HTMLDivElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const controlled = value !== undefined;
  const [uncontrolledValue, setUncontrolledValue] = useState(() => String(defaultValue ?? ''));
  const currentValue = controlled ? String(value ?? '') : uncontrolledValue;
  const initialParts = splitValue(currentValue, type);
  const today = useMemo(() => new Date(), []);
  const initialDate = validDateParts(initialParts.date);
  const [open, setOpen] = useState(false);
  const [draftDate, setDraftDate] = useState(initialParts.date);
  const [draftHour, setDraftHour] = useState(initialParts.hour);
  const [draftMinute, setDraftMinute] = useState(initialParts.minute);
  const [viewYear, setViewYear] = useState(initialDate?.year ?? today.getFullYear());
  const [viewMonth, setViewMonth] = useState((initialDate?.month ?? today.getMonth() + 1) - 1);
  const [yearInputValue, setYearInputValue] = useState(String(initialDate?.year ?? today.getFullYear()));
  const [calendarView, setCalendarView] = useState<CalendarView>('days');
  const [position, setPosition] = useState<MenuPosition | null>(null);
  const [inferredLabel, setInferredLabel] = useState('');

  const setNativeRef = useCallback((node: HTMLInputElement | null) => {
    nativeRef.current = node;
    if (typeof forwardedRef === 'function') forwardedRef(node);
    else if (forwardedRef) forwardedRef.current = node;
  }, [forwardedRef]);

  const openPicker = useCallback(() => {
    if (disabled || readOnly) return;
    const parts = splitValue(currentValue, type);
    const selectedDate = validDateParts(parts.date);
    setDraftDate(parts.date || (type === 'time' ? '' : localDateValue(today)));
    setDraftHour(parts.hour);
    setDraftMinute(parts.minute);
    setViewYear(selectedDate?.year ?? today.getFullYear());
    setViewMonth((selectedDate?.month ?? today.getMonth() + 1) - 1);
    setYearInputValue(String(selectedDate?.year ?? today.getFullYear()));
    setCalendarView('days');
    setOpen(true);
  }, [currentValue, disabled, readOnly, today, type]);

  const emitChange = useCallback((nextValue: string) => {
    if (!controlled) setUncontrolledValue(nextValue);
    const nativeInput = nativeRef.current;
    if (!nativeInput) return;

    const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    valueSetter?.call(nativeInput, nextValue);
    nativeInput.dispatchEvent(new Event('input', { bubbles: true }));
  }, [controlled]);

  const apply = useCallback(() => {
    const nextValue = type === 'time'
      ? `${pad(clampNumber(draftHour, 0, 23))}:${pad(clampNumber(draftMinute, 0, 59))}`
      : type === 'datetime-local'
        ? `${draftDate}T${pad(clampNumber(draftHour, 0, 23))}:${pad(clampNumber(draftMinute, 0, 59))}`
        : draftDate;
    if (!nextValue || !isValueWithinBounds(nextValue, min, max)) return;
    emitChange(nextValue);
    setOpen(false);
    requestAnimationFrame(() => buttonRef.current?.focus());
  }, [draftDate, draftHour, draftMinute, emitChange, max, min, type]);

  const clear = useCallback(() => {
    emitChange('');
    setOpen(false);
    requestAnimationFrame(() => buttonRef.current?.focus());
  }, [emitChange]);

  const computePosition = useCallback(() => {
    const button = buttonRef.current;
    const panel = panelRef.current;
    if (!button || !panel) return;
    const rect = button.getBoundingClientRect();
    const width = Math.min(PANEL_WIDTH, window.innerWidth - VIEWPORT_GAP * 2);
    const height = panel.getBoundingClientRect().height;
    const left = Math.min(Math.max(VIEWPORT_GAP, rect.left), window.innerWidth - width - VIEWPORT_GAP);
    const top = resolveDateMenuTop(rect.top, rect.bottom, height, window.innerHeight);
    setPosition({ left, top, width });
  }, []);

  useEffect(() => {
    if (autoFocus && !disabled) buttonRef.current?.focus();
  }, [autoFocus, disabled]);

  useLayoutEffect(() => {
    if (ariaLabel) return;
    const input = nativeRef.current;
    const root = input?.closest('[data-system-date-root]');
    const wrappingLabel = input?.closest('label');
    if (!root || !wrappingLabel) return;
    const labelText = Array.from(wrappingLabel.childNodes)
      .filter((node) => node !== root)
      .map((node) => node.textContent ?? '')
      .join(' ')
      .replace(/\s+/g, ' ')
      .trim();
    if (labelText) setInferredLabel(labelText);
  }, [ariaLabel]);

  useEffect(() => {
    if (!open) return;
    const frame = requestAnimationFrame(computePosition);
    const onViewportChange = () => computePosition();
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (!panelRef.current?.contains(target) && !buttonRef.current?.contains(target)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        buttonRef.current?.focus();
      }
    };
    window.addEventListener('resize', onViewportChange);
    window.addEventListener('scroll', onViewportChange, true);
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener('resize', onViewportChange);
      window.removeEventListener('scroll', onViewportChange, true);
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [computePosition, open]);

  const days = useMemo(() => {
    if (type === 'time') return [];
    const firstDay = new Date(viewYear, viewMonth, 1);
    const gridStart = new Date(viewYear, viewMonth, 1 - ((firstDay.getDay() + 6) % 7));
    return Array.from({ length: 42 }, (_, index) => {
      const date = new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + index);
      return {
        key: localDateValue(date),
        day: date.getDate(),
        inMonth: date.getMonth() === viewMonth,
        today: localDateValue(date) === localDateValue(today),
      };
    });
  }, [today, type, viewMonth, viewYear]);

  const changeMonth = (offset: number) => {
    const next = new Date(viewYear, viewMonth + offset, 1);
    setViewYear(next.getFullYear());
    setViewMonth(next.getMonth());
  };

  const minimumDate = type === 'time' || min == null ? null : validDateParts(String(min).slice(0, 10));
  const maximumDate = type === 'time' || max == null ? null : validDateParts(String(max).slice(0, 10));
  const changeViewYear = (nextYear: number) => {
    if (!Number.isInteger(nextYear)) return;
    const minimumYear = minimumDate?.year ?? MIN_CALENDAR_YEAR;
    const maximumYear = maximumDate?.year ?? MAX_CALENDAR_YEAR;
    const clampedYear = Math.min(maximumYear, Math.max(minimumYear, nextYear));
    setViewYear(clampedYear);
    setYearInputValue(String(clampedYear));
  };
  const handleYearInputChange = (nextValue: string) => {
    if (!/^\d{0,4}$/.test(nextValue)) return;
    setYearInputValue(nextValue);
    const nextYear = Number(nextValue);
    if (nextValue && Number.isInteger(nextYear) && nextYear >= MIN_CALENDAR_YEAR) {
      const minimumYear = minimumDate?.year ?? MIN_CALENDAR_YEAR;
      const maximumYear = maximumDate?.year ?? MAX_CALENDAR_YEAR;
      setViewYear(Math.min(maximumYear, Math.max(minimumYear, nextYear)));
    }
  };
  const normalizeYearInput = () => changeViewYear(viewYear);
  const isMonthUnavailable = (monthIndex: number) => {
    const monthStart = `${pad(viewYear)}-${pad(monthIndex + 1)}-01`;
    const monthEnd = `${pad(viewYear)}-${pad(monthIndex + 1)}-${pad(new Date(viewYear, monthIndex + 1, 0).getDate())}`;
    const minimum = minimumDate ? `${pad(minimumDate.year)}-${pad(minimumDate.month)}-${pad(minimumDate.day)}` : '';
    const maximum = maximumDate ? `${pad(maximumDate.year)}-${pad(maximumDate.month)}-${pad(maximumDate.day)}` : '';
    return Boolean((minimum && monthEnd < minimum) || (maximum && monthStart > maximum));
  };

  const nativeLabel = ariaLabel || inferredLabel || inputProps.title || name || 'Дата';
  const panel = open ? (
    <div
      ref={panelRef}
      role="dialog"
      aria-label={type === 'time' ? 'Выбор времени' : 'Выбор даты'}
      className="fixed z-[5100] max-h-[calc(100vh-1rem)] overflow-y-auto rounded-2xl border border-slate-200 bg-white p-3 shadow-2xl"
      style={position ?? { left: VIEWPORT_GAP, top: VIEWPORT_GAP, width: PANEL_WIDTH }}
    >
      {type !== 'time' ? (
        <>
          <div className="mb-2 flex items-center justify-between gap-2">
            <button
              type="button"
              onClick={() => calendarView === 'days' ? changeMonth(-1) : changeViewYear(viewYear - 1)}
              className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-slate-500 hover:bg-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-200"
              aria-label={calendarView === 'days' ? 'Предыдущий месяц' : 'Предыдущий год'}
            >
              <ChevronLeft className="h-4 w-4" />
            </button>
            {calendarView === 'days' ? (
              <button
                type="button"
                onClick={() => { setYearInputValue(String(viewYear)); setCalendarView('monthYear'); }}
                className="inline-flex h-9 min-w-0 flex-1 items-center justify-center rounded-lg px-3 text-sm font-semibold text-slate-800 hover:bg-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-200"
                aria-label="Выбрать месяц и год"
              >
                {MONTHS[viewMonth]} {viewYear}
              </button>
            ) : (
              <label className="flex h-9 min-w-0 flex-1 items-center justify-center gap-2 rounded-lg bg-slate-50 px-2 text-xs font-medium text-slate-500">
                Год
                <input
                  type="number"
                  inputMode="numeric"
                  aria-label="Год"
                  min={minimumDate?.year ?? MIN_CALENDAR_YEAR}
                  max={maximumDate?.year ?? MAX_CALENDAR_YEAR}
                  value={yearInputValue}
                  onChange={(event) => handleYearInputChange(event.target.value)}
                  onBlur={normalizeYearInput}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter') {
                      event.preventDefault();
                      normalizeYearInput();
                    }
                  }}
                  onFocus={(event) => event.currentTarget.select()}
                  className="h-7 w-20 rounded-md border border-slate-200 bg-white px-2 text-center text-sm font-semibold text-slate-800 outline-none focus:border-blue-500 focus:ring-2 focus:ring-blue-100"
                />
              </label>
            )}
            <button
              type="button"
              onClick={() => calendarView === 'days' ? changeMonth(1) : changeViewYear(viewYear + 1)}
              className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-slate-500 hover:bg-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-200"
              aria-label={calendarView === 'days' ? 'Следующий месяц' : 'Следующий год'}
            >
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
          {calendarView === 'days' ? (
            <div className="grid grid-cols-7 gap-1 text-center">
              {WEEKDAYS.map((day) => <div key={day} className="py-1 text-[11px] font-medium text-slate-400">{day}</div>)}
              {days.map((day) => {
                const candidate = type === 'datetime-local'
                  ? `${day.key}T${pad(clampNumber(draftHour, 0, 23))}:${pad(clampNumber(draftMinute, 0, 59))}`
                  : day.key;
                const unavailable = !isValueWithinBounds(candidate, min, max);
                const selected = day.key === draftDate;
                return (
                  <button
                    key={day.key}
                    type="button"
                    aria-label={formatDisplayValue(day.key, 'date', day.key)}
                    disabled={unavailable}
                    onClick={() => setDraftDate(day.key)}
                    className={[
                      'relative h-8 rounded-lg text-xs transition focus:outline-none focus:ring-2 focus:ring-blue-200',
                      selected ? 'bg-blue-600 font-semibold text-white' : day.inMonth ? 'text-slate-700 hover:bg-blue-50' : 'text-slate-300 hover:bg-slate-50',
                      day.today && !selected ? 'ring-1 ring-inset ring-blue-300' : '',
                      unavailable ? 'cursor-not-allowed opacity-30' : '',
                    ].filter(Boolean).join(' ')}
                  >
                    {day.day}
                  </button>
                );
              })}
            </div>
          ) : (
            <div aria-label="Выбор месяца и года" className="grid grid-cols-3 gap-2 py-1">
              {MONTHS.map((month, monthIndex) => {
                const unavailable = isMonthUnavailable(monthIndex);
                const selected = monthIndex === viewMonth;
                return (
                  <button
                    key={month}
                    type="button"
                    disabled={unavailable}
                    onClick={() => { setViewMonth(monthIndex); setCalendarView('days'); }}
                    className={[
                      'min-h-10 rounded-lg px-2 py-2 text-xs font-medium transition focus:outline-none focus:ring-2 focus:ring-blue-200',
                      selected ? 'bg-blue-600 text-white' : 'bg-slate-50 text-slate-700 hover:bg-blue-50 hover:text-blue-700',
                      unavailable ? 'cursor-not-allowed opacity-30' : '',
                    ].filter(Boolean).join(' ')}
                  >
                    {month}
                  </button>
                );
              })}
            </div>
          )}
        </>
      ) : null}

      {type !== 'date' ? (
        <div className={`${type === 'datetime-local' ? 'mt-3 border-t border-slate-100 pt-3' : ''} flex items-center gap-2`}>
          <Clock3 className="h-4 w-4 text-slate-400" />
          <span className="text-xs font-medium text-slate-500">Время</span>
          <input aria-label="Часы" type="number" inputMode="numeric" min={0} max={23} value={draftHour} onChange={(event) => setDraftHour(event.target.value)} className="ml-auto h-9 w-14 rounded-lg border border-slate-200 text-center text-sm outline-none focus:border-blue-500 focus:ring-2 focus:ring-blue-100" />
          <span className="font-semibold text-slate-400">:</span>
          <input aria-label="Минуты" type="number" inputMode="numeric" min={0} max={59} value={draftMinute} onChange={(event) => setDraftMinute(event.target.value)} className="h-9 w-14 rounded-lg border border-slate-200 text-center text-sm outline-none focus:border-blue-500 focus:ring-2 focus:ring-blue-100" />
        </div>
      ) : null}

      <div className="mt-3 flex items-center justify-between border-t border-slate-100 pt-3">
        <div className="flex items-center gap-1">
          {!required ? <button type="button" onClick={clear} className="rounded-lg px-2 py-1.5 text-xs font-medium text-slate-500 hover:bg-slate-100">Очистить</button> : null}
          {type !== 'time' ? (
            <button type="button" onClick={() => { const next = localDateValue(today); setDraftDate(next); setViewYear(today.getFullYear()); setViewMonth(today.getMonth()); setCalendarView('days'); }} className="rounded-lg px-2 py-1.5 text-xs font-medium text-blue-600 hover:bg-blue-50">Сегодня</button>
          ) : null}
        </div>
        <button type="button" onClick={apply} disabled={type !== 'time' && (!draftDate || calendarView === 'monthYear')} className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white hover:bg-slate-800 disabled:opacity-40">Применить</button>
      </div>
    </div>
  ) : null;

  return (
    <span data-system-date-root className="relative block w-full">
      <input
        {...inputProps}
        ref={setNativeRef}
        id={id}
        type={type}
        value={currentValue}
        onChange={onChange}
        min={min}
        max={max}
        name={name}
        required={required}
        disabled={disabled}
        readOnly={readOnly}
        aria-label={(ariaLabel ?? inferredLabel) || inputProps.title}
        onFocus={(event) => {
          inputProps.onFocus?.(event);
          openPicker();
        }}
        tabIndex={-1}
        className="pointer-events-none absolute h-px w-px opacity-0"
      />
      <div
        ref={buttonRef}
        id={controlId}
        role="button"
        tabIndex={disabled ? -1 : 0}
        aria-disabled={disabled || undefined}
        aria-required={required || undefined}
        aria-label={`Выбрать: ${nativeLabel}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={openPicker}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            openPicker();
          }
        }}
        className={[
          'inline-flex min-h-10 w-full items-center justify-between gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-left text-sm text-slate-700 shadow-sm outline-none transition',
          'hover:border-slate-300 hover:bg-slate-50 focus:border-blue-500 focus:ring-2 focus:ring-blue-100',
          disabled ? 'cursor-not-allowed bg-slate-100 text-slate-400' : '',
          readOnly ? 'cursor-default bg-slate-50' : '',
          className,
        ].filter(Boolean).join(' ')}
      >
        <span className={`min-w-0 flex-1 truncate ${currentValue ? '' : 'text-slate-400'} ${displayClassName}`}>
          {formatDisplayValue(currentValue, type, type === 'time' ? 'Выберите время' : emptyLabel)}
        </span>
        <span className="flex shrink-0 items-center gap-1 text-slate-400">
          {showInlineClear && currentValue && !required && !readOnly ? (
            <span
              aria-hidden="true"
              onClick={(event) => { event.stopPropagation(); clear(); }}
              className="rounded p-0.5 hover:bg-slate-200 hover:text-slate-600"
            >
              <X className="h-3.5 w-3.5" />
            </span>
          ) : null}
          {type === 'time' ? <Clock3 className="h-4 w-4" /> : <CalendarDays className="h-4 w-4" />}
        </span>
      </div>
      {open && typeof document !== 'undefined' ? createPortal(panel, document.body) : null}
    </span>
  );
});
