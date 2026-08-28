import { createContext, useContext, useEffect, useState, useCallback } from 'react';
import { useAuth } from './authContext.tsx';
import { apiFetch } from '../lib/apiClient.ts';

/** A selectable entry from the admin-managed location catalog. */
export interface LocationOption {
  locationId: string;
  name: string;
}

interface StoredPreference {
  locationId?: string | null;
  locationName?: string | null;
}

interface UserPreferencesContextValue {
  /** Full location catalog (Location Labels feature) — used by the Header picker and Sessions filter. */
  locations: LocationOption[];
  /** The signed-in user's preferred location, or null when unset. */
  preferredLocationId: string | null;
  preferredLocationName: string | null;
  /** Persists the user's preferred location (server-side, keyed by their Entra oid). Pass null to clear it. */
  setPreferredLocation: (location: LocationOption | null) => Promise<void>;
  /** Re-fetches the location catalog — call after adding/deleting a location on the admin page. */
  refreshLocations: () => Promise<void>;
  loading: boolean;
}

const UserPreferencesContext = createContext<UserPreferencesContextValue>({
  locations: [],
  preferredLocationId: null,
  preferredLocationName: null,
  setPreferredLocation: () => Promise.resolve(),
  refreshLocations: () => Promise.resolve(),
  loading: true,
});

/**
 * Provides the admin-managed location catalog and the signed-in user's preferred location
 * (Location Labels feature) — backs the Header account menu's location picker and the Sessions
 * page's "my location" hard filter.
 */
export function UserPreferencesProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  const { isAuthenticated } = useAuth();
  const [locations, setLocations] = useState<LocationOption[]>([]);
  const [preferredLocationId, setPreferredLocationId] = useState<string | null>(null);
  const [preferredLocationName, setPreferredLocationName] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const refreshLocations = useCallback(async () => {
    try {
      const res = await apiFetch('/api/locations', { credentials: 'include' });
      if (res.ok) setLocations(await res.json() as LocationOption[]);
    } catch { /* best effort — picker just shows whatever it already had */ }
  }, []);

  useEffect(() => {
    if (!isAuthenticated) { setLoading(false); return; }
    void (async () => {
      try {
        const [locRes, prefRes] = await Promise.all([
          apiFetch('/api/locations', { credentials: 'include' }),
          apiFetch('/api/user-preferences', { credentials: 'include' }),
        ]);
        if (locRes.ok) setLocations(await locRes.json() as LocationOption[]);
        if (prefRes.ok) {
          const pref = await prefRes.json() as StoredPreference | null;
          setPreferredLocationId(pref?.locationId ?? null);
          setPreferredLocationName(pref?.locationName ?? null);
        }
      } catch { /* best effort — Header/Sessions fall back to "no preference set" */ }
      finally { setLoading(false); }
    })();
  }, [isAuthenticated]);

  const setPreferredLocation = useCallback(async (location: LocationOption | null) => {
    // Optimistic — the picker and Sessions filter feel instant; a failed PUT just means the
    // choice won't survive a reload, not a broken UI in the meantime.
    setPreferredLocationId(location?.locationId ?? null);
    setPreferredLocationName(location?.name ?? null);
    try {
      await apiFetch('/api/user-preferences', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ locationId: location?.locationId ?? null, locationName: location?.name ?? null }),
      });
    } catch { /* best effort */ }
  }, []);

  return (
    <UserPreferencesContext.Provider
      value={{ locations, preferredLocationId, preferredLocationName, setPreferredLocation, refreshLocations, loading }}
    >
      {children}
    </UserPreferencesContext.Provider>
  );
}

export function useUserPreferences(): UserPreferencesContextValue {
  return useContext(UserPreferencesContext);
}
