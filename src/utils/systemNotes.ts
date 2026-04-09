import { INSTALLMENT_PAYMENT_MARKER } from './installmentPlan';

export const INVOICE_LINE_ITEMS_MARKER = '[pp:invoice-items]';

const HIDDEN_SYSTEM_NOTE_MARKERS = [INSTALLMENT_PAYMENT_MARKER, INVOICE_LINE_ITEMS_MARKER] as const;
const SYSTEM_NOTE_PREFIXES = ['system:', 'системно:'] as const;

function stripListPrefix(line: string): string {
  return line.startsWith('\u2022 ') ? line.slice(2) : line;
}

function stripBom(line: string): string {
  return line.replace(/^\uFEFF/, '');
}

function stripSystemNotePrefix(line: string): string {
  let normalized = stripBom(stripListPrefix(line.trim()));

  for (const prefix of SYSTEM_NOTE_PREFIXES) {
    if (normalized.toLowerCase().startsWith(prefix)) {
      normalized = normalized.slice(prefix.length).trim();
      break;
    }
  }

  return normalized;
}

function safeJsonParse(value: string): unknown | null {
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function looksLikeInvoiceItemsPayload(line: string): boolean {
  const parsed = safeJsonParse(line);
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
  const parsed = safeJsonParse(line);
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
    .filter((line) => line && !isHiddenSystemNoteLine(line));
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
