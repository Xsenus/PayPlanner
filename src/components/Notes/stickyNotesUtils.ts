export function getEffectiveBoardNotesCount(boardNotesCount: number, loadedNotesCount: number) {
  return Math.max(boardNotesCount, loadedNotesCount);
}

export async function copyTextToClipboard(text: string, clipboard?: Pick<Clipboard, 'writeText'>) {
  const targetClipboard = clipboard ?? globalThis.navigator?.clipboard ?? window.navigator.clipboard;
  if (!targetClipboard?.writeText) {
    throw new Error('Clipboard API is unavailable');
  }

  await targetClipboard.writeText(text);
}
