// Closing an owned browser disposes still-pending route.fetch calls. Preserve
// the scenario's primary failure instead of replacing it with that close error.
export async function runTaskUploadRoute(action, isClosing, recordDisposal) {
  try {
    return await action();
  } catch (error) {
    if (!isClosing() || !(error instanceof Error)
      || !error.message.startsWith('route.fetch: Request context disposed.')) throw error;
    recordDisposal();
  }
}
