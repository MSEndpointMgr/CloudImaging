import { useEffect, useRef, useState } from 'react';
import { Moon, Sun, LogOut, MapPin, ChevronDown } from 'lucide-react';
import { useLocation } from 'react-router-dom';
import { useTheme } from '../context/themeContext.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { Button } from './ui/button.tsx';
import { Select } from './ui/select.tsx';
import { resolveSectionTitle } from '../lib/routeTitles.ts';

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
  const title = resolveSectionTitle(path) ?? branding.applicationName ?? 'Cloud Imaging';

  const displayName = account?.name ?? account?.username ?? 'Signed in';
  const initials = initialsFrom(displayName) || 'U';

  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!menuOpen) return;
    const onClickOutside = (e: MouseEvent) => {
      const target = e.target as HTMLElement;
      // A surface that portals itself to <body> (the location picker's option list) is logically
      // inside this menu but is not a DOM descendant of it, so a plain containment test would
      // treat picking an option as a click outside and close the menu mid-interaction.
      if (target.closest?.('[data-portal-surface]')) return;
      if (menuRef.current && !menuRef.current.contains(target)) setMenuOpen(false);
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
            className="mx-1 flex items-center gap-3 rounded-full py-1 pl-1 pr-3 transition-colors hover:bg-accent/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          >
            {avatarUrl ? (
              <img src={avatarUrl} alt="" className="h-8 w-8 rounded-full object-cover" />
            ) : (
              <span className="flex h-8 w-8 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
                {initials}
              </span>
            )}
            <span className="hidden text-sm font-medium text-foreground sm:inline">{displayName}</span>
            <ChevronDown
              className={`h-4 w-4 text-muted-foreground transition-transform duration-150 ${menuOpen ? 'rotate-180' : ''}`}
              aria-hidden="true"
            />
          </button>

          {menuOpen && (
            <div className="absolute right-0 top-full mt-2 w-72 origin-top-right animate-popover-in rounded-xl border border-border bg-popover p-1.5 text-popover-foreground shadow-xl ring-1 ring-black/5">
              <div className="flex items-center gap-3 px-2 py-2">
                {avatarUrl ? (
                  <img src={avatarUrl} alt="" className="h-9 w-9 shrink-0 rounded-full object-cover" />
                ) : (
                  <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
                    {initials}
                  </span>
                )}
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium text-foreground">{displayName}</p>
                  {account?.username && <p className="truncate text-xs text-muted-foreground">{account.username}</p>}
                </div>
              </div>

              <div className="my-1.5 h-px bg-border" role="presentation" />

              <div className="px-2 py-1">
                <label htmlFor="header-location-select" className="mb-2 flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <MapPin className="h-3.5 w-3.5" aria-hidden="true" />
                  My location
                </label>
                <Select
                  id="header-location-select"
                  size="sm"
                  allowEmpty
                  placeholder="No location set"
                  value={preferredLocationId ?? ''}
                  onValueChange={locationId => {
                    const selected = locations.find(l => l.locationId === locationId);
                    void setPreferredLocation(selected ?? null);
                  }}
                  options={locations.map(loc => ({ value: loc.locationId, label: loc.name }))}
                />
                <p className="mt-2 text-xs leading-relaxed text-muted-foreground">
                  Filters the Devices page to devices registered at this location.
                </p>
              </div>

              <div className="my-1.5 h-px bg-border" role="presentation" />

              <button
                type="button"
                onClick={signOut}
                className="flex w-full items-center gap-2.5 rounded-md px-2 py-2 text-sm text-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <LogOut className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
                Sign out
              </button>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

