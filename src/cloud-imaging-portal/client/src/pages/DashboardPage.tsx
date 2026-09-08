import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Area, AreaChart, ResponsiveContainer } from 'recharts';
import {
  Monitor,
  HardDrive,
  Disc,
  LifeBuoy,
  BarChart3,
  MapPin,
  Palette,
  Settings,
  CheckCircle2,
  LayoutDashboard,
  ArrowRight,
  TrendingUp,
  TrendingDown,
  Minus,
} from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Badge } from '../components/ui/badge.tsx';

interface SessionLike { state: string; createdAt: string }
interface TimestampedLike { createdAt?: string; uploadedAt?: string }
interface SessionHistoryLike { finalState: string; terminalAt: string }

const ACTIVE_STATES = new Set([
  'SessionInit', 'SessionAllowed', 'SessionAssigned', 'SessionStarted', 'SessionInProgress',
]);

/** Number of trailing days each stat card's sparkline covers. */
const TREND_DAYS = 7;

interface DashboardStats {
  activeSessions: number;
  completedSessions: number;
  osImages: number;
  bootImages: number;
}

interface Trend {
  /** Per-day counts for the last `TREND_DAYS` days, oldest first. */
  series: number[];
  /** Percent change vs. the previous `TREND_DAYS`-day period, or null if there's no baseline to compare against. */
  changePct: number | null;
}

type DashboardTrends = Record<keyof DashboardStats, Trend>;

function startOfDay(date: Date): Date {
  const copy = new Date(date);
  copy.setHours(0, 0, 0, 0);
  return copy;
}

/**
 * Buckets timestamps into two consecutive `days`-long windows (previous, then current)
 * and returns the current window's daily counts plus the percent change vs. the previous
 * window. Used to derive trend sparklines for each stat card. The Completed Sessions card
 * sources its timestamps from the durable SessionHistory audit table (`/api/session-history`);
 * the other cards still derive theirs from the existing list endpoints, which is sufficient
 * since active sessions and image catalogs aren't purged the way completed sessions are.
 */
function computeTrend(timestamps: string[], days: number): Trend {
  const buckets = new Array(days * 2).fill(0) as number[];
  const today = startOfDay(new Date());
  for (const raw of timestamps) {
    const date = new Date(raw);
    if (Number.isNaN(date.getTime())) continue;
    const diffDays = Math.round((today.getTime() - startOfDay(date).getTime()) / 86_400_000);
    const idx = days * 2 - 1 - diffDays;
    if (idx >= 0 && idx < days * 2) buckets[idx] += 1;
  }
  const previous = buckets.slice(0, days);
  const current = buckets.slice(days);
  const sum = (values: number[]) => values.reduce((total, v) => total + v, 0);
  const prevSum = sum(previous);
  const currSum = sum(current);
  const changePct = prevSum === 0
    ? (currSum === 0 ? 0 : null)
    : Math.round(((currSum - prevSum) / prevSum) * 100);
  return { series: current, changePct };
}

interface StatCard {
  key: keyof DashboardStats;
  label: string;
  icon: React.ReactNode;
  /** Drill-down destination for this tile. */
  to: string;
  /** Whether to show the trend pill/sparkline for this card. Off for catalogs that aren't expected to change often. */
  showTrend: boolean;
  /** Undefined = visible to any signed-in portal role; 'operations' = Administrator/Technician only. */
  access?: 'operations';
}

/** Duration (ms) of the stat-card count-up animation. */
const COUNT_UP_DURATION_MS = 800;

/**
 * Animates a number counting up from 0 to `value` the first time it becomes available.
 * Respects `prefers-reduced-motion` by rendering the final value immediately.
 */
