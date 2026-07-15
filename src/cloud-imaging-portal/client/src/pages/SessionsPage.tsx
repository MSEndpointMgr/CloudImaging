// Full Sessions page implementation — see import below
export default function SessionsPage(): React.ReactElement {
  return <SessionsPageImpl />;
}

import { useState, useEffect, useCallback, useRef } from 'react';
import { RefreshCw } from 'lucide-react';
import { SessionFilterTabs } from '../components/SessionFilterTabs.tsx';
import { CoupleSessionDialog } from '../components/CoupleSessionDialog.tsx';
import { AssignImageDialog } from '../components/AssignImageDialog.tsx';

interface Session {
  sessionId: string;
  state: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  overallProgressPercent: number;
  currentStep: string | null;
}

const ACTIVE_STATES = new Set(['SessionInit','SessionAllowed','SessionAssigned','SessionStarted','SessionInProgress']);

function deriveCounts(sessions: Session[]) {
  return {
    active:    sessions.filter(s => ACTIVE_STATES.has(s.state)).length,
    completed: sessions.filter(s => s.state === 'SessionCompleted').length,
    failed:    sessions.filter(s => s.state === 'SessionFailed').length,
    all:       sessions.length,
  };
}

function applyFilter(sessions: Session[], filter: string): Session[] {
  switch (filter) {
    case 'active':    return sessions.filter(s => ACTIVE_STATES.has(s.state));
    case 'completed': return sessions.filter(s => s.state === 'SessionCompleted');
    case 'failed':    return sessions.filter(s => s.state === 'SessionFailed');
    default:          return sessions;
  }
}

function stateLabel(state: string): string {
  return state.replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
}

function stateBadgeClass(state: string): string {
  switch (state) {
    case 'SessionCompleted': return 'bg-green-100 text-green-800';
    case 'SessionFailed':    return 'bg-red-100 text-red-800';
    case 'SessionNotAuthorized': return 'bg-yellow-100 text-yellow-800';
    case 'SessionInProgress':
    case 'SessionStarted':   return 'bg-blue-100 text-blue-800';
    default:                 return 'bg-gray-100 text-gray-700';
  }
}

