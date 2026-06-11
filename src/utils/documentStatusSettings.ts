import type { DocumentStatusEntity } from '../types';

export type DocumentStatusKind = 'invoice' | 'act' | 'contract';

const flagByKind: Record<DocumentStatusKind, keyof DocumentStatusEntity> = {
  invoice: 'useForInvoices',
  act: 'useForActs',
  contract: 'useForContracts',
};

const canonicalDocumentStatusLabels: Record<DocumentStatusKind, Record<string, string>> = {
  invoice: {
    RequiresClientSignature: 'Требуется подпись клиента',
    SignedByClient: 'Подписан клиентом',
  },
  act: {
    RequiresClientSignature: 'Требуется подпись клиента',
    Signed: 'Подписан клиентом',
  },
  contract: {
    RequiresClientSignature: 'Требуется подпись клиента',
    Concluded: 'Подписан клиентом',
  },
};

const mandatoryDocumentStatusLabels: Record<string, string> = {
  RequiresClientSignature: 'Требуется подпись клиента',
};

const mandatoryDocumentStatusCodes = Object.keys(mandatoryDocumentStatusLabels);

const allowedCodesByKind: Record<DocumentStatusKind, Set<string>> = {
  invoice: new Set([
    'Draft',
    'NotSigned',
    'RequiresClientSignature',
    'OnApproval',
    'Approved',
    'ApprovalRejected',
    'NeedsCorrection',
    'OnSigning',
    'SignedByClient',
    'Paid',
    'PartiallyPaid',
    'Overdue',
    'Cancelled',
  ]),
  act: new Set([
    'Draft',
    'NotSigned',
    'RequiresClientSignature',
    'OnApproval',
    'Approved',
    'ApprovalRejected',
    'NeedsCorrection',
    'OnSigning',
    'Signed',
    'Cancelled',
  ]),
  contract: new Set([
    'Draft',
    'NotSigned',
    'RequiresClientSignature',
    'OnApproval',
    'Approved',
    'ApprovalRejected',
    'NeedsCorrection',
    'OnSigning',
    'Concluded',
    'Terminated',
    'Cancelled',
  ]),
};

export function isDocumentStatusCodeAllowedForKind(code: string | null | undefined, kind: DocumentStatusKind): boolean {
  return !!code && allowedCodesByKind[kind].has(code);
}

function ensureMandatoryDocumentStatusCodes<T extends string>(codes: T[], fallback: readonly T[], allowed: Set<string>) {
  mandatoryDocumentStatusCodes.forEach((mandatoryCode) => {
    if (!allowed.has(mandatoryCode) || codes.includes(mandatoryCode as T)) {
      return;
    }

    const fallbackIndex = fallback.findIndex((code) => code === mandatoryCode);
    const insertAt = codes.findIndex((code) => fallback.findIndex((fallbackCode) => fallbackCode === code) > fallbackIndex);
    if (insertAt >= 0) {
      codes.splice(insertAt, 0, mandatoryCode as T);
    } else {
      codes.push(mandatoryCode as T);
    }
  });

  return codes;
}

export function getDocumentStatusCodes<T extends string>(
  statuses: DocumentStatusEntity[] | null,
  kind: DocumentStatusKind,
  fallback: readonly T[],
): T[] {
  const allowed = new Set<string>(fallback);
  if (!statuses?.length) return ensureMandatoryDocumentStatusCodes([...fallback], fallback, allowed);

  const flag = flagByKind[kind];
  const configured = statuses
    .filter((status) => status.isActive && !!status[flag] && allowed.has(status.code))
    .sort((left, right) => (left.sortOrder ?? 0) - (right.sortOrder ?? 0) || left.name.localeCompare(right.name, 'ru'))
    .map((status) => status.code as T);

  return ensureMandatoryDocumentStatusCodes(configured.length > 0 ? configured : [...fallback], fallback, allowed);
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
        labels[status.code as T] = canonicalDocumentStatusLabels[kind][status.code] ?? mandatoryDocumentStatusLabels[status.code] ?? status.name.trim();
      }
    });

  mandatoryDocumentStatusCodes.forEach((code) => {
    if (code in labels) {
      labels[code as T] = canonicalDocumentStatusLabels[kind][code] ?? mandatoryDocumentStatusLabels[code];
    }
  });

  Object.entries(canonicalDocumentStatusLabels[kind]).forEach(([code, label]) => {
    if (code in labels) {
      labels[code as T] = label;
    }
  });

  return labels;
}
