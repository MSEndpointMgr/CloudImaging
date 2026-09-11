import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
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
    expect(sizer?.textContent).toContain(options[0].label);
    expect(sizer?.textContent).toContain(options[1].label);
    expect(sizer?.className).toContain('h-0');
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
});