/** 0..9 -> '00'..'09' */
const pad = (n: number) => (n < 10 ? `0${n}` : `${n}`);

/** Форматирование локальной Date в 'YYYY-MM-DD' без часового пояса */
export function formatLocalYMD(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * Приводит любое значение (ISO, 'YYYY-MM-DD', Date, null) к значению для <input type="date">
 * Возвращает 'YYYY-MM-DD' или ''.
 */
export function toDateInputValue(value: string | Date | null | undefined): string {
  if (!value) return '';

  // Строки, которые уже начинаются с YYYY-MM-DD
  if (typeof value === 'string') {
    const m = value.match(/^(\d{4}-\d{2}-\d{2})/);
    if (m) return m[1];
  }

  // Пытаемся распарсить как Date и вернуть локальную дату
  const d = typeof value === 'string' ? new Date(value) : value;
  if (isNaN(d.getTime())) return '';

  return formatLocalYMD(d);
}

/**
 * Конвертирует значение из инпута ('YYYY-MM-DD') в строку для API.
 * Если бэку нужна именно дата без времени — возвращаем как есть.
 * Если нужен ISO в UTC на полуночь — раскомментируй вариант ниже.
 */
export function fromInputToApiDate(ymd: string | null | undefined): string | undefined {
  if (!ymd) return undefined;
  return ymd; // дата без времени

  // ВАРИАНТ ДЛЯ ISO (UTC 00:00:00)
  // const [y, m, d] = ymd.split('-').map(Number);
  // const iso = new Date(Date.UTC(y, (m ?? 1) - 1, d ?? 1, 0, 0, 0)).toISOString();
  // return iso;
}

/** Сегодня (локально) в 'YYYY-MM-DD' */
export function todayYMD(): string {
  return formatLocalYMD(new Date());
}

/** Превращает ISO/'YYYY-MM-DD'/Date в 'ДД.ММ.ГГГГ' */
export function toRuDate(value: string | Date | null | undefined): string {
  const ymd = toDateInputValue(value); // нормализуем к 'YYYY-MM-DD' или ''
  if (!ymd) return '';
  const [y, m, d] = ymd.split('-');
  return `${d}.${m}.${y}`;
}
