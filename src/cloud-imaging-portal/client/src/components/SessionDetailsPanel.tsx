import * as React from 'react';
import { CopyableId } from './ui/copyable-id.tsx';
import { RelativeTime } from './ui/relative-time.tsx';
import { formatDateTime } from '../lib/utils.ts';

export interface SessionHardware {
  motherboardManufacturer?: string | null;
  motherboardModel?: string | null;
  biosVersion?: string | null;
  /** Each entry is `"{adapterName}|{macAddress}"`, as collected by the Client in WinPE. */
  nicIdentifiers?: string[];
  /** Each entry is `"{diskCaption}|{sizeInBytes}"`. */
  storageLayout?: string[];
}

export interface SessionDetails {
  sessionId: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  macAddress?: string | null;
  hardware?: SessionHardware | null;
  locationName: string | null;
  preFlightAuthorizationResult?: string | null;
  createdAt: string;
  lastHeartbeatAt?: string | null;
  terminalAt?: string | null;
}

/**
 * Human-readable label for the pre-flight authorization outcome (FR-026). The API returns the raw
 * enum name; none of them are self-explanatory to a technician, and "Skipped" in particular reads
 * as a failure when it actually means the check is switched off.
 *
 * Keep in sync with `PreFlightAuthorizationResult` in CloudImaging.Contracts.
 */
const PRE_FLIGHT_LABELS: Record<string, string> = {
  Skipped: 'Not required (check disabled)',
  MatchedAutopilotV1: 'Authorized \u2014 matched Autopilot device',
  MatchedCorporateIdentifier: 'Authorized \u2014 matched corporate identifier',
  NotAuthorized: 'Not authorized',
};

