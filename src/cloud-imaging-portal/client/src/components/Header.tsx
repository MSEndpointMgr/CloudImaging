import { useEffect, useRef, useState } from 'react';
import { Moon, Sun, LogOut, MapPin, ChevronDown } from 'lucide-react';
import { useLocation } from 'react-router-dom';
import { useTheme } from '../context/themeContext.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { Button } from './ui/button.tsx';

const SECTION_TITLES: Record<string, string> = {
  '/': 'Dashboard',
  '/sessions': 'Devices',
  '/os-images': 'OS Images',
  '/boot-images': 'Boot Images',
  '/recovery-images': 'Recovery Images',
  '/locations': 'Locations',
  '/branding': 'Branding',
  '/configuration': 'Configuration',
};

function initialsFrom(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('');
}

/**
 * Top application bar: contextual section title on the left, theme toggle and
 * signed-in user controls on the right (shadcn styling).
 */
export function Header(): React.ReactElement {
  const { theme, toggleTheme } = useTheme();
  const { account, avatarUrl, signOut } = useAuth();
  const { branding } = useBranding();
  const { locations, preferredLocationId, setPreferredLocation } = useUserPreferences();

  const path = useLocation().pathname;
  const title =
    SECTION_TITLES[path] ??
    Object.entries(SECTION_TITLES).find(([key]) => key !== '/' && path.startsWith(key))?.[1] ??
    branding.applicationName ??
    'Cloud Imaging';

  const displayName = account?.name ?? account?.username ?? 'Signed in';
  const initials = initialsFrom(displayName) || 'U';

  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!menuOpen) return;
    const onClickOutside = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    const onEscape = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenuOpen(false); };
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onEscape);
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onEscape);
    };
  }, [menuOpen]);

  return (
    <header className="relative z-10 flex h-14 shrink-0 items-center justify-between border-b border-border bg-header/95 px-6 backdrop-blur">
      <h1 className="text-base font-semibold text-foreground">{title}</h1>

      <div className="flex items-center gap-1">
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Toggle color theme">
          {theme === 'dark' ? <Sun /> : <Moon />}
        </Button>

        <div ref={menuRef} className="relative">
          <button
            type="button"
            onClick={() => setMenuOpen(o => !o)}
            aria-haspopup="true"
            aria-expanded={menuOpen}
            className="mx-1 flex items-center gap-2.5 rounded-full py-1 pl-1 pr-2.5 hover:bg-muted/60"
          >
            {avatarUrl ? (
              <img src={avatarUrl} alt="" className="h-8 w-8 rounded-full object-cover" />
            ) : (
              <span className="flex h-8 w-8 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
                {initials}
              </span>
            )}
            <span className="hidden text-sm font-medium text-foreground sm:inline">{displayName}</span>
            <ChevronDown className="h-3.5 w-3.5 text-muted-foreground" aria-hidden="true" />
          </button>

          {menuOpen && (
            <div className="absolute right-0 top-full mt-2 w-72 rounded-md border border-border bg-popover p-3 shadow-lg">
              <div className="pb-2">
                <p className="truncate text-sm font-medium text-foreground">{displayName}</p>
                {account?.username && <p className="truncate text-xs text-muted-foreground">{account.username}</p>}
              </div>

              <div className="border-t border-border pt-3">
                <label htmlFor="header-location-select" className="mb-1.5 flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <MapPin className="h-3.5 w-3.5" aria-hidden="true" />
                  My location
                </label>
                <div className="relative">
                  <select
                    id="header-location-select"
                    value={preferredLocationId ?? ''}
                    onChange={e => {
                      const selected = locations.find(l => l.locationId === e.target.value);
                      void setPreferredLocation(selected ?? null);
                    }}
                    className="h-8 w-full appearance-none rounded-md border border-input bg-background px-2 pr-8 text-sm shadow-sm"
                  >
                    <option value="">No location set</option>
                    {locations.map(loc => (
                      <option key={loc.locationId} value={loc.locationId}>{loc.name}</option>
                    ))}
                  </select>
                  <ChevronDown className="pointer-events-none absolute right-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
                </div>
                <p className="mt-1.5 text-xs text-muted-foreground">
                  Filters the Devices page to devices registered at this location.
                </p>
              </div>

              <div className="mt-3 border-t border-border pt-2">
                <button
                  type="button"
                  onClick={signOut}
                  className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-sm text-foreground hover:bg-muted/60"
                >
                  <LogOut className="h-4 w-4" />
                  Sign out
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

