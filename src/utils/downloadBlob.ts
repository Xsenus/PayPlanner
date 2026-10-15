export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  try {
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
  } finally {
    link.remove();
    // Match case CSV downloads: let the native download consume the URL first.
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
