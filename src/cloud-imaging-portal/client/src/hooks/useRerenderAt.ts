import { useEffect, useState } from 'react';

/** Renders the component again once `at` has passed, so a wait shown against the clock ends by itself. */
export function useRerenderAt(at: string | null | undefined): void {
  const [, setTick] = useState(0);
  useEffect(() => {
    const due = at ? Date.parse(at) - Date.now() : NaN;
    if (!Number.isFinite(due) || due <= 0) return undefined;
    const timer = window.setTimeout(() => setTick(t => t + 1), Math.min(due + 250, 2_147_000_000));
    return () => window.clearTimeout(timer);
  }, [at]);
}