function AnimatedNumber({ value }: { value: number }): React.ReactElement {
  const [display, setDisplay] = useState(0);

  useEffect(() => {
    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduceMotion || value <= 0) {
      setDisplay(value);
      return;
    }
    let frame = 0;
    const start = performance.now();
    const tick = (now: number) => {
      const progress = Math.min((now - start) / COUNT_UP_DURATION_MS, 1);
      const eased = 1 - (1 - progress) ** 3; // ease-out cubic: fast start, gentle settle
      setDisplay(Math.round(eased * value));
      if (progress < 1) frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, [value]);

  return <>{display}</>;
}

const STAT_CARDS: StatCard[] = [
  { key: 'activeSessions',    label: 'Active Sessions',    icon: <Monitor      size={16} />, to: '/sessions',    showTrend: true,  access: 'operations' },
  { key: 'completedSessions', label: 'Completed Sessions', icon: <CheckCircle2 size={16} />, to: '/sessions',    showTrend: true },
  { key: 'osImages',          label: 'OS Images',          icon: <HardDrive    size={16} />, to: '/os-images',   showTrend: false, access: 'operations' },
  { key: 'bootImages',        label: 'Boot Images',        icon: <Disc         size={16} />, to: '/boot-images', showTrend: false, access: 'operations' },
];

interface NavCard {
  to: string;
  title: string;
  description: string;
  icon: React.ReactNode;
  /** Undefined = visible to any signed-in portal role. */
  access?: 'operations' | 'reports' | 'admin';
}

const NAV_CARDS: NavCard[] = [
  {
    to: '/sessions',
    title: 'Devices',
    description: 'Monitor and manage active imaging sessions. Couple devices, assign images, and track progress.',
    icon: <Monitor size={18} />,
    access: 'operations',
  },
  {
    to: '/os-images',
    title: 'OS Images',
    description: 'Browse and manage the operating system image catalog uploaded for deployment.',
    icon: <HardDrive size={18} />,
    access: 'operations',
  },
  {
    to: '/boot-images',
    title: 'Boot Images',
    description: 'WinPE boot media published from the Media Builder app.',
    icon: <Disc size={18} />,
    access: 'operations',
  },
  {
    to: '/recovery-images',
    title: 'Recovery Images',
    description: 'Custom Windows Recovery Environment (WinRE) images published for deployment.',
    icon: <LifeBuoy size={18} />,
    access: 'operations',
  },
  {
    to: '/reports',
    title: 'Reports',
    description: 'Session outcomes, failure details, and image inventory across the fleet.',
    icon: <BarChart3 size={18} />,
    access: 'reports',
  },
  {
    to: '/locations',
    title: 'Locations',
    description: 'Manage the site catalog devices are registered against.',
    icon: <MapPin size={18} />,
    access: 'admin',
  },
  {
    to: '/branding',
    title: 'Branding',
    description: 'Customise how the portal and boot media appear to operators.',
    icon: <Palette size={18} />,
    access: 'admin',
  },
  {
    to: '/configuration',
    title: 'Configuration',
    description: 'Deployment settings, security options, and boot media certificate management.',
    icon: <Settings size={18} />,
    access: 'admin',
  },
];

/**
 * Portal start page: a dashboard with at-a-glance stat cards and quick links
 * into each section. Stat counts load asynchronously with skeleton placeholders.
 */
export default function DashboardPage(): React.ReactElement {
  const { isAdministrator, isTechnician, isReader } = useAuth();
  const canOperations = isAdministrator || isTechnician;
  const canReports = isAdministrator || isReader;
  const { branding } = useBranding();
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [trends, setTrends] = useState<DashboardTrends | null>(null);

  const appName = branding.applicationName ?? 'Cloud Imaging';

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const historyFrom = new Date(Date.now() - TREND_DAYS * 2 * 86_400_000).toISOString();
        // Reader holds none of the roles the sessions/images/boot-images endpoints accept, so
        // skip those fetches entirely rather than let them 403 — session-history alone covers
        // Reader's one visible stat (Completed Sessions).
        const [sRes, iRes, bRes, hRes] = await Promise.all([
          canOperations ? apiFetchWithRetry('/api/sessions',    { credentials: 'include' }) : Promise.resolve(null),
          canOperations ? apiFetchWithRetry('/api/images',      { credentials: 'include' }) : Promise.resolve(null),
          canOperations ? apiFetchWithRetry('/api/boot-images', { credentials: 'include' }) : Promise.resolve(null),
          apiFetchWithRetry(`/api/session-history?from=${encodeURIComponent(historyFrom)}`, { credentials: 'include' }),
        ]);
        const sessions   = sRes?.ok ? (await sRes.json() as SessionLike[])   : [];
        const osImages   = iRes?.ok ? (await iRes.json() as TimestampedLike[]) : [];
        const bootImages = bRes?.ok ? (await bRes.json() as TimestampedLike[]) : [];
        // Completed-session counts/trends are sourced from the durable SessionHistory audit
        // table rather than the live `/api/sessions` list, which only ever reflects sessions
        // still within the (much shorter) live-session purge window — using it here would
        // silently undercount completions older than that window (Reports feature, Phase 7).
        const history = hRes.ok ? (await hRes.json() as SessionHistoryLike[]) : [];
        const completedTerminalAts = history
          .filter(h => h.finalState === 'SessionCompleted')
          .map(h => h.terminalAt);
        const cutoff = Date.now() - TREND_DAYS * 86_400_000;
        if (cancelled) return;
        setStats({
          activeSessions:    sessions.filter(s => ACTIVE_STATES.has(s.state)).length,
          completedSessions: completedTerminalAts.filter(t => new Date(t).getTime() >= cutoff).length,
          osImages:          osImages.length,
          bootImages:        bootImages.length,
        });
        const timestampsOf = (items: TimestampedLike[]) =>
          items.map(i => i.createdAt ?? i.uploadedAt).filter((d): d is string => !!d);
        setTrends({
          activeSessions:    computeTrend(sessions.filter(s => ACTIVE_STATES.has(s.state)).map(s => s.createdAt), TREND_DAYS),
          completedSessions: computeTrend(completedTerminalAts, TREND_DAYS),
          osImages:          computeTrend(timestampsOf(osImages), TREND_DAYS),
          bootImages:        computeTrend(timestampsOf(bootImages), TREND_DAYS),
        });
      } catch {
        if (!cancelled) setStats({ activeSessions: 0, completedSessions: 0, osImages: 0, bootImages: 0 });
      }
    })();
    return () => { cancelled = true; };
  }, [canOperations]);

  const visibleStatCards = STAT_CARDS.filter(c => c.access !== 'operations' || canOperations);
  const visibleNavCards = NAV_CARDS.filter(c => {
    switch (c.access) {
      case 'operations': return canOperations;
      case 'reports':    return canReports;
      case 'admin':      return isAdministrator;
      default:           return true;
    }
  });

  return (
    <div className="space-y-8">
      {/* Stat cards */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {visibleStatCards.map(card => {
          const trend = card.showTrend ? trends?.[card.key] : undefined;
          const changePct = trend?.changePct ?? null;
          // A trend pill comparing against zero is meaningless (e.g. "0%" next to a 0 count), and
          // a null changePct means there's no previous-window baseline to compute a percentage
          // from at all — show the pill only when there's an actual number to report.
          const showPill = trend != null && !!stats && stats[card.key] > 0 && changePct !== null;
          const badgeVariant = changePct == null ? 'muted' : changePct > 0 ? 'success' : changePct < 0 ? 'negative' : 'muted';
          const TrendIcon = changePct == null || changePct === 0 ? Minus : changePct > 0 ? TrendingUp : TrendingDown;
          return (
            <Link key={card.key} to={card.to} className="group block">
              <Card className="transition-colors hover:border-primary/50 hover:bg-accent/40">
                <CardContent className="p-5">
                  <div className="flex items-center gap-2 text-sm text-muted-foreground">
                    {card.icon}
                    <span>{card.label}</span>
                  </div>
                  <div className="mt-3 flex items-end justify-between gap-2">
                    {stats ? (
                      <p className="text-3xl font-semibold tabular-nums"><AnimatedNumber value={stats[card.key]} /></p>
                    ) : (
                      <Skeleton className="h-9 w-16" />
                    )}
                    {showPill && (
                      <Badge variant={badgeVariant} title={`vs. previous ${TREND_DAYS} days`}>
                        <TrendIcon size={11} />
                        {`${Math.abs(changePct ?? 0)}%`}
                      </Badge>
                    )}
                  </div>
                  <div className="mt-3 h-10">
                    {!card.showTrend ? null : trend ? (
                      <ResponsiveContainer width="100%" height="100%">
                        <AreaChart data={trend.series.map(v => ({ v }))}>
                          <defs>
                            <linearGradient id={`spark-${card.key}`} x1="0" y1="0" x2="0" y2="1">
                              <stop offset="5%" stopColor="currentColor" stopOpacity={0.35} />
                              <stop offset="95%" stopColor="currentColor" stopOpacity={0} />
                            </linearGradient>
                          </defs>
                          <Area
                            type="monotone"
                            dataKey="v"
                            stroke="currentColor"
                            strokeWidth={1.5}
                            fill={`url(#spark-${card.key})`}
                            className="text-primary"
                            isAnimationActive={false}
                            dot={false}
                          />
                        </AreaChart>
                      </ResponsiveContainer>
                    ) : (
                      <Skeleton className="h-full w-full" />
                    )}
                  </div>
                </CardContent>
              </Card>
            </Link>
          );
        })}
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
