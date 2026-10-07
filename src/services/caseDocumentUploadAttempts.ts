import { createClientRequestId } from '../utils/clientRequestId';
const storageKey = 'pp.case-document-upload-attempts.v1';
const maxPending = 64;
type Attempt = { identity: string; key: string };

// This is a retry identity only. The server authorizes the live actor and checks
// the full SHA/size/metadata; matching browser file metadata never proves identity.
function actorScope(token: string | null): string {
  try {
    const payload = token?.split('.')[1];
    if (!payload) return 'unidentified';
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const claims = JSON.parse(atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))) as Record<string, unknown>;
    const id = claims['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'] ?? claims.nameid ?? claims.sub;
    return JSON.stringify([claims.iss ?? '', typeof id === 'string' || typeof id === 'number' ? String(id) : 'unidentified']);
  } catch { return 'unidentified'; }
}

function read(): Attempt[] {
  try {
    const raw = sessionStorage.getItem(storageKey);
    if (!raw) return [];
    const values: unknown = JSON.parse(raw);
    if (!Array.isArray(values) || values.length > maxPending || !values.every((item: unknown) => {
      if (!item || typeof item !== 'object') return false;
      const value = item as Partial<Attempt>;
      return typeof value.identity === 'string' && value.identity.length <= 2048
        && typeof value.key === 'string' && /^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(value.key);
    })) throw new Error('invalid registry');
    return values as Attempt[];
  } catch {
    throw new Error('Не удалось прочитать данные предыдущих загрузок. Эта попытка не отправлена; восстановите доступ к данным вкладки.');
  }
}

export function beginCaseDocumentUpload(scope: string, file: File, token: string | null): Attempt {
  const identity = JSON.stringify([actorScope(token), scope, file.name, file.type, file.size, file.lastModified]);
  return beginAttempt(identity);
}

export function beginCaseDocumentGeneration(scope: string, token: string | null): Attempt {
  return beginAttempt(JSON.stringify([actorScope(token), scope, "generated-template", "text/html", 0, 0]));
}

function beginAttempt(identity: string): Attempt {
  const attempts = read();
  const existing = attempts.find((item) => item.identity === identity);
  if (existing) return existing;
  if (attempts.length >= maxPending) throw new Error('Слишком много незавершённых загрузок. Завершите предыдущие попытки перед новой отправкой.');
  const attempt = { identity, key: createClientRequestId() };
  try { sessionStorage.setItem(storageKey, JSON.stringify([...attempts, attempt])); }
  catch { throw new Error('Не удалось сохранить данные попытки загрузки. Эта попытка не отправлена; восстановите доступ к данным вкладки.'); }
  return attempt;
}

export function confirmCaseDocumentUpload(attempt: Attempt): void {
  // Never remove a newer attempt if an older concurrent response arrives late.
  try {
    const remaining = read().filter((item) => item.identity !== attempt.identity || item.key !== attempt.key);
    if (remaining.length) sessionStorage.setItem(storageKey, JSON.stringify(remaining));
    else sessionStorage.removeItem(storageKey);
  } catch { /* Keep an unresolved identity when persistence cannot confirm its removal. */ }
}

type GenerationProof = { kind: string; requestSha256: string; templateKey: string | null;
  documentType: string | null; status: string | null; sourceVerified: boolean;
  financialKind?: 'invoice' | 'act' | 'contract' | null };
type PublicationReceipt = { uploadKey: string; status: string; documentId: number | null; version: number | null; generation?: GenerationProof | null };

// Receipts come from an authorized read of this case. They can release a retry
// identity without a browser File, but cannot confirm the bytes of a new POST.
export function confirmPublishedCaseDocumentUploads(caseId: number, apiBaseUrl: string,
  receipts: readonly PublicationReceipt[], requestToken: string | null, currentToken: string | null): void {
  acknowledgeCaseDocumentAttempts(caseId, apiBaseUrl, receipts, requestToken, currentToken, 'published');
}

// Only an explicit authorized retirement response/read may call this helper.
// Polling failures, missing operations and dead-letter states are not proof.
export function confirmRetiredCaseDocumentUpload(caseId: number, apiBaseUrl: string,
  receipt: { uploadKey: string; status: string; generation?: GenerationProof | null }, requestToken: string | null, currentToken: string | null): boolean {
  return acknowledgeCaseDocumentAttempts(caseId, apiBaseUrl, [receipt], requestToken, currentToken, 'retired');
}

export function confirmCompletedCaseDocumentUpload(caseId: number, apiBaseUrl: string,
  receipt: PublicationReceipt, requestToken: string | null, currentToken: string | null): boolean {
  return acknowledgeCaseDocumentAttempts(caseId, apiBaseUrl, [receipt], requestToken, currentToken, 'published');
}

export function isCaseDocumentUploadActorCurrent(requestToken: string | null, currentToken: string | null): boolean {
  const actor = actorScope(requestToken);
  if (actor === 'unidentified' || actor !== actorScope(currentToken)) return false;
  const parts: unknown = JSON.parse(actor);
  return Array.isArray(parts) && parts[1] !== 'unidentified' && parts[1] !== '';
}

