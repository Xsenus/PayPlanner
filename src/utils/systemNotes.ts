import { INSTALLMENT_PAYMENT_MARKER } from './installmentPlan';
import { PARTIAL_REMAINDER_PAYMENT_MARKER, PARTIAL_REMAINDER_SOURCE_MARKER } from './invoicePayments';

export const INVOICE_LINE_ITEMS_MARKER = '[pp:invoice-items]';

const HIDDEN_SYSTEM_NOTE_MARKERS = [
  INSTALLMENT_PAYMENT_MARKER,
  PARTIAL_REMAINDER_PAYMENT_MARKER,
  PARTIAL_REMAINDER_SOURCE_MARKER,
  INVOICE_LINE_ITEMS_MARKER,
] as const;
const SYSTEM_NOTE_PREFIXES = ['system:', 'системно:'] as const;
const WINDOWS_1251_DECODED_80_TO_FF =
  'ЂЃ‚ѓ„…†‡€‰Љ‹ЊЌЋЏђ‘’“”•–—\u0098™љ›њќћџ\u00A0ЎўЈ¤Ґ¦§Ё©Є«¬\u00AD®Ї°±Ііґµ¶·ё№є»јЅѕїАБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдежзийклмнопрстуфхцчшщъыьэюя';

const WINDOWS_1251_CHAR_TO_BYTE = new Map<string, number>(
  Array.from(WINDOWS_1251_DECODED_80_TO_FF).map((char, index) => [char, index + 0x80]),
);
const MOJIBAKE_CYRILLIC_PATTERN = /[РС][\u0080-\u04FF]|[\u0402-\u045F][РС]/;
const INSTALLMENT_MONEY_LINE_PATTERN = /^(Основной долг|Проценты|Остаток):\s*([0-9\s.,-]+)(?:\s*₽)?$/i;
const MONEY_FORMATTER = new Intl.NumberFormat('ru-RU', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

function stripListPrefix(line: string): string {
  return line.startsWith('\u2022 ') ? line.slice(2) : line;
}

function stripBom(line: string): string {
  return line.replace(/^\uFEFF/, '');
}

function repairWindows1251Mojibake(value: string): string {
  if (!MOJIBAKE_CYRILLIC_PATTERN.test(value)) {
    return value;
  }

  const bytes: number[] = [];
  for (const char of value) {
    const code = char.charCodeAt(0);
    if (code <= 0x7f) {
      bytes.push(code);
      continue;
    }

    const byte = WINDOWS_1251_CHAR_TO_BYTE.get(char);
    if (byte === undefined) {
      return value;
    }

    bytes.push(byte);
  }

  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(new Uint8Array(bytes));
  } catch {
    return value;
  }
}

function stripSystemNotePrefix(line: string): string {
  let normalized = stripBom(stripListPrefix(line.trim()));

  for (const prefix of SYSTEM_NOTE_PREFIXES) {
    if (normalized.toLowerCase().startsWith(prefix)) {
      normalized = normalized.slice(prefix.length).trim();
      break;
    }
  }

  return repairWindows1251Mojibake(normalized);
}

function safeJsonParse(value: string): unknown | null {
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function parseLooseJson(value: string): unknown | null {
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }

  const direct = safeJsonParse(trimmed);
  if (direct !== null) {
    if (typeof direct === 'string') {
      return safeJsonParse(direct) ?? direct;
    }

    return direct;
  }

  if (!trimmed.includes('\\"') && !trimmed.includes('\\\\u')) {
    return null;
  }

  const unescaped = trimmed.replace(/\\"/g, '"').replace(/\\\\/g, '\\');
  if (unescaped === trimmed) {
    return null;
  }

  const reparsed = safeJsonParse(unescaped);
  if (reparsed !== null) {
    if (typeof reparsed === 'string') {
      return safeJsonParse(reparsed) ?? reparsed;
    }

    return reparsed;
  }

  return null;
}

function looksLikeInvoiceItemsPayload(line: string): boolean {
  const parsed = parseLooseJson(line);
  if (!Array.isArray(parsed) || parsed.length === 0) {
    return false;
  }

  return parsed.every((item) => {
    if (!item || typeof item !== 'object') {
      return false;
    }

    const candidate = item as Record<string, unknown>;
    const name = candidate.Name ?? candidate.name;
    const quantity = candidate.Quantity ?? candidate.quantity;
    const unit = candidate.Unit ?? candidate.unit;
    const price = candidate.Price ?? candidate.price;

    return (
      typeof name === 'string' &&
      typeof quantity === 'number' &&
      typeof unit === 'string' &&
      typeof price === 'number'
    );
  });
}

function looksLikeInstallmentPayload(line: string): boolean {
  const parsed = parseLooseJson(line);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return false;
  }

  const candidate = parsed as Record<string, unknown>;
  const sequence = candidate.sequence ?? candidate.Sequence;
  const months = candidate.months ?? candidate.Months;

  return typeof sequence === 'number' && typeof months === 'number';
}

function isHiddenSystemNoteLine(line: string): boolean {
  const normalized = stripSystemNotePrefix(line);
  const lowered = normalized.toLowerCase();

  return (
    HIDDEN_SYSTEM_NOTE_MARKERS.some((marker) => lowered.includes(marker.toLowerCase())) ||
    looksLikeInvoiceItemsPayload(normalized) ||
    looksLikeInstallmentPayload(normalized)
  );
}

function getVisibleLines(value?: string | null): string[] {
  return (value ?? '')
    .split(/\r?\n/)
    .map((line) => stripSystemNotePrefix(line))
    .filter((line) => line && !isHiddenSystemNoteLine(line))
    .map((line) => {
      const match = line.match(INSTALLMENT_MONEY_LINE_PATTERN);
      if (!match) return line;

      return `${match[1]}: ${MONEY_FORMATTER.format(parseMoneyFromText(match[2]))} ₽`;
    });
}

export function getVisibleSystemNoteLines(systemNotes?: string | null): string[] {
  return getVisibleLines(systemNotes);
}

export function getVisibleSystemNotes(systemNotes?: string | null): string {
  return getVisibleSystemNoteLines(systemNotes).join('\n');
}

export function getVisibleNoteLines(notes?: string | null): string[] {
  return getVisibleLines(notes);
}

export function getVisibleNotes(notes?: string | null): string {
  return getVisibleNoteLines(notes).join('\n');
}

function parseMoneyFromText(value: string): number {
  const normalized = value
    .replace(/\s+/g, '')
    .replace(',', '.')
    .replace(/[^\d.-]/g, '');
  const amount = Number(normalized);
  return Number.isFinite(amount) ? amount : 0;
}

export function getAppliedClientBalanceAmount(systemNotes?: string | null): number {
  const total = getVisibleSystemNoteLines(systemNotes).reduce((sum, line) => {
    const match = line.match(/зачтено\s+с\s+баланса(?:\s+(?:клиента|контрагента))?\s*:\s*([0-9\s.,-]+)/i);
    if (!match) {
      return sum;
    }

    return sum + Math.max(parseMoneyFromText(match[1]), 0);
  }, 0);

  return Number(total.toFixed(2));
}
