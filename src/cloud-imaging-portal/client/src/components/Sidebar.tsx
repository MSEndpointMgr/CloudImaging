import { Link, useLocation } from 'react-router-dom';
import { LayoutDashboard, Monitor, HardDrive, Disc, LifeBuoy, Palette, Settings, HardDriveDownload, BarChart3, MapPin } from 'lucide-react';
import { useBranding } from '../context/brandingContext.tsx';
import { useAuth } from '../context/authContext.tsx';
import { cn } from '../lib/utils.ts';

interface NavItem {
  label: string;
  path: string;
  icon: React.ReactNode;
  adminOnly?: boolean;
}

const navItems: NavItem[] = [
  { label: 'Dashboard',   path: '/',              icon: <LayoutDashboard size={18} /> },
  { label: 'Devices',     path: '/sessions',      icon: <Monitor   size={18} /> },
  { label: 'OS Images',   path: '/os-images',     icon: <HardDrive size={18} /> },
  { label: 'Boot Images', path: '/boot-images',   icon: <Disc      size={18} /> },
  { label: 'Recovery Images', path: '/recovery-images', icon: <LifeBuoy size={18} /> },
  { label: 'Reports',     path: '/reports',       icon: <BarChart3 size={18} />, adminOnly: true },
  { label: 'Locations',   path: '/locations',     icon: <MapPin    size={18} />, adminOnly: true },
  { label: 'Branding',    path: '/branding',      icon: <Palette   size={18} />, adminOnly: true },
  { label: 'Configuration', path: '/configuration', icon: <Settings size={18} />, adminOnly: true },
];

export function Sidebar(): React.ReactElement {
  const { pathname } = useLocation();
  const { branding, logoUrl } = useBranding();
  const { isAdministrator } = useAuth();
  const appName = branding.applicationName ?? 'Cloud Imaging';
  const visibleNavItems = navItems.filter((item) => !item.adminOnly || isAdministrator);

  return (
    <aside className="flex h-full w-60 shrink-0 flex-col border-r border-sidebar-border bg-sidebar">
      <div className="flex h-14 items-center gap-2.5 border-b border-sidebar-border px-5">
        {logoUrl ? (
          <img src={logoUrl} alt="" className="h-8 w-8 rounded-lg object-contain" />
        ) : (
          <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-sm">
            <HardDriveDownload size={18} />
          </span>
        )}
        <span className="truncate text-sm font-semibold text-sidebar-foreground">{appName}</span>
      </div>

      <nav className="flex-1 space-y-1 p-3">
        <p className="px-3 pb-1 pt-2 text-xs font-medium uppercase tracking-wider text-sidebar-foreground/50">
          Manage
        </p>
        {visibleNavItems.map((item) => {
          const isActive =
            item.path === '/' ? pathname === '/' : pathname.startsWith(item.path);

          return (
            <Link
              key={item.path}
              to={item.path}
              aria-current={isActive ? 'page' : undefined}
              className={cn(
                'flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
                isActive
                  ? 'bg-sidebar-accent text-sidebar-accent-foreground'
                  : 'text-sidebar-foreground/80 hover:bg-sidebar-accent/60 hover:text-sidebar-accent-foreground',
              )}
            >
              {item.icon}
              {item.label}
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}
