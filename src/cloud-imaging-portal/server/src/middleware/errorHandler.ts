import type { NextFunction, Request, Response } from 'express';
import axios from 'axios';

/** Longest upstream message relayed to the browser. Operator API messages are one sentence. */
const MAX_FORWARDED_DETAIL_CHARS = 500;

/** Upstream field problems relayed to the browser, and the longest message kept for each. */
const MAX_FORWARDED_ERRORS = 50;
const MAX_FORWARDED_ERROR_MESSAGE_CHARS = 300;

/**
 * Keeps only `code`, `line` and `message` of each upstream problem (for example the answer file
 * rules), so nothing else from the upstream body reaches the browser.
 */
function forwardedErrors(value: unknown): { code: string; line: number | null; message: string }[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const errors = value
    .filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === 'object')
    .filter(entry => typeof entry['code'] === 'string' && typeof entry['message'] === 'string')
    .slice(0, MAX_FORWARDED_ERRORS)
    .map(entry => ({
      code: (entry['code'] as string).slice(0, 100),
      line: typeof entry['line'] === 'number' && Number.isInteger(entry['line']) ? entry['line'] : null,
      message: (entry['message'] as string).slice(0, MAX_FORWARDED_ERROR_MESSAGE_CHARS),
    }));
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

    // Only client errors carry an operator-actionable, deliberately worded message
    // ("checksum mismatch", "this upload has expired", "image is in use"). An upstream 5xx
    // is an unhandled backend fault whose body can contain stack traces, connection strings
    // in exception text, or internal host names, so its text is logged above and replaced
    // here rather than relayed to the browser.
    if (status >= 500) {
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
    const upstream = data && typeof data === 'object' ? (data as Record<string, unknown>) : undefined;
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
