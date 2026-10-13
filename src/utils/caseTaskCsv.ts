import type { TaskJournalItem } from './taskJournalStore';

// Spreadsheet applications interpret formulas even inside quoted CSV cells.
export function caseTaskCsvCell(value: string | number | null | undefined): string {
  let text = String(value ?? '');
  if (/^[\s\uFEFF]*[=+\-@]/u.test(text) || /^[\t\r\n]/u.test(text)) text = `'${text}`;
  return `"${text.replaceAll('"', '""')}"`;
}

export function buildCaseTaskCsv(tasks: TaskJournalItem[], statusLabels: Record<string, string>): string {
  const rows = [
    ['ID', 'Задача', 'Исполнитель', 'Срок (UTC)', 'Статус', 'Результат'],
    ...tasks.map((task) => [task.id, task.title, task.assigneeName, task.dueAt,
      statusLabels[task.status] ?? task.status, task.result]),
  ];
  return `\uFEFF${rows.map((row) => row.map(caseTaskCsvCell).join(';')).join('\r\n')}`;
}

export function downloadCaseTaskCsv(fileName: string, csv: string): void {
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  try {
    link.click();
  } finally {
    link.remove();
    // Let the browser consume the URL before releasing it.
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
