type ValidationErrorPayload = {
  message?: string;
  missingFields?: string[];
};

function stripHttpEnvelope(raw: string): string {
  const marker = ' - ';
  const markerIndex = raw.indexOf(marker);
  if (markerIndex >= 0) {
    const payload = raw.slice(markerIndex + marker.length).trim();
    if (payload) return payload;
  }

  const atIndex = raw.indexOf(' at ');
  if (atIndex > 0 && /^HTTP\s+\d+/i.test(raw)) {
    return raw.slice(0, atIndex).trim();
  }

  return raw.trim();
}

function tryParseValidationPayload(raw: string): ValidationErrorPayload | null {
  try {
    const parsed = JSON.parse(raw) as ValidationErrorPayload;
    if (parsed && typeof parsed === 'object') {
      return parsed;
    }
  } catch {
    /** */
  }

  return null;
}

export function extractDisplayErrorMessage(
  error: unknown,
  fallback: string,
  fieldLabels?: Record<string, string>,
): string {
  const raw = error instanceof Error ? error.message : String(error ?? '');
  if (/^(failed to fetch|networkerror|load failed)$/i.test(raw.trim())) {
    return fallback;
  }

  const stripped = stripHttpEnvelope(raw);
  const parsed = tryParseValidationPayload(stripped) ?? tryParseValidationPayload(raw);
  const httpStatus = raw.match(/^HTTP\s+(\d+)/i)?.[1] ?? stripped.match(/^HTTP\s+(\d+)/i)?.[1];

  if (httpStatus === '401') {
    return 'Сессия истекла. Войдите снова.';
  }

  if (httpStatus === '403') {
    return 'Недостаточно прав для выполнения действия.';
  }

  if (parsed) {
    const base = parsed.message?.trim() || fallback;
    const missingFields = Array.isArray(parsed.missingFields) ? parsed.missingFields : [];
    if (!missingFields.length) {
      return base;
    }

    const mapped = missingFields.map((field) => fieldLabels?.[field] ?? field).join(', ');
    return mapped ? `${base}: ${mapped}` : base;
  }

  if (!stripped || /^HTTP\s+\d+/i.test(stripped)) {
    return fallback;
  }

  return stripped;
}

export function isHttpErrorStatus(error: unknown, status: number): boolean {
  if (!Number.isInteger(status) || status < 100 || status > 599) {
    return false;
  }

  const raw = error instanceof Error ? error.message : String(error ?? '');
  return new RegExp(`(?:^|\\s)HTTP\\s+${status}(?:\\s|$)`, 'i').test(raw);
}