// Only exact local retry keys absent from the server's pending page need a
// separate proof read. Published numeric owners intentionally leave that page.
export function getUnconfirmedCaseDocumentUploadKeys(caseId: number, apiBaseUrl: string,
  token: string | null, observedKeys: readonly string[] = []): string[] {
  if (!Number.isSafeInteger(caseId) || caseId <= 0 || !isCaseDocumentUploadActorCurrent(token, token)) return [];
  const actor = actorScope(token);
  const observed = new Set(observedKeys.map((key) => key.toLowerCase()));
  const keys = new Set<string>();
  let attempts: Attempt[];
  try { attempts = read(); }
  catch { return []; /* Keep unreadable identities; server discovery still works. */ }
  for (const attempt of attempts) {
    try {
      const identity: unknown = JSON.parse(attempt.identity);
      if (!Array.isArray(identity) || identity.length !== 6 || identity[0] !== actor || typeof identity[1] !== 'string') continue;
      const scope: unknown = JSON.parse(identity[1]);
      if (!Array.isArray(scope) || scope[1] !== apiBaseUrl || scope[2] !== caseId) continue;
      const supported = (scope[0] === 'case' && scope.length === 6)
        || (scope[0] === 'template' && scope.length === 6)
        || (scope[0] === 'work-order' && scope.length === 3)
        || (scope[0] === 'financial' && scope.length === 5 && ['invoice', 'act', 'contract'].includes(scope[3])
          && typeof scope[4] === 'string' && /^[a-f0-9]{64}$/i.test(scope[4]));
      const key = attempt.key.toLowerCase();
      if (supported && key !== '00000000-0000-0000-0000-000000000000' && !observed.has(key)) keys.add(key);
    } catch { /* Unrecognized identities remain unresolved. */ }
  }
  return [...keys];
}

function acknowledgeCaseDocumentAttempts(caseId: number, apiBaseUrl: string,
  receipts: readonly { uploadKey: string; status: string; documentId?: number | null; version?: number | null; generation?: GenerationProof | null }[],
  requestToken: string | null, currentToken: string | null, proof: 'published' | 'retired'): boolean {
  const actor = actorScope(requestToken);
  if (actor === 'unidentified' || actor !== actorScope(currentToken) || !Number.isSafeInteger(caseId) || caseId <= 0
    || !Array.isArray(receipts) || receipts.length > 50) return false;
  try {
    const actorParts: unknown = JSON.parse(actor);
    if (!Array.isArray(actorParts) || actorParts[1] === 'unidentified' || actorParts[1] === '') return false;
    const confirmed = new Set(receipts.filter((item) => ((proof === 'retired' && item.status === 'retired')
      || (proof === 'published' && item.status === 'uploaded'
      && Number.isSafeInteger(item.documentId) && item.documentId! > 0
      && Number.isSafeInteger(item.version) && item.version! > 0))
      && /^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(item.uploadKey))
      .map((item) => item.uploadKey.toLowerCase()));
    if (confirmed.size === 0) return false;
    const attempts = read();
    const remaining = attempts.filter((item) => {
      if (!confirmed.has(item.key.toLowerCase())) return true;
      try {
        const identity: unknown = JSON.parse(item.identity);
        if (!Array.isArray(identity) || identity.length !== 6 || identity[0] !== actor || typeof identity[1] !== 'string') return true;
        const scope: unknown = JSON.parse(identity[1]);
        if (!Array.isArray(scope) || scope[1] !== apiBaseUrl) return true;
        const matching = receipts.filter((value) => value.uploadKey.toLowerCase() === item.key.toLowerCase());
        const receipt = matching[0];
        if (scope[0] === 'template') {
          if (scope.length !== 6 || scope[2] !== caseId || matching.length !== 1) return true;
          // An immutable owned retirement marker remains proof after intent cleanup.
          if (proof === 'retired') return false;
          const generation = receipt?.generation;
          return generation?.kind !== 'template' || generation.sourceVerified !== true
            || !/^[a-f0-9]{64}$/i.test(generation.requestSha256)
            || scope[3] !== (generation.templateKey ?? '') || scope[4] !== (generation.documentType ?? '')
            || scope[5] !== generation.status;
        }
        if (scope[0] === 'work-order') {
          if (scope.length !== 3 || scope[2] !== caseId || matching.length !== 1) return true;
          if (proof === 'retired') return false;
          const generation = receipt?.generation;
          return generation?.kind !== 'work-order' || generation.sourceVerified !== true
            || !/^[a-f0-9]{64}$/i.test(generation.requestSha256)
            || !['Подготовлен', 'Согласован'].includes(generation.status ?? '');
        }
        if (scope[0] === 'financial') {
          if (scope.length !== 5 || scope[2] !== caseId || matching.length !== 1
            || !['invoice', 'act', 'contract'].includes(scope[3])
            || typeof scope[4] !== 'string' || !/^[a-f0-9]{64}$/i.test(scope[4])) return true;
          if (proof === 'retired') return false;
          const generation = receipt?.generation;
          const documentTypes = { invoice: 'Счет', act: 'Акт', contract: 'Договор' };
          return generation?.kind !== 'financial' || generation.sourceVerified !== true
            || generation.financialKind !== scope[3] || generation.status !== 'Черновик'
            || generation.documentType !== documentTypes[scope[3] as keyof typeof documentTypes]
            || !/^[a-f0-9]{64}$/i.test(generation.requestSha256);
        }
        // A generated receipt cannot acknowledge a browser file upload.
        if (proof === 'published' && receipt?.generation != null) return true;
        if (scope[0] === 'case') return scope.length !== 6 || scope[2] !== caseId;
        // Version attempts store their source document ID. The exact attempt key
        // in the authorized case receipt identifies the published version.
        return scope[0] !== 'version' || scope.length !== 5 || !Number.isSafeInteger(scope[2]) || scope[2] <= 0;
      } catch { return true; }
    });
    if (remaining.length === attempts.length) {
      // No local identity is valid for an attempt started in another tab.
      // A present key in an unrecognized/foreign scope remains unresolved.
      return !attempts.some((item) => confirmed.has(item.key.toLowerCase()));
    }
    if (remaining.length) sessionStorage.setItem(storageKey, JSON.stringify(remaining));
    else sessionStorage.removeItem(storageKey);
    return true;
  } catch { return false; /* A corrupt or inaccessible registry stays intact. */ }
}
