import type { DocumentStatusEntity } from '../types';

export type DocumentStatusKind = 'invoice' | 'act' | 'contract';

const flagByKind: Record<DocumentStatusKind, keyof DocumentStatusEntity> = {
  invoice: 'useForInvoices',
  act: 'useForActs',
  contract: 'useForContracts',
};

export function getDocumentStatusCodes<T extends string>(
  statuses: DocumentStatusEntity[] | null,
  kind: DocumentStatusKind,
  fallback: readonly T[],
): T[] {
  if (!statuses?.length) return [...fallback];

  const allowed = new Set<string>(fallback);
  const flag = flagByKind[kind];
  const configured = statuses
    .filter((status) => status.isActive && !!status[flag] && allowed.has(status.code))
    .sort((left, right) => (left.sortOrder ?? 0) - (right.sortOrder ?? 0) || left.name.localeCompare(right.name, 'ru'))
    .map((status) => status.code as T);

  return configured.length > 0 ? configured : [...fallback];
}

export function getDocumentStatusLabels<T extends string>(
  statuses: DocumentStatusEntity[] | null,
  kind: DocumentStatusKind,
  fallback: Record<T, string>,
): Record<T, string> {
  const labels = { ...fallback };
  if (!statuses?.length) return labels;

  const flag = flagByKind[kind];
  statuses
    .filter((status) => !!status[flag] && status.name.trim().length > 0)
    .forEach((status) => {
      if (status.code in labels) {
        labels[status.code as T] = status.name.trim();
      }
    });

  return labels;
}
