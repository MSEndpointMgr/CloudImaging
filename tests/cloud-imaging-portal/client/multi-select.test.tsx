import React, { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MultiSelect, type MultiSelectOption } from '../../../src/cloud-imaging-portal/client/src/components/ui/multi-select.tsx';

const locations: MultiSelectOption[] = [
  { value: 'cph', label: 'Copenhagen' },
  { value: 'got', label: 'G\u00f6teborg' },
  { value: 'hel', label: 'Helsingfors' },
  { value: 'mal', label: 'Malm\u00f6' },
];

function Picker({ search = false, options = locations, onChange }: { search?: boolean; options?: MultiSelectOption[]; onChange?: (values: string[]) => void }): React.ReactElement {
  const [values, setValues] = useState<string[]>([]);
  return (
    <MultiSelect
      aria-label="Locations"
      values={values}
      onValuesChange={next => { setValues(next); onChange?.(next); }}
      options={options}
      placeholder="All locations"
      countLabel={n => `${n} locations`}
      searchPlaceholder={search ? 'Search locations' : undefined}
    />
  );
}

function trigger(): HTMLElement {
  return screen.getByRole('combobox', { name: 'Locations' });
}

describe('Portal frontend: multi-select', () => {
  afterEach(cleanup);

  it('names up to two choices on its button in list order, then counts them', () => {
    const label = (values: string[]): string => {
      cleanup();
      render(<MultiSelect aria-label="Locations" values={values} onValuesChange={() => undefined} options={locations} placeholder="All locations" countLabel={n => `${n} locations`} />);
      return trigger().textContent ?? '';
    };

    expect(label([])).toBe('All locations');
    expect(trigger().querySelector('span')?.className).toContain('text-muted-foreground');
    expect(label(['mal'])).toBe('Malm\u00f6');
    expect(label(['mal', 'got'])).toBe('G\u00f6teborg, Malm\u00f6');
    expect(label(['mal', 'got', 'cph'])).toBe('3 locations');
  });

  it('stays open while values are ticked, keeps them in list order, counts them and clears them', () => {
    const changes: string[][] = [];
    render(<Picker onChange={values => changes.push(values)} />);

    fireEvent.click(trigger());
    fireEvent.click(screen.getByRole('option', { name: 'Malm\u00f6' }));
    fireEvent.click(screen.getByRole('option', { name: 'G\u00f6teborg' }));

    expect(changes.at(-1)).toEqual(['got', 'mal']);
    expect(screen.getByRole('listbox').getAttribute('aria-multiselectable')).toBe('true');
    expect(screen.getByRole('option', { name: 'Malm\u00f6' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('option', { name: 'Copenhagen' }).getAttribute('aria-selected')).toBe('false');
    expect(screen.getByText('2 of 4 chosen')).toBeTruthy();
    expect(trigger().textContent).toBe('G\u00f6teborg, Malm\u00f6');

    fireEvent.click(screen.getByRole('option', { name: 'G\u00f6teborg' }));
    expect(changes.at(-1)).toEqual(['mal']);

    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(changes.at(-1)).toEqual([]);
    expect((screen.getByRole('button', { name: 'Clear' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole('listbox')).toBeTruthy();
  });

  it('searches from a row that takes focus, ticks with Enter and closes with Escape back on its button', () => {
    render(<Picker search />);

    fireEvent.click(trigger());
    const search = screen.getByRole('searchbox', { name: 'Search locations' });
    expect(document.activeElement).toBe(search);

    fireEvent.change(search, { target: { value: 'hel' } });
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Helsingfors']);
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(screen.getByRole('option', { name: 'Helsingfors' }).getAttribute('aria-selected')).toBe('true');

    fireEvent.change(search, { target: { value: 'zzz' } });
    expect(screen.getByText('No matches')).toBeTruthy();

    fireEvent.keyDown(search, { key: 'Escape' });
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(document.activeElement).toBe(trigger());
    expect(trigger().textContent).toBe('Helsingfors');
  });

  it('works from the keyboard without a search row: arrows move, Space ticks, Escape closes', () => {
    render(<Picker />);
    const button = trigger();

    fireEvent.keyDown(button, { key: 'ArrowDown' });
    expect(button.getAttribute('aria-activedescendant')).toMatch(/-0$/);
    fireEvent.keyDown(button, { key: 'ArrowDown' });
    fireEvent.keyDown(button, { key: ' ' });
    expect(screen.getByRole('option', { name: 'G\u00f6teborg' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.queryByRole('searchbox')).toBeNull();

    fireEvent.keyDown(button, { key: 'Escape' });
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(button.textContent).toBe('G\u00f6teborg');
  });

  it('draws a dot in the colour of each value\'s badge', () => {
    render(<Picker options={[{ value: 'SessionFailed', label: 'Failed', dotClassName: 'bg-red-600 dark:bg-red-400' }]} />);

    fireEvent.click(trigger());

    const dot = screen.getByRole('option', { name: 'Failed' }).querySelector('.rounded-full');
    expect(dot?.className).toContain('bg-red-600');
  });
});
