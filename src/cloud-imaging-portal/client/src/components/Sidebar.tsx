import { Link, useLocation } from 'react-router-dom';
import { LayoutDashboard, Monitor, HardDrive, Disc, LifeBuoy, Palette, Settings, HardDriveDownload, BarChart3, MapPin } from 'lucide-react';
import { useBranding } from '../context/brandingContext.tsx';
import { useAuth } from '../context/authContext.tsx';
import { cn } from '../lib/utils.ts';

interface NavItem {
  label: string;
  path: string;
  icon: React.ReactNode;
  /** Undefined = visible to any signed-in portal role (Administrator/Technician/Reader). */
  access?: 'operations' | 'reports' | 'admin';
}

interface NavSection {
  title: string;
  items: NavItem[];
}

// Grouped rather than presented as one flat list: an administrator sees nine destinations, which
// is past the point where a single undifferentiated column can be scanned by shape. The headings
// also give the destructive/tenant-wide screens (Locations, Branding, Configuration) a visible
// boundary away from the day-to-day imaging work.
const navSections: NavSection[] = [
  {
    title: 'Overview',
    items: [
      { label: 'Dashboard', path: '/', icon: <LayoutDashboard size={18} /> },
    ],
  },
  {
    title: 'Imaging',
    items: [
      { label: 'Devices',         path: '/sessions',         icon: <Monitor   size={18} />, access: 'operations' },
      { label: 'OS Images',       path: '/os-images',        icon: <HardDrive size={18} />, access: 'operations' },
      { label: 'Boot Images',     path: '/boot-images',      icon: <Disc      size={18} />, access: 'operations' },
      { label: 'Recovery Images', path: '/recovery-images',  icon: <LifeBuoy  size={18} />, access: 'operations' },
    ],
  },
  {
    title: 'Insights',
    items: [
      { label: 'Reports', path: '/reports', icon: <BarChart3 size={18} />, access: 'reports' },
    ],
  },
  {
    title: 'Administration',
    items: [
      { label: 'Locations',     path: '/locations',     icon: <MapPin   size={18} />, access: 'admin' },
      { label: 'Branding',      path: '/branding',      icon: <Palette  size={18} />, access: 'admin' },
      { label: 'Configuration', path: '/configuration', icon: <Settings size={18} />, access: 'admin' },
    ],
  },
];

export function Sidebar(): React.ReactElement {
  const { pathname } = useLocation();
  const { branding, logoUrl } = useBranding();
  const { isAdministrator, isTechnician, isReader } = useAuth();
  const canOperations = isAdministrator || isTechnician;
  const canReports = isAdministrator || isReader;
  const appName = branding.applicationName ?? 'Cloud Imaging';
  const canAccess = (item: NavItem): boolean => {
    switch (item.access) {
      case 'operations': return canOperations;
      case 'reports':    return canReports;
      case 'admin':      return isAdministrator;
      default:           return true;
    }
  };
  // Sections whose every item is filtered out by role are dropped entirely, so a Reader never
  // sees an "Administration" heading with nothing beneath it.
  const visibleSections = navSections
    .map((section) => ({ ...section, items: section.items.filter(canAccess) }))
    .filter((section) => section.items.length > 0);

  return (
    <aside className="flex h-full w-60 shrink-0 flex-col border-r border-sidebar-border bg-sidebar">
      {/* Rendered immediately rather than held back until `/api/branding` answers. The resolved
          logo is cached in localStorage, so from the second visit onwards the correct mark is on
          screen in the first frame and there is nothing to wait for; waiting only ever produced a
          blank header - for the whole request on a first visit, and on every single load for a
          tenant whose branding call fails, since a failed call never populates the cache. */}
      {/* px-6 (not px-5) so the logo's left edge lands on the same 24px rail as the nav icons
          below it, which sit at nav's p-3 plus each link's px-3. */}
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
        {visibleSections.map((section) => (
          <div key={section.title} className="space-y-1">
            <p className="px-3 pb-1 text-xs font-medium uppercase tracking-wider text-sidebar-foreground/50">
              {section.title}
            </p>
            {section.items.map((item) => {
              const isActive =
                item.path === '/' ? pathname === '/' : pathname.startsWith(item.path);

              return (
                <Link
                  key={item.path}
                  to={item.path}
                  aria-current={isActive ? 'page' : undefined}
                  className={cn(
                    'relative flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
                    // Brand-coloured rail marking the active destination. Rendered as a
                    // permanently present pseudo-element that only changes opacity, so switching
                    // pages cannot reflow the row.
                    'before:absolute before:left-0 before:top-1/2 before:h-5 before:w-[3px] before:-translate-y-1/2 before:rounded-r-full before:bg-primary before:transition-opacity',
                    isActive
                      // The tint and rail carry the brand colour, but the label deliberately does
                      // NOT: --color-primary is a single tenant-overridable value shared by both
                      // themes, and the default blue-600 as text on the dark sidebar is only
                      // 3.6:1. Keeping the label on the sidebar's own accent foreground means no
                      // tenant can pick a brand colour that makes the active item unreadable.
                      ? 'bg-primary/10 text-sidebar-accent-foreground before:opacity-100'
                      : 'text-sidebar-foreground/80 before:opacity-0 hover:bg-sidebar-accent/60 hover:text-sidebar-accent-foreground',
                  )}
                >
                  {item.icon}
                  {item.label}
                </Link>
              );
            })}
          </div>
        ))}
      </nav>
    </aside>
  );
}
