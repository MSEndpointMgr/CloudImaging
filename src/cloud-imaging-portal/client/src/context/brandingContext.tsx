import { createContext, useContext, useCallback, useEffect, useRef, useState } from 'react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { useTheme } from './themeContext.tsx';

interface BrandingConfig {
  primaryColor?: string;
  accentColor?: string;
  applicationName?: string;
  /** Boot image logo blob path (embedded into boot media by the Media Builder). */
  logoBlobPath?: string;
  /** Portal header/sidebar logo blob path (streamed to the browser). */
  portalLogoBlobPath?: string;
  /** Surface background colours (FR-038 extension) — independent light/dark values per surface. */
  sidebarBackgroundLight?: string;
  sidebarBackgroundDark?: string;
  cardBackgroundLight?: string;
  cardBackgroundDark?: string;
  pageBackgroundLight?: string;
  pageBackgroundDark?: string;
  headerBackgroundLight?: string;
  headerBackgroundDark?: string;
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
  const { theme } = useTheme();

  /**
   * Replaces the current logo object URL, revoking the previous one to avoid leaks, and keeps
   * the browser tab favicon in sync with it — the portal logo is the only branding asset
   * available for this, so it doubles as the favicon (falls back to the browser's default
   * icon when no portal logo is configured, since there is no bundled default to fall back to).
   */
  const setLogoObjectUrl = useCallback((url: string | null) => {
    if (logoUrlRef.current) {
      URL.revokeObjectURL(logoUrlRef.current);
    }
    logoUrlRef.current = url;
    setLogoUrl(url);
    applyFavicon(url);
  }, []);

  const load = useCallback(async () => {
    try {
      const res = await apiFetchWithRetry('/api/branding', { credentials: 'include' });
      if (res.ok) {
        const data = await res.json() as BrandingConfig;
        setBranding(data);

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

  // Re-applies branding CSS variables whenever the branding config or the active light/dark
  // theme changes, so surface-colour overrides pick the correct per-theme value immediately.
  useEffect(() => { applyBrandingCssVariables(branding, theme); }, [branding, theme]);

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
 * Points the browser tab's favicon at the given portal logo object URL, reusing the same
 * <link rel="icon"> element across updates instead of appending a new one each time. When
 * `url` is null (no portal logo configured, or it failed to load), the injected link is
 * removed entirely so the browser falls back to its own default tab icon.
 */
function applyFavicon(url: string | null): void {
  const existing = document.head.querySelector<HTMLLinkElement>('link[rel="icon"]');

  if (!url) {
    existing?.remove();
    return;
  }

  const link = existing ?? document.createElement('link');
  link.rel = 'icon';
  link.href = url;
  if (!existing) {
    document.head.appendChild(link);
  }
}

/**
 * Injects portal branding into CSS custom properties on the root element (FR-038).
 * All Tailwind `primary` / `accent` colour tokens reference these variables.
 */
function applyBrandingCssVariables(cfg: BrandingConfig, theme: 'light' | 'dark'): void {
  const root = document.documentElement;

  const setColor = (varName: string, hex?: string): void => {
    if (!hex) return;
    const [r, g, b] = hexToRgb(hex);
    root.style.setProperty(varName, `${r} ${g} ${b}`);
  };

  setColor('--color-primary', cfg.primaryColor);
  setColor('--color-accent', cfg.accentColor);
  setColor('--brand-sidebar-bg', theme === 'dark' ? cfg.sidebarBackgroundDark : cfg.sidebarBackgroundLight);
  setColor('--brand-card-bg',    theme === 'dark' ? cfg.cardBackgroundDark    : cfg.cardBackgroundLight);
  setColor('--brand-page-bg',    theme === 'dark' ? cfg.pageBackgroundDark    : cfg.pageBackgroundLight);
  setColor('--brand-header-bg',  theme === 'dark' ? cfg.headerBackgroundDark  : cfg.headerBackgroundLight);

  if (cfg.applicationName) {
    document.title = cfg.applicationName;
  }
}

function hexToRgb(hex: string): [number, number, number] {
  const clean = hex.replace('#', '');
  const n     = parseInt(clean, 16);
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}