function DetailField({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return (
    <div className="min-w-0 space-y-1">
      <dt className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="min-w-0 text-sm text-foreground">{children}</dd>
    </div>
  );
}

/** Renders an em dash for anything the API left null, so a gap never looks like a loading state. */
function Maybe({ value }: { value: string | null | undefined }): React.ReactElement {
  return value ? <>{value}</> : <span className="text-muted-foreground">—</span>;
}
/**
 * The Client packs these as `"{label}|{value}"` because the hardware inventory is a flat string
 * list. A missing separator is possible (WMI returned something unexpected), so fall back to
 * showing the raw entry rather than dropping it.
 */
function splitEntry(entry: string): { label: string; value: string | undefined } {
  const separator = entry.indexOf('|');
  if (separator === -1) return { label: entry, value: undefined };
  return { label: entry.slice(0, separator), value: entry.slice(separator + 1) };
}

function formatCapacity(raw: string | undefined): string | undefined {
  const bytes = Number(raw);
  if (!Number.isFinite(bytes) || bytes <= 0) return undefined;
  const terabytes = bytes / 1_099_511_627_776;
  return terabytes >= 1
    ? `${terabytes.toFixed(2)} TB`
    : `${(bytes / 1_073_741_824).toFixed(0)} GB`;
}

function InventoryList({
  label,
  entries,
  formatValue,
}: {
  label: string;
  entries: string[];
  formatValue?: (value: string | undefined) => string | undefined;
}): React.ReactElement {
  return (
    <div className="min-w-0 space-y-1">
      <dt className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd>
        <ul className="space-y-1">
          {entries.map(entry => {
            const { label: entryLabel, value } = splitEntry(entry);
            const formatted = formatValue ? formatValue(value) : value;
            return (
              <li key={entry} className="flex min-w-0 flex-wrap items-baseline gap-2 text-sm">
                <span className="min-w-0 break-words">{entryLabel}</span>
                {formatted && <span className="font-mono text-xs text-muted-foreground">{formatted}</span>}
              </li>
            );
          })}
        </ul>
      </dd>
    </div>
  );
}
/**
 * Expanded detail for a device row.
 *
 * Deliberately limited to fields the session summary already carries end-to-end. Notably absent is
 * the device's IP address: it is never captured at any layer — the Device Gateway does not read the
 * client address, and rate limiting is keyed on a token hash specifically so it does not have to.
 * Surfacing it would need capture at the gateway plus a retention decision.
 */
export function SessionDetailsPanel({ session }: { session: SessionDetails }): React.ReactElement {
  const preFlight = session.preFlightAuthorizationResult;
  const hardware = session.hardware;
  const motherboard = [hardware?.motherboardManufacturer, hardware?.motherboardModel]
    .filter(Boolean)
    .join(' ');
  const nicIdentifiers = hardware?.nicIdentifiers ?? [];
  const storageLayout = hardware?.storageLayout ?? [];
  const hasInventory = nicIdentifiers.length > 0 || storageLayout.length > 0;

  return (
    <div className="space-y-4 p-4">
      <dl className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
      <DetailField label="Session ID">
        <CopyableId
          value={session.sessionId}
          label="session ID"
          className="font-mono text-xs text-muted-foreground"
        />
      </DetailField>

      <DetailField label="Serial number">
        <CopyableId
          value={session.deviceSerialNumber}
          label="device serial number"
          className="font-mono text-sm"
        />
      </DetailField>

      <DetailField label="Manufacturer">
        <Maybe value={session.deviceManufacturer} />
      </DetailField>

      <DetailField label="Model">
        <Maybe value={session.deviceModel} />
      </DetailField>

      {/* The primary adapter's MAC. Operators use it to line a device up against DHCP/switch
          records — it is the closest identifier the platform has to a network address. */}
      <DetailField label="MAC address">
        {session.macAddress ? (
          <CopyableId value={session.macAddress} label="MAC address" className="font-mono text-sm" />
        ) : (
          <Maybe value={null} />
        )}
      </DetailField>

      <DetailField label="Motherboard">
        <Maybe value={motherboard || null} />
      </DetailField>

      <DetailField label="BIOS version">
        <Maybe value={hardware?.biosVersion} />
      </DetailField>

      <DetailField label="Location">
        <Maybe value={session.locationName} />
      </DetailField>

      <DetailField label="Pre-flight authorization">
        <Maybe value={preFlight ? PRE_FLIGHT_LABELS[preFlight] ?? preFlight : null} />
      </DetailField>

      {/* The row itself shows this as a relative time, which answers "is this waiting on me?".
          The absolute instant is what you need when correlating against a device-side log. */}
      <DetailField label="Session initialised">
        <span className="block">{formatDateTime(session.createdAt)}</span>
        <span className="block text-xs text-muted-foreground">
          <RelativeTime value={session.createdAt} />
        </span>
      </DetailField>

      <DetailField label="Last contact">
        {session.lastHeartbeatAt ? (
          <>
            <span className="block">{formatDateTime(session.lastHeartbeatAt)}</span>
            <span className="block text-xs text-muted-foreground">
              <RelativeTime value={session.lastHeartbeatAt} />
            </span>
          </>
        ) : (
          <span className="text-muted-foreground">Not yet polled</span>
        )}
      </DetailField>

      {session.terminalAt && (
        <DetailField label="Finished">
          <span className="block">{formatDateTime(session.terminalAt)}</span>
          <span className="block text-xs text-muted-foreground">
            <RelativeTime value={session.terminalAt} />
          </span>
        </DetailField>
      )}
      </dl>

      {/* Separated from the scalar fields because these are variable-length lists that need the
          full width. Hidden entirely when WMI returned nothing, rather than showing empty lists. */}
      {hasInventory && (
        <dl className="grid grid-cols-1 gap-4 border-t border-border pt-4 sm:grid-cols-2">
          {nicIdentifiers.length > 0 && (
            <InventoryList label="Network adapters" entries={nicIdentifiers} />
          )}
          {storageLayout.length > 0 && (
            <InventoryList label="Storage" entries={storageLayout} formatValue={formatCapacity} />
          )}
        </dl>
      )}
    </div>
  );
}
