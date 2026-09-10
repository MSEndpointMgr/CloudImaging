import {
  LayoutDashboard,
  Monitor,
  HardDrive,
  Disc,
  LifeBuoy,
  BarChart3,
  MapPin,
  Palette,
  Settings,
  HardDriveDownload,
  Lock,
  ShieldAlert,
  LogOut,
  Moon,
  Sun,
} from 'lucide-react';
import { useTheme } from '../context/themeContext.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { Button } from './ui/button.tsx';

// Mirrors the section grouping and labels of the real Sidebar. This is the only view of the
// navigation a role-less user ever gets, so showing them a structure the portal no longer has
// would misrepresent what they are being granted access to. Every destination is listed here
// regardless of the role that would normally gate it, because the point is to show the whole
// product behind the lock.
const NAV_SECTIONS = [
  {
    title: 'Overview',
    items: [{ label: 'Dashboard', icon: <LayoutDashboard size={18} /> }],
  },
  {
    title: 'Imaging',
    items: [
      { label: 'Devices', icon: <Monitor size={18} /> },
      { label: 'OS Images', icon: <HardDrive size={18} /> },
      { label: 'Boot Images', icon: <Disc size={18} /> },
      { label: 'Recovery Images', icon: <LifeBuoy size={18} /> },
    ],
  },
  {
    title: 'Insights',
    items: [{ label: 'Reports', icon: <BarChart3 size={18} /> }],
  },
  {
    title: 'Administration',
    items: [
      { label: 'Locations', icon: <MapPin size={18} /> },
      { label: 'Branding', icon: <Palette size={18} /> },
      { label: 'Configuration', icon: <Settings size={18} /> },
    ],
  },
];

function initialsFrom(name: string): string {
  return (
    name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]?.toUpperCase() ?? '')
      .join('') || 'U'
  );
}

/**
 * Shown to users who are signed in but have no portal role assigned (FR-040a).
 * Renders the full portal chrome (branded sidebar and header), but with every
 * navigation target locked, so it is clear the user has reached the portal yet
 * cannot act until an administrator grants them a role.
 */
export function AccessDenied(): React.ReactElement {
  const { theme, toggleTheme } = useTheme();
  const { account, avatarUrl, signOut } = useAuth();
  const { branding, logoUrl } = useBranding();

  const appName = branding.applicationName ?? 'Cloud Imaging';
  const displayName = account?.name ?? account?.username ?? 'Signed in';
  const email = account?.username ?? '';
  const initials = initialsFrom(displayName);

  return (
    <div className="flex h-screen overflow-hidden bg-background text-foreground">
      {/* Locked sidebar (decorative, non-interactive) */}
      <aside
        aria-hidden="true"
        className="flex h-full w-60 shrink-0 flex-col border-r border-sidebar-border bg-sidebar"
      >
        <div className="flex h-14 items-center gap-3 border-b border-sidebar-border px-6">
          {logoUrl ? (
            <img src={logoUrl} alt="" className="h-8 w-8 rounded-lg object-contain" />
          ) : (
            <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-sm">
              <HardDriveDownload size={18} />
            </span>
          )}
          <span className="truncate text-sm font-semibold text-sidebar-foreground">{appName}</span>
        </div>

        <nav className="flex-1 space-y-6 overflow-y-auto p-3">
          {NAV_SECTIONS.map((section) => (
            <div key={section.title} className="space-y-1">
              <p className="px-3 pb-1 text-xs font-medium uppercase tracking-wider text-sidebar-foreground/50">
                {section.title}
              </p>
              {section.items.map((item) => (
                <div
                  key={item.label}
                  className="flex cursor-not-allowed select-none items-center gap-3 rounded-md px-3 py-2 text-sm font-medium text-sidebar-foreground/35"
                >
                  {item.icon}
                  <span className="flex-1">{item.label}</span>
                  <Lock size={16} className="text-sidebar-foreground/30" />
                </div>
              ))}
            </div>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
        {/* Header, matches the authenticated app bar */}
        <header className="flex h-14 shrink-0 items-center justify-between border-b border-border bg-background/95 px-6 backdrop-blur">
          <h1 className="text-base font-semibold text-foreground">{appName}</h1>

          <div className="flex items-center gap-1">
            <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Toggle color theme">
              {theme === 'dark' ? <Sun /> : <Moon />}
            </Button>

            <div className="mx-1 flex items-center gap-3 rounded-full py-1 pl-1 pr-3">
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

        {/* Locked workspace */}
        <main className="flex flex-1 items-start justify-center overflow-y-auto bg-muted/30 p-6 pt-[15vh]">
          <div className="w-full max-w-md rounded-xl border border-border bg-card p-8 text-center shadow-sm">
            <span className="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-amber-500/10 text-amber-500">
              <ShieldAlert size={24} />
            </span>

            <h2 className="mt-5 text-lg font-semibold text-foreground">No access assigned</h2>

            <p className="mt-2 text-sm text-muted-foreground">
              You are signed in to {appName}
              {email ? (
                <>
                  {' '}
                  as <span className="font-medium text-foreground">{email}</span>
                </>
              ) : null}
              , but your account has not been granted a Cloud Imaging portal role yet.
            </p>
          </div>
        </main>
      </div>
    </div>
  );
}
