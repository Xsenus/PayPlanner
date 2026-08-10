export type InvoiceDocumentTemplateKey = 'invoice-offer' | 'invoice-order' | string;

function sanitizeFilePart(value: string): string {
  return value
    .trim()
    .replace(/[<>:"/\\|?*]/g, '_')
    .replace(/\s+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^[_.]+|[_.]+$/g, '');
}

function formatDocumentDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value.trim());
  return match ? `${match[3]}.${match[2]}.${match[1]}` : sanitizeFilePart(value);
}

export function buildInvoiceDocumentFileName(
  number: string,
  date: string,
  format: 'pdf' | 'word',
  templateKey?: InvoiceDocumentTemplateKey,
): string {
  const documentName = templateKey === 'invoice-offer'
    ? 'Счет-договор'
    : templateKey === 'invoice-order'
      ? 'Заказ'
      : 'Счет';
  const safeNumber = sanitizeFilePart(number) || 'без_номера';
  const safeDate = formatDocumentDate(date) || 'без_даты';
  const extension = format === 'pdf' ? 'pdf' : 'docx';

  return `${documentName}_№${safeNumber}_${safeDate}.${extension}`;
}
