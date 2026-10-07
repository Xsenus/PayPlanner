// An accepted intent is not a published attachment or permission to complete the task.
export class TaskFileUploadAcceptedError extends Error {
  constructor(public readonly taskId: number, public readonly uploadKey: string) {
    super('HTTP 503: файл сохранён, обязательные копии ещё не готовы.');
    this.name = 'TaskFileUploadAcceptedError';
  }
}
