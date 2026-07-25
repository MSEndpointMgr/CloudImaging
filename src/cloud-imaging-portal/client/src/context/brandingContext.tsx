import { createContext, useContext, useCallback, useEffect, useState } from 'react';
import { apiFetch } from '../lib/apiClient.ts';

interface BrandingConfig {
  primaryColor?: string;
  accentColor?: string;
  applicationName?: string;
  logoBlobPath?: string;
}

interface BrandingContextValue {
  branding: BrandingConfig;
  /** Time-limited SAS URL for the configured logo, or null when none is set. */
  logoUrl: string | null;
  isLoaded: boolean;
  /** Re-fetches branding (and the logo SAS URL) from the backend. */
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

  const load = useCallback(async () => {
    try {
      const res = await apiFetch('/api/branding', { credentials: 'include' });
      if (res.ok) {
        const data = await res.json() as BrandingConfig;
        setBranding(data);
        applyBrandingCssVariables(data);

        if (data.logoBlobPath) {
          try {
            const sasRes = await apiFetch('/api/branding/logo/sas', { credentials: 'include' });
            if (sasRes.ok) {
              const sas = await sasRes.json() as { sasTokenUrl?: string };
              setLogoUrl(sas.sasTokenUrl ?? null);
            } else {
              setLogoUrl(null);
            }
          } catch {
            setLogoUrl(null);
          }
        } else {
          setLogoUrl(null);
        }
      }
    } catch {
      // Use default CSS variables (set in index.css)
    } finally {
      setIsLoaded(true);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

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
