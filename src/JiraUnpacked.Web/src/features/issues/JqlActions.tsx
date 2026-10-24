import { useEffect, useState } from 'react';
import { hashKey, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { getIssueJql, type JqlRequest, type JqlResult } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Button, Modal, Toast } from '../../ui';

/** How long "JQL copied" stays up. */
const TOAST_MS = 4000;

/** Only a web address is ever opened, whatever the server sends. */
const isWebUrl = (url: string): boolean => /^https?:\/\//i.test(url);

interface JqlActionsProps {
  projectId: string;
  /** The list's pruned filter and sort — exactly what the issue query is sent. */
  request: JqlRequest;
  /**
   * The list is holding its own query (the catalog or the default view has not settled), so the
   * filter is not yet the one it will show: nothing is fetched and the buttons are disabled.
   */
  disabled?: boolean;
}

/**
 * Copy JQL and Open in Jira for the list on screen and — once the JQL is known — a "⚠ N
 * differences" button listing what JQL could not say the same way.
 *
 * Fetched on demand (hover, focus, click), not on every edit: the list already sends a request per
 * edit, and the JQL is wanted only when asked for. Hover and focus start it early so a click usually
 * finds it ready — which matters for Open in Jira, because a tab opened long after the click is what
 * popup blockers block.
 */
export function JqlActions({ projectId, request, disabled = false }: JqlActionsProps) {
  const queryClient = useQueryClient();
  const queryKey = queryKeys.issueJql(projectId, request);
  const options = {
    queryKey,
    queryFn: () => getIssueJql(projectId, request),
    staleTime: 30_000,
  };
  // Only watches the cache, for THIS filter: an earlier filter's differences are never shown.
  const { data } = useQuery({ ...options, enabled: false });

  // An error is about the filter it was asked for, so it carries that request's key: a new filter
  // drops it (set during render — React re-renders at once), and a request that fails only after
  // the filter changed never shows its message beside the new one.
  const requestHash = hashKey(queryKey);
  const [failure, setFailure] = useState<{ key: string; message: string } | null>(null);
  if (failure && failure.key !== requestHash) setFailure(null);
  const error = failure?.key === requestHash ? failure.message : null;
  const fail = (message: string) => setFailure({ key: requestHash, message });
  const [copied, setCopied] = useState(false);
  const [manualCopy, setManualCopy] = useState<string | null>(null);
  const [showWarnings, setShowWarnings] = useState(false);

  useEffect(() => {
    if (!copied) return undefined;
    const timer = setTimeout(() => setCopied(false), TOAST_MS);
    return () => clearTimeout(timer);
  }, [copied]);

  const prefetch = () => {
    if (!disabled) void queryClient.prefetchQuery(options);
  };

  async function load(): Promise<JqlResult | undefined> {
    if (disabled) return undefined;
    setFailure(null);
    try {
      return await queryClient.fetchQuery(options);
    } catch (e) {
      fail(e instanceof ApiError ? e.title : 'Could not build the JQL.');
      return undefined;
    }
  }

  async function copy() {
    const result = await load();
    if (!result) return;
    try {
      // Missing outside a secure context, refused when the browser says no: either way the JQL is
      // shown to copy by hand rather than lost.
      if (!navigator.clipboard?.writeText) throw new Error('No clipboard');
      await navigator.clipboard.writeText(result.jql);
      setCopied(true);
    } catch {
      setManualCopy(result.jql);
    }
  }

  async function openInJira() {
    const result = await load();
    if (!result) return;
    if (!isWebUrl(result.url)) {
      fail('The Jira link is not a web address.');
      return;
    }
    window.open(result.url, '_blank', 'noopener,noreferrer');
  }

  const differences = data?.warnings.length ?? 0;

  return (
    <>
      <Button type="button" variant="secondary" disabled={disabled} onMouseEnter={prefetch} onFocus={prefetch} onClick={() => void copy()}>
        Copy JQL
      </Button>
      <Button type="button" variant="secondary" disabled={disabled} onMouseEnter={prefetch} onFocus={prefetch} onClick={() => void openInJira()}>
        Open in Jira
      </Button>
      {differences > 0 && (
        <Button type="button" variant="ghost" size="sm" aria-haspopup="dialog" onClick={() => setShowWarnings(true)}>
          <span aria-hidden="true">⚠</span> {differences} {differences === 1 ? 'difference' : 'differences'}
        </Button>
      )}
      {error && <p role="alert" className="text-sm text-[var(--ju-danger)]">{error}</p>}

      <Modal open={showWarnings} onClose={() => setShowWarnings(false)} title="How the JQL differs from this filter">
        <ul className="flex list-disc flex-col gap-2 pl-5 text-sm text-[var(--ju-text)]">
          {data?.warnings.map((w) => <li key={w.message}>{w.message}</li>)}
        </ul>
        <div className="mt-4 flex justify-end">
          <Button type="button" variant="secondary" onClick={() => setShowWarnings(false)}>Close</Button>
        </div>
      </Modal>

      <Modal open={manualCopy !== null} onClose={() => setManualCopy(null)} title="Copy the JQL">
        <p className="text-sm text-[var(--ju-text-secondary)]">
          The browser did not let the page copy it. Select the JQL and copy it yourself.
        </p>
        <textarea
          readOnly
          aria-label="JQL"
          rows={4}
          value={manualCopy ?? ''}
          onFocus={(e) => e.currentTarget.select()}
          className="mt-2 w-full rounded-md border border-[var(--ju-border)] bg-[var(--ju-surface)] p-2 font-mono text-xs text-[var(--ju-text)]"
        />
        <div className="mt-4 flex justify-end">
          <Button type="button" variant="secondary" onClick={() => setManualCopy(null)}>Close</Button>
        </div>
      </Modal>

      {copied && (
        <div className="fixed bottom-4 right-4 z-50">
          <Toast message="JQL copied" onDismiss={() => setCopied(false)} />
        </div>
      )}
    </>
  );
}
