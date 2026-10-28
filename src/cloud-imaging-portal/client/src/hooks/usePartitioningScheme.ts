import { useEffect, useState } from 'react';
import { apiFetch } from '../lib/apiClient.ts';
import { DEFAULT_SCHEME, schemeEquals, type PartitioningScheme } from '../lib/partitioningScheme.ts';

/**
 * Loads and saves the partitioning scheme. Held by the page rather than the panel, so the draft
 * outlives the panel when its tab is switched away.
 */
export function usePartitioningScheme() {
  const [scheme, setScheme] = useState<PartitioningScheme>(DEFAULT_SCHEME);
  const [savedScheme, setSavedScheme] = useState<PartitioningScheme>(DEFAULT_SCHEME);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    void (async () => {
      try {
        const res = await apiFetch('/api/partitioning-scheme', { credentials: 'include' });
        if (res.ok) {
          const data = await res.json() as PartitioningScheme;
          setScheme(data);
          setSavedScheme(data);
        }
      } catch { /* use defaults */ }
      finally { setLoading(false); }
    })();
  }, []);

  /** Saves the draft. Throws with the server's reason when it is refused. */
  const save = async (): Promise<void> => {
    const res = await apiFetch('/api/partitioning-scheme', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify(scheme),
    });
    if (!res.ok && res.status !== 204) {
      let detail = 'The partitioning scheme was not saved.';
      try { detail = await res.text() || detail; } catch { /* keep default */ }
      throw new Error(detail);
    }
    setSavedScheme(scheme);
  };

  return {
    scheme,
    savedScheme,
    loading,
    isDirty: !schemeEquals(scheme, savedScheme),
    setScheme,
    discard: () => setScheme(savedScheme),
    save,
  };
}
