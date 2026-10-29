// Discards UI adoption only; an already sent server request may have succeeded.
export class StaleUiOperationError extends Error {
  constructor() {
    super('Владелец открытой карточки изменился. Результат не применяется к другой форме.');
    this.name = 'StaleUiOperationError';
  }
}

export async function awaitOwnedUiResult<T>(operation: () => Promise<T>, isCurrent: () => boolean): Promise<T> {
  if (!isCurrent()) throw new StaleUiOperationError();
  let value: T;
  try { value = await operation(); }
  catch (error) {
    if (!isCurrent()) throw new StaleUiOperationError();
    throw error;
  }
  if (!isCurrent()) throw new StaleUiOperationError();
  return value;
}
