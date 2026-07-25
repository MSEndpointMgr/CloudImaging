import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Monitor,
  HardDrive,
  Disc,
  Palette,
  Settings,
  CheckCircle2,
  LayoutDashboard,
  ArrowRight,
} from 'lucide-react';
import { apiFetch } from '../lib/apiClient.ts';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';

interface SessionLike { state: string }

const ACTIVE_STATES = new Set([
  'SessionInit', 'SessionAllowed', 'SessionAssigned', 'SessionStarted', 'SessionInProgress',
]);

interface DashboardStats {
  activeSessions: number;
  completedSessions: number;
  osImages: number;
  bootImages: number;
}

interface StatCard {
  key: keyof DashboardStats;
  label: string;
  icon: React.ReactNode;
}

const STAT_CARDS: StatCard[] = [
  { key: 'activeSessions',    label: 'Active Sessions',    icon: <Monitor      size={16} /> },
  { key: 'completedSessions', label: 'Completed Sessions', icon: <CheckCircle2 size={16} /> },
  { key: 'osImages',          label: 'OS Images',          icon: <HardDrive    size={16} /> },
  { key: 'bootImages',        label: 'Boot Images',        icon: <Disc         size={16} /> },
];

interface NavCard {
  to: string;
  title: string;
  description: string;
  icon: React.ReactNode;
  adminOnly?: boolean;
}

const NAV_CARDS: NavCard[] = [
  {
    to: '/sessions',
    title: 'Sessions',
    description: 'Monitor and manage active imaging sessions — couple devices, assign images, and track progress.',
    icon: <Monitor size={18} />,
  },
  {
    to: '/os-images',
    title: 'OS Images',
    description: 'Browse and manage the operating system image catalog uploaded for deployment.',
    icon: <HardDrive size={18} />,
  },
  {
    to: '/boot-images',
    title: 'Boot Images',
    description: 'WinPE boot media published from the Media Builder app.',
    icon: <Disc size={18} />,
  },
  {
    to: '/branding',
    title: 'Branding',
    description: 'Customise how the portal and boot media appear to operators.',
    icon: <Palette size={18} />,
    adminOnly: true,
  },
  {
    to: '/configuration',
    title: 'Configuration',
    description: 'Deployment settings, security options, and boot media certificate management.',
    icon: <Settings size={18} />,
    adminOnly: true,
  },
];

/**
 * Portal start page: a dashboard with at-a-glance stat cards and quick links
 * into each section. Stat counts load asynchronously with skeleton placeholders.
 */
export default function DashboardPage(): React.ReactElement {
  const { isAdministrator } = useAuth();
  const { branding } = useBranding();
  const [stats, setStats] = useState<DashboardStats | null>(null);

  const appName = branding.applicationName ?? 'Cloud Imaging';

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [sRes, iRes, bRes] = await Promise.all([
          apiFetch('/api/sessions',    { credentials: 'include' }),
          apiFetch('/api/images',      { credentials: 'include' }),
          apiFetch('/api/boot-images', { credentials: 'include' }),
        ]);
        const sessions   = sRes.ok ? (await sRes.json() as SessionLike[]) : [];
        const osImages   = iRes.ok ? (await iRes.json() as unknown[])     : [];
        const bootImages = bRes.ok ? (await bRes.json() as unknown[])     : [];
        if (cancelled) return;
        setStats({
          activeSessions:    sessions.filter(s => ACTIVE_STATES.has(s.state)).length,
          completedSessions: sessions.filter(s => s.state === 'SessionCompleted').length,
          osImages:          osImages.length,
          bootImages:        bootImages.length,
        });
      } catch {
        if (!cancelled) setStats({ activeSessions: 0, completedSessions: 0, osImages: 0, bootImages: 0 });
      }
    })();
    return () => { cancelled = true; };
  }, []);

  const visibleNavCards = NAV_CARDS.filter(c => !c.adminOnly || isAdministrator);

  return (
    <div className="space-y-8">
      {/* Stat cards */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {STAT_CARDS.map(card => (
          <Card key={card.key}>
            <CardContent className="p-5">
              <div className="flex items-center gap-2 text-sm text-muted-foreground">
                {card.icon}
                <span>{card.label}</span>
              </div>
              <div className="mt-3">
                {stats ? (
                  <p className="text-3xl font-semibold tabular-nums">{stats[card.key]}</p>
                ) : (
                  <Skeleton className="h-9 w-16" />
                )}
              </div>
            </CardContent>
          </Card>
        ))}
      </div>

      {/* Welcome / intro */}
      <div className="space-y-2">
        <div className="flex items-center gap-2">
          <LayoutDashboard size={22} className="text-primary" />
          <h2 className="text-lg font-semibold">Welcome to {appName}</h2>
        </div>
        <p className="max-w-3xl text-sm text-muted-foreground">
          Manage your cloud-based Windows imaging environment from one place. Jump into a
          section below to monitor imaging sessions, manage the OS and boot image catalogs,
          or adjust deployment settings.
        </p>
      </div>

      {/* Quick-link navigation cards */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {visibleNavCards.map(card => (
          <Link key={card.to} to={card.to} className="group">
            <Card className="h-full transition-colors hover:border-primary/50 hover:bg-accent/40">
              <CardContent className="flex h-full flex-col p-5">
                <div className="flex items-center gap-2.5">
                  <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
                    {card.icon}
                  </span>
                  <h3 className="text-sm font-semibold">{card.title}</h3>
                </div>
                <p className="mt-3 flex-1 text-sm text-muted-foreground">{card.description}</p>
                <span className="mt-4 inline-flex items-center gap-1 text-xs font-medium text-primary opacity-0 transition-opacity group-hover:opacity-100">
                  Open <ArrowRight size={13} />
                </span>
              </CardContent>
            </Card>
          </Link>
        ))}
      </div>
    </div>
  );
}
