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
  /** Data URL for the configured portal logo, or null when none is set. */
  logoUrl: string | null;
  /** Re-fetches branding (and the portal logo) from the backend. */
  refresh: () => Promise<void>;
}

const BrandingContext = createContext<BrandingContextValue>({
  branding: {},
  logoUrl: null,
  refresh: async () => { /* no-op default */ },
});

// ── Persisted branding chrome ────────────────────────────────────────────────
// Branding is fetched asynchronously, so on every reload there is a window where the portal does
// not yet know whether this tenant has a custom logo. Caching the resolved logo and application
// name means a tenant that has configured a logo renders it immediately on every subsequent load,
// so the built-in mark is only ever on screen during a first-ever visit (or after the cache is
// cleared) - the chrome paints it straight away rather than sitting blank, and swaps once if a
// tenant logo turns out to be configured.
//
// The logo is held as a data URL rather than an object URL so it survives being written to
// storage. Uploads are capped at 512 KB (see BrandingPage), which stays well inside the
// localStorage quota even after base64 expansion.

const BRANDING_CACHE_KEY = 'ci-portal-branding';

interface CachedBranding {
  applicationName?: string;
  portalLogoDataUrl?: string;
}

function readCachedBranding(): CachedBranding | null {
  try {
    const raw = localStorage.getItem(BRANDING_CACHE_KEY);
    return raw ? JSON.parse(raw) as CachedBranding : null;
  } catch {
    return null;
  }
}

function writeCachedBranding(value: CachedBranding): void {
  try { localStorage.setItem(BRANDING_CACHE_KEY, JSON.stringify(value)); }
  catch { /* quota or private mode — the portal just falls back to fetching every load */ }
}

function blobToDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read logo.'));
    reader.readAsDataURL(blob);
  });
}

/**
 * Applies branding at runtime by injecting CSS custom properties (T096, FR-038).
 * Wraps the application so all components can consume live branding values.
 */
export function BrandingProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  // Seeded synchronously from the last resolved branding so the correct logo is on screen from
  // the very first paint, then reconciled with the backend by `load` below.
  const cached = useRef(readCachedBranding()).current;
  const [branding, setBranding] = useState<BrandingConfig>(
    cached?.applicationName ? { applicationName: cached.applicationName } : {});
  const [logoUrl, setLogoUrl]   = useState<string | null>(cached?.portalLogoDataUrl ?? null);
  const { theme } = useTheme();

  /**
   * Publishes the resolved logo and keeps the browser tab favicon in sync with it — the portal
   * logo is the only branding asset available for this, so it doubles as the favicon (falls back
   * to the browser's default icon when no portal logo is configured, since there is no bundled
   * default to fall back to).
   */
  const applyLogo = useCallback((dataUrl: string | null, applicationName?: string) => {
    setLogoUrl(dataUrl);
    applyFavicon(dataUrl);
    writeCachedBranding({ applicationName, portalLogoDataUrl: dataUrl ?? undefined });
  }, []);

  const load = useCallback(async () => {
    try {
      const res = await apiFetchWithRetry('/api/branding', { credentials: 'include' });
      if (res.ok) {
        const data = await res.json() as BrandingConfig;
        setBranding(data);

        if (data.portalLogoBlobPath) {
          try {
            // Stream the logo bytes through the backend (managed identity read, no SAS).
            const logoRes = await apiFetchWithRetry('/api/branding/portal-logo/content', { credentials: 'include' });
            // A failed logo read leaves whatever is already on screen in place: the tenant does
            // have a logo, so falling back to the built-in mark would be the wrong answer.
            if (logoRes.ok) {
              applyLogo(await blobToDataUrl(await logoRes.blob()), data.applicationName);
            }
          } catch { /* keep the current logo */ }
        } else {
          applyLogo(null, data.applicationName);
        }
      }
    } catch {
      // Use default CSS variables (set in index.css)
    }
  }, [applyLogo]);

  useEffect(() => { void load(); }, [load]);

  // Re-applies branding CSS variables whenever the branding config or the active light/dark
  // theme changes, so surface-colour overrides pick the correct per-theme value immediately.
  useEffect(() => { applyBrandingCssVariables(branding, theme); }, [branding, theme]);

  return (
    <BrandingContext.Provider value={{ branding, logoUrl, refresh: load }}>
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
