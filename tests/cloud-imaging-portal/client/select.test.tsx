import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Select } from '../../../src/cloud-imaging-portal/client/src/components/ui/select.tsx';

const options = [
  { value: 'short', label: 'Windows 11' },
  { value: 'long', label: 'Windows 11 23H2 x64 en-US (10.0.22631.0)' },
];

describe('Portal frontend: select primitive', () => {
  afterEach(cleanup);

  it('reserves option-label width while showing the placeholder', () => {
    render(
      <Select
        value=""
        onValueChange={() => undefined}
        options={options}
        placeholder={'Select OS image\u2026'}
        aria-label="OS image to assign"
        sizeToOptions
      />,
    );

    const trigger = screen.getByRole('combobox', { name: 'OS image to assign' });
    const wrapper = trigger.parentElement;
    const sizer = wrapper?.querySelector('[data-select-width-sizer]');

    expect(trigger.textContent).toContain('Select OS image\u2026');
    expect(wrapper?.className).toContain('inline-grid');
    expect(sizer?.textContent).toContain('Select OS image\u2026');
    expect(sizer?.textContent).toContain(options[0].label);
    expect(sizer?.textContent).toContain(options[1].label);
    expect(sizer?.className).toContain('h-0');
  });

  it('reserves placeholder width when there are no options', () => {
    render(
      <Select
        value=""
        onValueChange={() => undefined}
        options={[]}
        placeholder="No OS images uploaded"
        aria-label="OS image to assign"
        sizeToOptions
      />,
    );

    const trigger = screen.getByRole('combobox', { name: 'OS image to assign' });
    const sizer = trigger.parentElement?.querySelector('[data-select-width-sizer]');

    expect(trigger.textContent).toContain('No OS images uploaded');
    expect(sizer?.textContent).toContain('No OS images uploaded');
  });

  it('reserves placeholder width when it is longer than every option', () => {
    render(
      <Select
        value=""
        onValueChange={() => undefined}
        options={[{ value: 'short', label: 'Short' }]}
        placeholder="Choose an unusually descriptive option"
        aria-label="Example picker"
        sizeToOptions
      />,
    );

    const trigger = screen.getByRole('combobox', { name: 'Example picker' });
    const sizer = trigger.parentElement?.querySelector('[data-select-width-sizer]');

    expect(sizer?.textContent).toContain('Choose an unusually descriptive option');
    expect(sizer?.textContent).toContain('Short');
  });

  it('keeps the same option-derived sizing layer after selection', () => {
    render(
      <Select
        value="short"
        onValueChange={() => undefined}
        options={options}
        aria-label="OS image to assign"
        sizeToOptions
      />,
    );

    const trigger = screen.getByRole('combobox', { name: 'OS image to assign' });
    const sizer = trigger.parentElement?.querySelector('[data-select-width-sizer]');

    expect(trigger.textContent).toContain('Windows 11');
    expect(sizer?.textContent).toContain(options[1].label);
  });

  it('does not reserve a leading check-icon column for unselected options', () => {
    render(
      <Select
        value="short"
        onValueChange={() => undefined}
        options={options}
        aria-label="OS image to assign"
      />,
    );

    fireEvent.click(screen.getByRole('combobox', { name: 'OS image to assign' }));

    const renderedOptions = screen.getAllByRole('option');
    expect(renderedOptions[0].querySelector('[data-select-check]')).not.toBeNull();
    expect(renderedOptions[1].querySelector('[data-select-check]')).toBeNull();
    expect(renderedOptions[1].className).not.toContain('gap-2');
  });

  it('draws group headings that arrow keys and type-ahead skip, and meta text that type-ahead does not match', () => {
    const chosen: string[] = [];
    const grouped = [
      { value: 'a', label: 'HP EliteBook 840 G11', group: 'HP', meta: '2.41 GB' },
      { value: 'b', label: 'HP ProBook 450 G10', group: 'HP', meta: '1.1 GB' },
      { value: 'c', label: 'Latitude 5450', group: 'Dell', meta: '900 MB' },
      { value: 'none', label: 'No drivers' },
    ];
    render(<Select value="" onValueChange={v => chosen.push(v)} options={grouped} aria-label="Driver set for 5CG1234" />);
    const trigger = screen.getByRole('combobox', { name: 'Driver set for 5CG1234' });

    fireEvent.click(trigger);

    const groups = screen.getAllByRole('group');
    expect(groups.map(g => g.getAttribute('aria-label'))).toEqual(['HP', 'Dell']);
    expect(screen.getAllByRole('option').map(o => o.getAttribute('title'))).toEqual(['HP EliteBook 840 G11', 'HP ProBook 450 G10', 'Latitude 5450', 'No drivers']);
    expect(screen.getByText('2.41 GB')).toBeTruthy();
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(trigger.getAttribute('aria-activedescendant')).toMatch(/-2$/);
    fireEvent.keyDown(trigger, { key: 'Escape' });

    // Each key starts a new type-ahead search.
    let now = 0;
    const clock = vi.spyOn(Date, 'now').mockImplementation(() => (now += 1000));
    fireEvent.keyDown(trigger, { key: '9' });
    expect(chosen).toEqual([]);
    fireEvent.keyDown(trigger, { key: 'D' });
    expect(chosen).toEqual([]);
    fireEvent.keyDown(trigger, { key: 'L' });
    expect(chosen).toEqual(['c']);
    clock.mockRestore();
  });
});