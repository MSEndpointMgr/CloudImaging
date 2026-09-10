import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { SessionDetailsPanel, type SessionDetails } from '../../../src/cloud-imaging-portal/client/src/components/SessionDetailsPanel.tsx';

/**
 * The Available Devices rows expand to this panel. Its whole purpose is to answer questions the
 * six-column row cannot, so the fields that are easy to regress are the nullable ones: a device
 * that has registered but not yet polled has no `lastHeartbeatAt`, and a non-terminal session has
 * no `terminalAt`. Neither may render as a blank gap that reads as "still loading".
 */
describe('Portal frontend: session details panel', () => {
  afterEach(cleanup);

  const base: SessionDetails = {
    sessionId: '4f1d2c3b-0000-4a5b-9c8d-7e6f5a4b3c2d',
    deviceSerialNumber: 'JHK4L92',
    deviceManufacturer: 'Dell Inc.',
    deviceModel: 'Latitude 5450',
    locationName: 'Copenhagen HQ',
    preFlightAuthorizationResult: 'MatchedAutopilotV1',
    createdAt: '2026-09-09T23:45:55.000Z',
    lastHeartbeatAt: null,
    terminalAt: null,
  };

  it('shows the identifiers and device details', () => {
    render(<SessionDetailsPanel session={base} />);

    expect(screen.getByText('Session ID')).toBeTruthy();
    expect(screen.getByText('JHK4L92')).toBeTruthy();
    expect(screen.getByText('Dell Inc.')).toBeTruthy();
    expect(screen.getByText('Latitude 5450')).toBeTruthy();
    expect(screen.getByText('Copenhagen HQ')).toBeTruthy();
  });

  it('translates the pre-flight enum instead of leaking "Skipped"', () => {
    render(<SessionDetailsPanel session={{ ...base, preFlightAuthorizationResult: 'Skipped' }} />);
    expect(screen.getByText('Not required (check disabled)')).toBeTruthy();
  });

  it('falls back to the raw value for an unrecognised pre-flight result', () => {
    render(<SessionDetailsPanel session={{ ...base, preFlightAuthorizationResult: 'SomethingNew' }} />);
    expect(screen.getByText('SomethingNew')).toBeTruthy();
  });

  it('says a device has not polled yet rather than leaving the field blank', () => {
    render(<SessionDetailsPanel session={base} />);
    expect(screen.getByText('Not yet polled')).toBeTruthy();
  });

  it('renders the last-contact time once the device has polled', () => {
    render(<SessionDetailsPanel session={{ ...base, lastHeartbeatAt: '2026-09-10T08:00:00.000Z' }} />);
    expect(screen.getByText('Last contact')).toBeTruthy();
    expect(screen.queryByText('Not yet polled')).toBeNull();
  });

  it('omits the finished field entirely while the session is still running', () => {
    render(<SessionDetailsPanel session={base} />);
    expect(screen.queryByText('Finished')).toBeNull();
  });

  it('shows the finished field once the session reaches a terminal state', () => {
    render(<SessionDetailsPanel session={{ ...base, terminalAt: '2026-09-10T09:15:00.000Z' }} />);
    expect(screen.getByText('Finished')).toBeTruthy();
  });

  it('renders an em dash for a missing location instead of an empty cell', () => {
    render(<SessionDetailsPanel session={{ ...base, locationName: null }} />);
    expect(screen.getAllByText('\u2014').length).toBeGreaterThan(0);
  });

  it('shows the MAC address, which is the closest thing to a network address we hold', () => {
    render(<SessionDetailsPanel session={{ ...base, macAddress: 'A4B1C2D3E4F5' }} />);
    expect(screen.getByText('MAC address')).toBeTruthy();
    expect(screen.getByText('A4B1C2D3E4F5')).toBeTruthy();
  });

  it('joins motherboard manufacturer and model into one field', () => {
    render(
      <SessionDetailsPanel
        session={{
          ...base,
          hardware: { motherboardManufacturer: 'Dell Inc.', motherboardModel: '0ABCD1' },
        }}
      />,
    );
    expect(screen.getByText('Dell Inc. 0ABCD1')).toBeTruthy();
  });

  it('hides the inventory section entirely when WMI returned nothing', () => {
    render(<SessionDetailsPanel session={{ ...base, hardware: { biosVersion: '1.14.0' } }} />);
    expect(screen.queryByText('Network adapters')).toBeNull();
    expect(screen.queryByText('Storage')).toBeNull();
    expect(screen.getByText('1.14.0')).toBeTruthy();
  });

  it('splits the "name|value" packing the Client uses for adapters', () => {
    render(
      <SessionDetailsPanel
        session={{ ...base, hardware: { nicIdentifiers: ['Ethernet|A4B1C2D3E4F5'] } }}
      />,
    );
    expect(screen.getByText('Ethernet')).toBeTruthy();
    expect(screen.getByText('A4B1C2D3E4F5')).toBeTruthy();
  });

  it('formats raw disk byte counts as human-readable capacity', () => {
    render(
      <SessionDetailsPanel
        session={{
          ...base,
          hardware: { storageLayout: ['NVMe KBG50ZNV512G|512110190592', 'HDD BIG|2000398934016'] },
        }}
      />,
    );
    expect(screen.getByText('477 GB')).toBeTruthy();
    expect(screen.getByText('1.82 TB')).toBeTruthy();
  });

  it('still lists an inventory entry that has no separator', () => {
    render(<SessionDetailsPanel session={{ ...base, hardware: { storageLayout: ['UnknownDisk'] } }} />);
    expect(screen.getByText('UnknownDisk')).toBeTruthy();
  });

  it('does not print a capacity when the byte count is unparseable', () => {
    render(<SessionDetailsPanel session={{ ...base, hardware: { storageLayout: ['Disk|0'] } }} />);
    expect(screen.getByText('Disk')).toBeTruthy();
    expect(screen.queryByText('0 GB')).toBeNull();
  });
});
