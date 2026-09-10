import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor } from '@testing-library/react';
import { Tooltip } from '../../../src/cloud-imaging-portal/client/src/components/ui/tooltip.tsx';

/**
 * The tooltip replaced the native `title` attribute on icon-only controls. `title` never appears
 * on keyboard focus, so the behaviour that actually matters for accessibility is that focusing
 * the trigger reveals the text and wires it to the *button* via `aria-describedby` — putting that
 * attribute on the wrapper span instead would leave it unannounced, because assistive tech reads
 * the description of the focused element.
 */
describe('Portal frontend: tooltip primitive', () => {
  afterEach(cleanup);

  const renderTooltip = (): HTMLElement => {
    render(
      <Tooltip content="Delete this location">
        <button type="button" aria-label="Delete location HQ">x</button>
      </Tooltip>,
    );
    return screen.getByRole('button', { name: 'Delete location HQ' });
  };

  it('stays closed until the trigger is hovered or focused', () => {
    renderTooltip();
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('opens immediately on keyboard focus', async () => {
    const trigger = renderTooltip();
    fireEvent.focus(trigger);

    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip.textContent).toBe('Delete this location');
  });

  it('describes the trigger itself, not the wrapper', async () => {
    const trigger = renderTooltip();
    expect(trigger.getAttribute('aria-describedby')).toBeNull();

    fireEvent.focus(trigger);
    const tooltip = await screen.findByRole('tooltip');

    expect(trigger.getAttribute('aria-describedby')).toBe(tooltip.id);
  });

  it('closes again on blur', async () => {
    const trigger = renderTooltip();
    fireEvent.focus(trigger);
    await screen.findByRole('tooltip');

    fireEvent.blur(trigger);
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
    expect(trigger.getAttribute('aria-describedby')).toBeNull();
  });

  it('closes on Escape', async () => {
    const trigger = renderTooltip();
    fireEvent.focus(trigger);
    await screen.findByRole('tooltip');

    fireEvent.keyDown(document, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
  });

  it('closes on scroll, because the bubble is fixed-positioned and its coordinates go stale', async () => {
    const trigger = renderTooltip();
    fireEvent.focus(trigger);
    await screen.findByRole('tooltip');

    fireEvent.scroll(window);
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
  });

  it('ignores touch pointers, which fire enter without a matching leave', async () => {
    const trigger = renderTooltip();

    fireEvent.pointerEnter(trigger, { pointerType: 'touch' });
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
  });
});
