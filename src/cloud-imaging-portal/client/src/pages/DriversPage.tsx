import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Plus, RefreshCw } from 'lucide-react';
import { Button } from '../components/ui/button.tsx';
import { CountPill } from '../components/ui/count-pill.tsx';
import { SearchInput } from '../components/ui/search-input.tsx';
import { Select } from '../components/ui/select.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { DOWNLOADS_POLL_MS, DownloadsTab } from '../components/drivers/DownloadsTab.tsx';
import { ModelsTable } from '../components/drivers/ModelsTable.tsx';
import { OnboardModelDialog } from '../components/drivers/OnboardModelDialog.tsx';
import { RulesTab } from '../components/drivers/RulesTab.tsx';
import { StorageTab } from '../components/drivers/StorageTab.tsx';
import { SyncTab } from '../components/drivers/SyncTab.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import {
  DRIVERS_TABS, availableAgainText, checkAllModels, fetchDownloads, fetchModels, fetchSyncSettings, fetchVendors, parseDriversTab, vendorLabel,
  type DriverModelRow, type DriverSyncSettings, type DriverVendorRow, type DriversTab,
} from '../lib/drivers.ts';
import { cn, formatTimestamp } from '../lib/utils.ts';

const ALL_VENDORS = 'all';

/** Models tab: every onboarded model, with onboarding and Check for updates for Operators. */
function ModelsTab(): React.ReactElement {
  const navigate = useNavigate();
  const { canManageDrivers } = useAuth();
  const { notify } = useToast();
  const [models, setModels] = useState<DriverModelRow[] | null>(null);
  const [vendors, setVendors] = useState<DriverVendorRow[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [vendor, setVendor] = useState(ALL_VENDORS);
  const [onboarding, setOnboarding] = useState(false);
  const [checking, setChecking] = useState(false);
  const [sync, setSync] = useState<DriverSyncSettings | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [modelList, vendorList] = await Promise.all([fetchModels(), fetchVendors()]);
      setModels(modelList);
      setVendors(vendorList);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load driver models.');
    }
    // The last check time is extra information; the tab works without it.
    fetchSyncSettings().then(setSync).catch(() => setSync(null));
  }, []);

  useEffect(() => { void load(); }, [load]);

  const visible = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (models ?? []).filter(m =>
      (vendor === ALL_VENDORS || m.vendor === vendor)
      && (!term || m.displayName.toLowerCase().includes(term) || m.matchKeys.some(k => k.toLowerCase().includes(term))));
  }, [models, search, vendor]);

  const checkAll = async () => {
    setChecking(true);
    try {
      await checkAllModels();
      notify({ status: 'success', title: 'Checking all models for updates.' });
      fetchSyncSettings().then(setSync).catch(() => undefined);
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Could not check for updates', description: err instanceof Error ? err.message : undefined });
    } finally {
      setChecking(false);
    }
  };

  const onboardButton = <Button onClick={() => setOnboarding(true)}><Plus />Onboard model</Button>;
  const waiting = availableAgainText(sync?.syncNowAvailableAt ?? null);

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">Drivers installed during deployment, per device model and Windows release.</p>
      <div className="flex flex-wrap items-center gap-2">
        <SearchInput value={search} onValueChange={setSearch} placeholder="Search models" aria-label="Search models" wrapperClassName="w-[280px]" />
        <Select
          value={vendor}
          onValueChange={setVendor}
          options={[{ value: ALL_VENDORS, label: 'All vendors' }, ...vendors.filter(v => v.supported && v.syncEnabled).map(v => ({ value: v.vendor, label: vendorLabel(v.vendor) }))]}
          aria-label="Vendor"
          wrapperClassName="w-40"
        />
        <div className="ml-auto flex items-center gap-2">
          {sync?.lastRunStartedAt && <span className="text-sm text-muted-foreground">{`Last checked ${formatTimestamp(sync.lastRunStartedAt)}`}</span>}
          {canManageDrivers && (
            <>
              <Tooltip content={`Checks every onboarded model for new component versions.${waiting ? ` ${waiting}.` : ''}`}>
                <span>
                  <Button variant="outline" onClick={() => void checkAll()} loading={checking} disabled={!models || models.length === 0 || waiting !== null}><RefreshCw />Check for updates</Button>
                </span>
              </Tooltip>
              {onboardButton}
            </>
          )}
        </div>
      </div>

      <ModelsTable
        models={models === null ? null : visible}
        total={models?.length ?? 0}
        loadError={loadError}
        onRetry={() => void load()}
        emptyAction={canManageDrivers ? onboardButton : undefined}
        onOpen={id => navigate(`/drivers/models/${id}`)}
      />

      {onboarding && <OnboardModelDialog vendors={vendors} onClose={() => setOnboarding(false)} />}
    </div>
  );
}

/**
 * Drivers page: one tab per area, the selected tab kept in `?tab=` so a link or a reload opens the
 * same tab.
 */
export default function DriversPage(): React.ReactElement {
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = parseDriversTab(searchParams.get('tab'));
  const [inProgress, setInProgress] = useState(0);

  // The count is extra information on the tab label; the page works without it. It follows
  // downloads that start or finish while the page is open.
  useEffect(() => {
    let cancelled = false;
    const read = () => fetchDownloads().then(d => { if (!cancelled) setInProgress(d.inProgress.length); }).catch(() => undefined);
    void read();
    const timer = window.setInterval(() => void read(), DOWNLOADS_POLL_MS);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, []);

  const select = (tab: DriversTab): void => {
    const next = new URLSearchParams(searchParams);
    next.set('tab', tab);
    setSearchParams(next, { replace: true });
  };

  return (
    <div className="space-y-6">
      <div className="border-b border-border">
        <nav className="flex flex-wrap gap-1" role="tablist" aria-label="Driver areas">
          {DRIVERS_TABS.map((tab) => (
            <button
              key={tab.key}
              id={`drivers-tab-${tab.key}`}
              type="button"
              role="tab"
              aria-selected={activeTab === tab.key}
              aria-controls={`drivers-panel-${tab.key}`}
              onClick={() => select(tab.key)}
              className={cn(
                '-mb-px inline-flex items-center gap-2 border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                activeTab === tab.key
                  ? 'border-primary text-foreground'
                  : 'border-transparent text-muted-foreground hover:text-foreground',
              )}
            >
              {tab.label}
              {tab.key === 'downloads' && inProgress > 0 && (
                <>
                  <CountPill count={inProgress} />
                  <span className="sr-only">in progress</span>
                </>
              )}
            </button>
          ))}
        </nav>
      </div>

      <div id={`drivers-panel-${activeTab}`} role="tabpanel" aria-labelledby={`drivers-tab-${activeTab}`}>
        {activeTab === 'models' && <ModelsTab />}
        {activeTab === 'rules' && <RulesTab />}
        {activeTab === 'sync' && <SyncTab />}
        {activeTab === 'downloads' && <DownloadsTab onCount={setInProgress} />}
        {activeTab === 'storage' && <StorageTab />}
      </div>
    </div>
  );
}
