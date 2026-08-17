import { createContext, useContext, useCallback, useEffect, useRef, useState } from 'react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';

interface BrandingConfig {
  primaryColor?: string;
  accentColor?: string;
  applicationName?: string;
  /** Boot image logo blob path (embedded into boot media by the Media Builder). */
  logoBlobPath?: string;
  /** Portal header/sidebar logo blob path (streamed to the browser). */
  portalLogoBlobPath?: string;
}

interface BrandingContextValue {
  branding: BrandingConfig;
  /** Object URL for the configured portal logo, or null when none is set. */
  logoUrl: string | null;
  isLoaded: boolean;
  /** Re-fetches branding (and the portal logo) from the backend. */
  refresh: () => Promise<void>;
}

const BrandingContext = createContext<BrandingContextValue>({
  branding: {},
  logoUrl: null,
  isLoaded: false,
  refresh: async () => { /* no-op default */ },
});

/**
 * Applies branding at runtime by injecting CSS custom properties (T096, FR-038).
 * Wraps the application so all components can consume live branding values.
 */
export function BrandingProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  const [branding, setBranding] = useState<BrandingConfig>({});
  const [logoUrl, setLogoUrl]   = useState<string | null>(null);
  const [isLoaded, setIsLoaded] = useState(false);
  const logoUrlRef = useRef<string | null>(null);

  /** Replaces the current logo object URL, revoking the previous one to avoid leaks. */
  const setLogoObjectUrl = useCallback((url: string | null) => {
    if (logoUrlRef.current) {
      URL.revokeObjectURL(logoUrlRef.current);
    }
    logoUrlRef.current = url;
    setLogoUrl(url);
  }, []);

  const load = useCallback(async () => {
    try {
      const res = await apiFetchWithRetry('/api/branding', { credentials: 'include' });
      if (res.ok) {
        const data = await res.json() as BrandingConfig;
        setBranding(data);
        applyBrandingCssVariables(data);

        if (data.portalLogoBlobPath) {
          try {
            // Stream the logo bytes through the backend (managed identity read, no SAS)
            // and expose them as an ephemeral object URL for <img>.
            const logoRes = await apiFetchWithRetry('/api/branding/portal-logo/content', { credentials: 'include' });
            if (logoRes.ok) {
              setLogoObjectUrl(URL.createObjectURL(await logoRes.blob()));
            } else {
              setLogoObjectUrl(null);
            }
          } catch {
            setLogoObjectUrl(null);
          }
        } else {
          setLogoObjectUrl(null);
        }
      }
    } catch {
      // Use default CSS variables (set in index.css)
    } finally {
      setIsLoaded(true);
    }
  }, [setLogoObjectUrl]);

  useEffect(() => { void load(); }, [load]);

  // Revoke the last object URL on unmount.
  useEffect(() => () => {
    if (logoUrlRef.current) {
      URL.revokeObjectURL(logoUrlRef.current);
    }
  }, []);

  return (
    <BrandingContext.Provider value={{ branding, logoUrl, isLoaded, refresh: load }}>
      {children}
    </BrandingContext.Provider>
  );
}

export function useBranding(): BrandingContextValue {
  return useContext(BrandingContext);
}

/**
 * Injects portal branding into CSS custom properties on the root element (FR-038).
 * All Tailwind `primary` / `accent` colour tokens reference these variables.
 */
function applyBrandingCssVariables(cfg: BrandingConfig): void {
  const root = document.documentElement;

  if (cfg.primaryColor) {
    const [r, g, b] = hexToRgb(cfg.primaryColor);
    root.style.setProperty('--color-primary', `${r} ${g} ${b}`);
  }
  if (cfg.accentColor) {
    const [r, g, b] = hexToRgb(cfg.accentColor);
    root.style.setProperty('--color-accent', `${r} ${g} ${b}`);
  }
  if (cfg.applicationName) {
    document.title = cfg.applicationName;
  }
}

function hexToRgb(hex: string): [number, number, number] {
  const clean = hex.replace('#', '');
  const n     = parseInt(clean, 16);
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}
