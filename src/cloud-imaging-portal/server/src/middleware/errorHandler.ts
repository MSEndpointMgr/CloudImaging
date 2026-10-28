import type { NextFunction, Request, Response } from 'express';
import axios from 'axios';

/** Longest upstream message relayed to the browser. Operator API messages are one sentence. */
const MAX_FORWARDED_DETAIL_CHARS = 500;

/** Upstream field problems relayed to the browser, and the longest message kept for each. */
const MAX_FORWARDED_ERRORS = 50;
const MAX_FORWARDED_ERROR_MESSAGE_CHARS = 300;

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const FIELD_PATTERN = /^[A-Za-z][A-Za-z0-9.]{0,49}$/;

type ForwardedError =
  | { code: string; line: number | null; message: string }
  | { sessionId: string; reason: string }
  | { field: string; detail: string };

/**
 * Keeps only `code`, `line` and `message` of each upstream problem (for example the answer file
 * rules), `sessionId` and `reason` (a driver choice that can no longer be used), or `field` and
 * `detail` (a form field Core refused), so nothing else from the upstream body reaches the browser.
 */
function forwardedErrors(value: unknown): ForwardedError[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const errors = value
    .filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === 'object')
    .flatMap((entry): ForwardedError[] => {
      if (typeof entry['code'] === 'string' && typeof entry['message'] === 'string') {
        return [{
          code: entry['code'].slice(0, 100),
          line: typeof entry['line'] === 'number' && Number.isInteger(entry['line']) ? entry['line'] : null,
          message: entry['message'].slice(0, MAX_FORWARDED_ERROR_MESSAGE_CHARS),
        }];
      }
      if (typeof entry['sessionId'] === 'string' && GUID_PATTERN.test(entry['sessionId']) && typeof entry['reason'] === 'string') {
        return [{ sessionId: entry['sessionId'], reason: entry['reason'].slice(0, MAX_FORWARDED_ERROR_MESSAGE_CHARS) }];
      }
      if (typeof entry['field'] === 'string' && FIELD_PATTERN.test(entry['field']) && typeof entry['detail'] === 'string') {
        return [{ field: entry['field'], detail: entry['detail'].slice(0, MAX_FORWARDED_ERROR_MESSAGE_CHARS) }];
      }
      return [];
    })
    .slice(0, MAX_FORWARDED_ERRORS);
  return errors.length > 0 ? errors : undefined;
}

function truncateDetail(detail: string | undefined): string | undefined {
  if (!detail) return undefined;
  const trimmed = detail.trim();
  if (!trimmed) return undefined;
  return trimmed.length > MAX_FORWARDED_DETAIL_CHARS
    ? `${trimmed.slice(0, MAX_FORWARDED_DETAIL_CHARS)}…`
    : trimmed;
}

/**
 * Upstream 5xx problems whose detail was written for the user (a vendor catalog Core cannot
 * reach), identified by their problem type. Every other 5xx text is replaced.
 */
const DELIBERATE_SERVER_PROBLEM_TYPES = new Set(['https://cloudimaging.io/errors/drivers']);

/** `availableAt` (when a limited action is available again) and `catalogUrl`, when well formed. */
function forwardedExtras(upstream: Record<string, unknown> | undefined): Record<string, string> {
  const extras: Record<string, string> = {};
  const availableAt = upstream?.['availableAt'];
  if (typeof availableAt === 'string' && availableAt.length <= 40 && !Number.isNaN(Date.parse(availableAt))) {
    extras['availableAt'] = availableAt;
  }
  const catalogUrl = upstream?.['catalogUrl'];
  if (typeof catalogUrl === 'string' && catalogUrl.length <= 300 && catalogUrl.startsWith('https://')) {
    extras['catalogUrl'] = catalogUrl;
  }
  return extras;
}

/** Relays a numeric Retry-After from the upstream response. */
function forwardRetryAfter(res: Response, headers: unknown): void {
  const value = headers && typeof headers === 'object' ? (headers as Record<string, unknown>)['retry-after'] : undefined;
  if (typeof value === 'string' && /^\d{1,6}$/.test(value)) {
    res.set('Retry-After', value);
  }
}

