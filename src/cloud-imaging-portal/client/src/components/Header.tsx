import { Moon, Sun, LogOut } from 'lucide-react';
import { useLocation } from 'react-router-dom';
import { useTheme } from '../context/themeContext.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { Button } from './ui/button.tsx';

const SECTION_TITLES: Record<string, string> = {
  '/': 'Dashboard',
  '/sessions': 'Devices',
  '/os-images': 'OS Images',
  '/boot-images': 'Boot Images',
  '/recovery-images': 'Recovery Images',
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

  const path = useLocation().pathname;
  const title =
    SECTION_TITLES[path] ??
    Object.entries(SECTION_TITLES).find(([key]) => key !== '/' && path.startsWith(key))?.[1] ??
    branding.applicationName ??
    'Cloud Imaging';

  const displayName = account?.name ?? account?.username ?? 'Signed in';
  const initials = initialsFrom(displayName) || 'U';

  return (
    <header className="relative z-10 flex h-14 shrink-0 items-center justify-between border-b border-border bg-background/95 px-6 backdrop-blur">
      <h1 className="text-base font-semibold text-foreground">{title}</h1>

      <div className="flex items-center gap-1">
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Toggle color theme">
          {theme === 'dark' ? <Sun /> : <Moon />}
        </Button>

        <div className="mx-1 flex items-center gap-2.5 rounded-full py-1 pl-1 pr-3">
          {avatarUrl ? (
            <img src={avatarUrl} alt="" className="h-8 w-8 rounded-full object-cover" />
          ) : (
            <span className="flex h-8 w-8 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
              {initials}
            </span>
          )}
          <span className="hidden text-sm font-medium text-foreground sm:inline">{displayName}</span>
        </div>

        <Button variant="ghost" size="icon" onClick={signOut} aria-label="Sign out" title="Sign out">
          <LogOut />
        </Button>
      </div>
    </header>
  );
}
