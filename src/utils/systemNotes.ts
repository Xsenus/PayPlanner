import { INSTALLMENT_PAYMENT_MARKER } from './installmentPlan';

export const INVOICE_LINE_ITEMS_MARKER = '[pp:invoice-items]';

const HIDDEN_SYSTEM_NOTE_MARKERS = [INSTALLMENT_PAYMENT_MARKER, INVOICE_LINE_ITEMS_MARKER] as const;

function stripListPrefix(line: string): string {
  return line.startsWith('\u2022 ') ? line.slice(2) : line;
}

export function getVisibleSystemNoteLines(systemNotes?: string | null): string[] {
  return (systemNotes ?? '')
    .split('\n')
    .map((line) => stripListPrefix(line.trim()))
    .filter((line) => line && !HIDDEN_SYSTEM_NOTE_MARKERS.some((marker) => line.startsWith(marker)));
}

export function getVisibleSystemNotes(systemNotes?: string | null): string {
  return getVisibleSystemNoteLines(systemNotes).join('\n');
}