/** Global error handler: turns Operator API failures into problem responses for the browser. */
export function errorHandler(err: unknown, _req: Request, res: Response, _next: NextFunction): void {
  // An AxiosError carries its request config: the Operator API bearer token and the request body,
  // which can hold a Wi-Fi password. Log only what identifies the failure.
  console.error('Unhandled error', axios.isAxiosError(err)
    ? { message: err.message, code: err.code, method: err.config?.method, url: err.config?.url, status: err.response?.status, response: err.response?.data as unknown }
    : err);

  // Translate downstream Operator API failures into meaningful responses so the
  // portal can distinguish "backend unreachable" from an authorization/validation
  // error instead of always surfacing a generic 500.
  if (axios.isAxiosError(err)) {
    // No response received. The Operator API is unreachable (down, wrong URL,
    // connection refused, DNS failure, or timed out).
    if (!err.response) {
      const unreachable = err.code === 'ECONNABORTED';
      res.status(504).json({
        type: 'https://cloudimaging.io/errors/backend-unavailable',
        title: 'Backend API unavailable.',
        status: 504,
        detail: unreachable
          ? 'The Operator API did not respond in time. Confirm it is running and reachable.'
          : `The Operator API could not be reached (${err.code ?? 'connection error'}). Confirm it is running and reachable.`,
      });
      return;
    }

    // Upstream responded with an error status. Forward it (and its problem
    // details when present) so the client sees the real cause (e.g. 403, 400).
    const status = err.response.status;
    const data: unknown = err.response.data;
    const upstream = data && typeof data === 'object' ? (data as Record<string, unknown>) : undefined;
    forwardRetryAfter(res, err.response.headers);

    // Only client errors carry an operator-actionable, deliberately worded message
    // ("checksum mismatch", "this upload has expired", "image is in use"). An upstream 5xx
    // is an unhandled backend fault whose body can contain stack traces, connection strings
    // in exception text, or internal host names, so its text is logged above and replaced
    // here rather than relayed to the browser, unless its problem type marks it as written
    // for the user.
    const deliberate = typeof upstream?.['type'] === 'string' && DELIBERATE_SERVER_PROBLEM_TYPES.has(upstream['type']);
    if (status >= 500 && !deliberate) {
      res.status(status).json({
        type: 'https://cloudimaging.io/errors/upstream-error',
        title: 'The backend API returned an error.',
        status,
        detail: `The Operator API responded with status ${String(status)}. ` +
          'Check Application Insights for correlation details.',
      });
      return;
    }

    // OperatorApi's proxy forwards its own plain-text error bodies (e.g. checksum
    // validation, stale/expired staged-upload blocks) as-is rather than wrapping them in
    // ProblemDetails JSON, so `data` here is sometimes a raw string rather than an object.
    const plainTextDetail = typeof data === 'string' && data.trim().length > 0 ? data : undefined;
    const forwarded = (upstream?.['detail'] as string | undefined) ?? plainTextDetail;
    const errors = forwardedErrors(upstream?.['errors']);
    res.status(status).json({
      type: (upstream?.['type'] as string | undefined) ?? 'https://cloudimaging.io/errors/upstream-error',
      title: (upstream?.['title'] as string | undefined) ?? 'The backend API returned an error.',
      status,
      // Cap the relayed text: an unexpected HTML error page from an edge component in front
      // of the Operator API would otherwise be echoed into the portal UI in full.
      detail: truncateDetail(forwarded) ?? `The Operator API responded with status ${String(status)}.`,
      ...(errors ? { errors } : {}),
      ...forwardedExtras(upstream),
    });
    return;
  }

  // body-parser refusals (body too large, malformed JSON) are the caller's fault, not a server error.
  const parserError = err as { type?: unknown; status?: unknown } | null;
  if (parserError && typeof parserError === 'object' && (parserError.type === 'entity.too.large' || parserError.type === 'entity.parse.failed')) {
    const tooLarge = parserError.type === 'entity.too.large';
    res.status(tooLarge ? 413 : 400).json({
      type: 'https://cloudimaging.io/errors/invalid-request',
      title: 'Invalid request.',
      status: tooLarge ? 413 : 400,
      detail: tooLarge ? 'The request is too large.' : 'The request body is not valid JSON.',
    });
    return;
  }

  res.status(500).json({
    type: 'https://cloudimaging.io/errors/internal-error',
    title: 'An unexpected error occurred.',
    status: 500,
    detail: 'An internal error occurred. Check Application Insights for correlation details.',
  });
}
