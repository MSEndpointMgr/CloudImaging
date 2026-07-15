import { Link, useLocation } from 'react-router-dom';
import { Monitor, HardDrive, Disc, Palette, Settings } from 'lucide-react';

interface NavItem {
  label: string;
  path: string;
  icon: React.ReactNode;
}

const navItems: NavItem[] = [
  { label: 'Sessions',    path: '/',              icon: <Monitor   size={18} /> },
  { label: 'OS Images',   path: '/os-images',     icon: <HardDrive size={18} /> },
  { label: 'Boot Images', path: '/boot-images',   icon: <Disc      size={18} /> },
  { label: 'Branding',    path: '/branding',      icon: <Palette   size={18} /> },
  { label: 'Configuration', path: '/configuration', icon: <Settings size={18} /> },
];

export function Sidebar(): React.ReactElement {
  const { pathname } = useLocation();

  return (
    <nav className="flex flex-col w-56 shrink-0 border-r border-border bg-sidebar h-full">
      <div className="px-4 py-5">
        <span className="text-sm font-semibold text-sidebar-foreground tracking-wide uppercase">
          Cloud Imaging
        </span>
      </div>
      <ul className="flex-1 space-y-1 px-2">
        {navItems.map((item) => {
          const isActive =
            item.path === '/'
              ? pathname === '/'
              : pathname.startsWith(item.path);

          return (
            <li key={item.path}>
              <Link
                to={item.path}
                className={[
                  'flex items-center gap-2.5 rounded-md px-3 py-2 text-sm font-medium transition-colors',
                  isActive
                    ? 'bg-sidebar-accent text-sidebar-accent-foreground'
                    : 'text-sidebar-foreground hover:bg-sidebar-accent/60 hover:text-sidebar-accent-foreground',
                ].join(' ')}
              >
                {item.icon}
                {item.label}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
