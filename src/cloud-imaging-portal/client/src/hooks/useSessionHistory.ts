import { useEffect, useState } from 'react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import type { DateRange } from '../lib/dateRange.ts';
import type { SessionHistoryRecord } from '../lib/locationStatistics.ts';

export interface SessionHistoryState {
  /** Null while loading, empty after a failure. */
  records: SessionHistoryRecord[] | null;
  failed: boolean;
}

/**
 * The finished sessions of a period, in whole UTC days. Null skips the request, for a previous
 * period that cannot be worked out; a new `attempt` loads the period again.
 */
export function useSessionHistory(range: DateRange | null, attempt = 0): SessionHistoryState {
  const [state, setState] = useState<SessionHistoryState>({ records: null, failed: false });
  const from = range?.from;
  const to = range?.to;

  useEffect(() => {
    if (!from || !to) return undefined;
    let cancelled = false;
    setState({ records: null, failed: false });
    void (async () => {
      try {
        const params = new URLSearchParams({
          from: new Date(`${from}T00:00:00Z`).toISOString(),
          to: new Date(`${to}T23:59:59Z`).toISOString(),
        });
        const response = await apiFetchWithRetry(`/api/session-history?${params.toString()}`, { credentials: 'include' });
        if (cancelled) return;
        if (!response.ok) {
          setState({ records: [], failed: true });
          return;
        }
        const records = await response.json() as SessionHistoryRecord[];
        if (!cancelled) setState({ records, failed: false });
      } catch {
        if (!cancelled) setState({ records: [], failed: true });
      }
    })();
    return () => { cancelled = true; };
  }, [from, to, attempt]);

  return state;
}
