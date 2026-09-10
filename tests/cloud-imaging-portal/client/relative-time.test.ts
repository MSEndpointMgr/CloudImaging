import { describe, it, expect } from 'vitest';
import { formatRelativeTime } from '../../../src/cloud-imaging-portal/client/src/lib/utils.ts';

/**
 * Session and image tables label recency ("registered", "uploaded", "created") with relative
 * timestamps, because the operator's actual question is "is this device waiting on me right
 * now?" rather than "what wall-clock instant was the row written at". Absolute timestamps are
 * deliberately kept on the report pages, where the precise instant is the point.
 *
 * The boundaries are asserted from a fixed `now` rather than the real clock so the suite cannot
 * fail intermittently when it happens to run across a minute or day rollover.
 */
describe('Portal frontend: relative timestamps', () => {
  const now = Date.parse('2025-03-14T12:00:00.000Z');
  const ago = (ms: number): string => new Date(now - ms).toISOString();

  const SECOND = 1000;
  const MINUTE = 60 * SECOND;
  const HOUR = 60 * MINUTE;
  const DAY = 24 * HOUR;

  it('reports anything under a minute as "just now"', () => {
    expect(formatRelativeTime(ago(0), now)).toBe('just now');
    expect(formatRelativeTime(ago(59 * SECOND), now)).toBe('just now');
  });

  it('reports minutes, singularising the first one', () => {
    expect(formatRelativeTime(ago(MINUTE), now)).toBe('1 min ago');
    expect(formatRelativeTime(ago(59 * MINUTE), now)).toBe('59 min ago');
  });

  it('reports hours once past the hour boundary', () => {
    expect(formatRelativeTime(ago(HOUR), now)).toBe('1 hr ago');
    expect(formatRelativeTime(ago(23 * HOUR), now)).toBe('23 hr ago');
  });

  it('names the previous day rather than calling it "1 day ago"', () => {
    expect(formatRelativeTime(ago(DAY), now)).toBe('yesterday');
    expect(formatRelativeTime(ago(2 * DAY), now)).toBe('2 days ago');
    expect(formatRelativeTime(ago(6 * DAY), now)).toBe('6 days ago');
  });

  it('falls back to an absolute date once "N days ago" stops being meaningful', () => {
    const old = formatRelativeTime(ago(30 * DAY), now);
    expect(old).not.toMatch(/ago|just now|yesterday/);
    expect(Date.parse(old)).not.toBeNaN();
  });

  /**
   * Devices image themselves in WinPE, where the clock is frequently wrong until the first NTP
   * sync, so a session can carry a createdAt slightly in the future. That must degrade to "just
   * now" instead of rendering a negative age.
   */
  it('treats future timestamps from a skewed device clock as "just now"', () => {
    expect(formatRelativeTime(new Date(now + 5 * MINUTE).toISOString(), now)).toBe('just now');
  });

  it('renders an em dash for an unparseable value instead of "Invalid Date"', () => {
    expect(formatRelativeTime('not-a-timestamp', now)).toBe('\u2014');
    expect(formatRelativeTime('', now)).toBe('\u2014');
  });
});
