import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { LocationPicker, type PickerLocation } from '../../../src/cloud-imaging-portal/client/src/components/LocationPicker.tsx';
import { TargetingBadges } from '../../../src/cloud-imaging-portal/client/src/components/TargetingBadges.tsx';

/**
 * The location picker and targeting badges are shared by Wi-Fi networks and setup profiles. These
 * pin the Wi-Fi behaviour so sharing them cannot change it, plus the disabled reason profiles add.
 */

const locations: PickerLocation[] = [
  { locationId: 'got', name: 'Gothenburg Office', effectiveRegion: 'EMEA', countryCode: 'SE' },
  { locationId: 'mal', name: 'Malmo Lab', effectiveRegion: 'EMEA', countryCode: 'SE' },
  { locationId: 'nyc', name: 'New York', effectiveRegion: 'AMER', countryCode: 'US' },
  { locationId: 'sto', name: 'Stockholm HQ', effectiveRegion: 'EMEA', countryCode: 'SE' },
];

describe('Portal frontend: location picker and targeting badges', () => {
  afterEach(cleanup);

  it('selects every shown location and reports the count', () => {
    const onChange = vi.fn();
    render(<LocationPicker locations={locations} selected={['sto']} onChange={onChange} />);

    expect(screen.getByText('1 of 4 selected')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    expect(onChange).toHaveBeenCalledWith(['sto', 'got', 'mal', 'nyc']);
  });

  it('narrows Select all and Clear to the search results', () => {
    const onChange = vi.fn();
    render(<LocationPicker locations={locations} selected={['sto', 'nyc']} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText('Search locations'), { target: { value: 'us' } });
    expect(screen.getByText('1 shown')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(onChange).toHaveBeenLastCalledWith(['sto']);
  });

  it('disables a location with a reason and leaves it out of Select all', () => {
    const onChange = vi.fn();
    render(<LocationPicker locations={locations} selected={[]} onChange={onChange} disabledReason={(id) => (id === 'got' ? 'Targeted by Gothenburg lab' : undefined)} />);

    const row = screen.getByRole('checkbox', { name: /Gothenburg Office/ });
    expect((row as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    expect(onChange).toHaveBeenCalledWith(['mal', 'nyc', 'sto']);
  });

  it('shows the all label', () => {
    render(<TargetingBadges all locationIds={[]} locations={locations} allLabel="All locations" editNoun="network" />);
    expect(screen.getByText('All locations')).toBeTruthy();
  });

  it('shows the first location and +N more with a summary of the rest in the tooltip', () => {
    const many: PickerLocation[] = Array.from({ length: 8 }, (_, i) => ({ locationId: `l${String(i)}`, name: `Site ${String(i)}` }));
    render(<TargetingBadges all={false} locationIds={many.map((l) => l.locationId)} locations={many} allLabel="All devices" editNoun="profile" />);

    expect(screen.getByText('Site 0')).toBeTruthy();
    const more = screen.getByText('+7 more');
    fireEvent.focus(more);
    expect(screen.getByRole('tooltip').textContent).toBe('Site 1, Site 2, Site 3 and 2 other locations and 2 more. Edit the profile to see and search all 8.');
  });
});
