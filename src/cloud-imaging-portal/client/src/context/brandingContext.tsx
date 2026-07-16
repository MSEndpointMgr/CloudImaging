import { createContext, useContext, useEffect, useState } from 'react';

interface BrandingConfig {
  primaryColor?: string;
  accentColor?: string;
  applicationName?: string;
  logoBlobPath?: string;
}

interface BrandingContextValue {
  branding: BrandingConfig;
  isLoaded: boolean;
}

const BrandingContext = createContext<BrandingContextValue>({
  branding: {},
  isLoaded: false,
});

/**
 * Applies branding at runtime by injecting CSS custom properties (T096, FR-038).
 * Wraps the application so all components can consume live branding values.
 */
export function BrandingProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  const [branding, setBranding] = useState<BrandingConfig>({});
  const [isLoaded, setIsLoaded] = useState(false);

  useEffect(() => {
    void (async () => {
      try {
        const res = await fetch('/api/branding', { credentials: 'include' });
        if (res.ok) {
          const data = await res.json() as BrandingConfig;
          setBranding(data);
          applyBrandingCssVariables(data);
        }
      } catch {
        // Use default CSS variables (set in index.css)
      } finally {
        setIsLoaded(true);
      }
    })();
  }, []);

  return (
    <BrandingContext.Provider value={{ branding, isLoaded }}>
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
