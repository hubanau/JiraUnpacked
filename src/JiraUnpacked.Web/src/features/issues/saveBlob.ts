/**
 * Hands a file already in memory to the browser's download manager, by clicking a temporary
 * link to it — the only way a page can start a download it did not navigate to.
 */
export function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  // Not revoked straight away: the browser reads the blob after click() returns, and revoking
  // first leaves it a failed download. A minute is far longer than any read and still frees
  // the memory.
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}
