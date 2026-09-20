import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

/** Errors from the API carry a stable code (PRD US-23); the UI turns the code into text. */
export class ApiError extends Error {
  constructor(
    readonly code: string,
    readonly status: number,
    /**
     * The rest of the problem body. Most refusals say everything in the code, but a few hand
     * back something the screen has to act on rather than only report — the bookings standing in
     * the way of a court closing (PRD US-11). Never text to show: the code still decides that.
     */
    readonly details: Record<string, unknown> | null = null,
  ) {
    super(code);
  }
}

export const UNKNOWN_ERROR_CODE = 'unknown';
export const TOO_MANY_REQUESTS_CODE = 'tooManyRequests';
/** Credentials were accepted but the browser ended up without a usable session. */
export const SESSION_NOT_ESTABLISHED_CODE = 'sessionNotEstablished';

/** Translation key for any failure, so no screen has to invent its own fallback. */
export function errorKey(error: unknown): string {
  return `error.${error instanceof ApiError ? error.code : UNKNOWN_ERROR_CODE}`;
}

/** Every API failure reaches feature code as an ApiError, whatever the endpoint. */
export const apiErrorInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(
    catchError((error: HttpErrorResponse) => {
      const body = error.error as { code?: string } | null;
      const code =
        error.status === 429 ? TOO_MANY_REQUESTS_CODE : (body?.code ?? UNKNOWN_ERROR_CODE);

      return throwError(
        () =>
          new ApiError(
            code,
            error.status,
            body && typeof body === 'object' ? (body as Record<string, unknown>) : null,
          ),
      );
    }),
  );