function SessionsPageImpl(): React.ReactElement {
  const [sessions, setSessions]         = useState<Session[]>([]);
  const [filter, setFilter]             = useState('active');
  const [checked, setChecked]           = useState<Set<string>>(new Set());
  const [coupleOpen, setCoupleOpen]     = useState(false);
  const [assignOpen, setAssignOpen]     = useState(false);
  const [assignTarget, setAssignTarget] = useState<string | null>(null);
  const [loading, setLoading]           = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const fetchSessions = useCallback(async (data?: Session[]) => {
    setLoading(true);
    try {
      const res = await fetch('/api/sessions', { credentials: 'include' });
      if (res.ok) {
        const fetched = await res.json() as Session[];
        setSessions(fetched);
        return fetched;
      }
    } catch { /* retain previous */ }
    finally { setLoading(false); }
    return data ?? [];
  }, []);

  const scheduleNextPoll = useCallback((data: Session[]) => {
    if (timerRef.current) clearTimeout(timerRef.current);
    const hasHot = data.some(s => s.state === 'SessionStarted' || s.state === 'SessionInProgress');
    timerRef.current = setTimeout(async () => {
      const next = await fetchSessions(data);
      scheduleNextPoll(next);
    }, hasHot ? 5_000 : 30_000);
  }, [fetchSessions]);

  useEffect(() => {
    void (async () => { const data = await fetchSessions(); scheduleNextPoll(data); })();
    return () => { if (timerRef.current) clearTimeout(timerRef.current); };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const visible  = applyFilter(sessions, filter);
  const counts   = deriveCounts(sessions);
  const toggleRow    = (id: string) => setChecked(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const selectAll    = () => setChecked(new Set(visible.map(s => s.sessionId)));
  const deselectAll  = () => setChecked(new Set());
  const eligibleCount = [...checked].filter(id => sessions.find(s => s.sessionId === id)?.state === 'SessionAssigned').length;

  const handleRefresh = () => {
    void (async () => { const data = await fetchSessions(); scheduleNextPoll(data); })();
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">Sessions</h1>
        <div className="flex items-center gap-2">
          <button onClick={handleRefresh} className="flex items-center gap-1.5 px-3 py-1.5 text-sm border border-border rounded-md hover:bg-muted">
            <RefreshCw size={14} className={loading ? 'animate-spin' : ''} /> Refresh
          </button>
          <button onClick={() => setCoupleOpen(true)} className="px-4 py-1.5 text-sm bg-primary text-primary-foreground rounded-md hover:bg-primary/90">
            Couple Device
          </button>
        </div>
      </div>

      <SessionFilterTabs activeFilter={filter} onFilterChange={setFilter} counts={counts} />

      <div className="flex gap-3 text-sm">
        <button onClick={selectAll}   className="text-primary hover:underline">Select All</button>
        <button onClick={deselectAll} className="text-muted-foreground hover:underline">Deselect All</button>
      </div>

      <div className="rounded-md border border-border overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-muted/50">
            <tr>
              <th className="w-8 px-3 py-2" />
              <th className="px-3 py-2 text-left font-medium">Serial</th>
              <th className="px-3 py-2 text-left font-medium">Device</th>
              <th className="px-3 py-2 text-left font-medium">State</th>
              <th className="px-3 py-2 text-left font-medium">Progress</th>
              <th className="px-3 py-2 text-left font-medium">Step</th>
              <th className="px-3 py-2 text-left font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {visible.length === 0 ? (
              <tr><td colSpan={7} className="py-8 text-center text-muted-foreground">{loading ? 'Loading…' : 'No sessions.'}</td></tr>
            ) : visible.map(s => (
              <tr key={s.sessionId} className="border-t border-border hover:bg-muted/30">
                <td className="px-3 py-2"><input type="checkbox" checked={checked.has(s.sessionId)} onChange={() => toggleRow(s.sessionId)} className="rounded" /></td>
                <td className="px-3 py-2 font-mono text-xs">{s.deviceSerialNumber}</td>
                <td className="px-3 py-2">{s.deviceManufacturer} {s.deviceModel}</td>
                <td className="px-3 py-2"><span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${stateBadgeClass(s.state)}`}>{stateLabel(s.state)}</span></td>
                <td className="px-3 py-2">
                  {s.overallProgressPercent > 0 ? (
                    <div className="flex items-center gap-2">
                      <div className="w-20 bg-muted rounded-full h-1.5"><div className="bg-primary h-1.5 rounded-full" style={{ width: `${s.overallProgressPercent}%` }} /></div>
                      <span className="text-xs text-muted-foreground">{s.overallProgressPercent}%</span>
                    </div>
                  ) : '—'}
                </td>
                <td className="px-3 py-2 text-xs text-muted-foreground">{s.currentStep ?? '—'}</td>
                <td className="px-3 py-2">
                  {s.state === 'SessionAssigned' && (
                    <button onClick={() => { setAssignTarget(s.sessionId); setAssignOpen(true); }} className="px-2 py-1 text-xs bg-secondary text-secondary-foreground rounded hover:bg-secondary/80">Assign Image</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {eligibleCount > 0 && (
        <div className="flex items-center justify-between rounded-md bg-primary/10 border border-primary/30 px-4 py-2 text-sm">
          <span>{eligibleCount} Assigned session{eligibleCount !== 1 ? 's' : ''} selected</span>
          <button onClick={() => { setAssignTarget(null); setAssignOpen(true); }} className="px-3 py-1 bg-primary text-primary-foreground rounded text-xs">Bulk Assign Image</button>
        </div>
      )}

      <CoupleSessionDialog open={coupleOpen} onClose={() => setCoupleOpen(false)} onCoupled={() => handleRefresh()} />
      <AssignImageDialog
        open={assignOpen}
        sessionId={assignTarget ?? (checked.size > 0 ? [...checked][0]! : null)}
        onClose={() => { setAssignOpen(false); setAssignTarget(null); }}
        onAssigned={() => handleRefresh()}
      />
    </div>
  );
}
