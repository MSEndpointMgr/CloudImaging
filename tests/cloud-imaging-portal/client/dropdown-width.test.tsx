import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Select } from '../../../src/cloud-imaging-portal/client/src/components/ui/select.tsx';
import { MultiSelect } from '../../../src/cloud-imaging-portal/client/src/components/ui/multi-select.tsx';
import { DateRangeSelect } from '../../../src/cloud-imaging-portal/client/src/components/ui/date-range-select.tsx';
import { resolvePreset } from '../../../src/cloud-imaging-portal/client/src/lib/dateRange.ts';

// jsdom lays nothing out, so the trigger's rect and the open panel's width are supplied here.
let triggerLeft = 100;
let panelWidth = 0;

beforeEach(() => {
  triggerLeft = 100;
  panelWidth = 0;
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    const isTrigger = this.getAttribute('role') === 'combobox';
    const rect = isTrigger ? { left: triggerLeft, top: 60, width: 176, height: 32 } : { left: 0, top: 0, width: 0, height: 0 };
    return { ...rect, x: rect.left, y: rect.top, right: rect.left + rect.width, bottom: rect.top + rect.height, toJSON: () => rect } as DOMRect;
  });
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.matches('[data-portal-surface], [role="dialog"]') ? panelWidth : 0;
  });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const options = [{ value: 'a', label: 'Imported' }, { value: 'b', label: 'Already registered' }];

describe('Portal frontend: dropdown width', () => {
  it('opens a list exactly as wide as its button, free to grow for a longer option', () => {
    render(<Select aria-label="Outcome" value="" onValueChange={() => undefined} options={options} />);

    fireEvent.click(screen.getByRole('combobox', { name: 'Outcome' }));

    const list = screen.getByRole('listbox');
    expect(list.style.minWidth).toBe('176px');
    expect(list.style.width).toBe('max-content');
    expect(list.style.maxWidth).toBe(`${window.innerWidth - 16}px`);
    expect(list.style.left).toBe('100px');
  });

  it('moves a list that grew past the window edge back inside it', () => {
    triggerLeft = 900;
    panelWidth = 260;
    render(<MultiSelect aria-label="Outcomes" values={[]} onValuesChange={() => undefined} options={options} placeholder="All outcomes" countLabel={n => `${n} outcomes`} />);

    fireEvent.click(screen.getByRole('combobox', { name: 'Outcomes' }));

    const panel = screen.getByRole('listbox').parentElement as HTMLElement;
    expect(panel.style.width).toBe('max-content');
    expect(panel.style.left).toBe(`${window.innerWidth - 260 - 8}px`);
  });

  it('keeps the width a searchable list opened with while its rows are filtered', () => {
    panelWidth = 240;
    render(
      <MultiSelect
        aria-label="Locations"
        values={[]}
        onValuesChange={() => undefined}
        options={[{ value: 'x', label: 'Stockholm Kista Science City' }, { value: 'y', label: 'Oslo' }]}
        placeholder="All locations"
        countLabel={n => `${n} locations`}
        searchPlaceholder="Search locations"
      />,
    );

    fireEvent.click(screen.getByRole('combobox', { name: 'Locations' }));
    const panel = screen.getByRole('listbox').parentElement as HTMLElement;
    expect(panel.style.minWidth).toBe('240px');

    panelWidth = 180;
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search locations' }), { target: { value: 'oslo' } });
    fireEvent(window, new Event('resize'));

    expect(panel.style.minWidth).toBe('240px');
  });

  it('opens the date range presets as wide as the button and widens only for a custom range', () => {
    render(<DateRangeSelect aria-label="Date range" value={resolvePreset('last30')!} onValueChange={() => undefined} />);

    fireEvent.click(screen.getByRole('combobox', { name: 'Date range' }));
    const panel = screen.getByRole('dialog', { name: 'Choose a date range' });
    expect(panel.style.minWidth).toBe('176px');
    expect(panel.style.width).toBe('max-content');

    fireEvent.click(screen.getByRole('option', { name: /Custom range/ }));

    expect(panel.style.minWidth).toBe('340px');
    expect(panel.style.width).toBe('340px');
  });
});
