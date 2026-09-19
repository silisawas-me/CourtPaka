import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

/** Errors from the API carry a stable code (PRD US-23); the UI turns the code into text. */
export class ApiError extends Error {
  constructor(
    readonly code: string,
    readonly status: number,
  ) {
    super(code);
  }
}

export const UNKNOWN_ERROR_CODE = 'unknown';
export const TOO_MANY_REQUESTS_CODE = 'tooManyRequests';

/** Translation key for any failure, so no screen has to invent its own fallback. */
export function errorKey(error: unknown): string {
  return `error.${error instanceof ApiError ? error.code : UNKNOWN_ERROR_CODE}`;
}

/** Every API failure reaches feature code as an ApiError, whatever the endpoint. */
export const apiErrorInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(
    catchError((error: HttpErrorResponse) => {
      const code =
        error.status === 429
          ? TOO_MANY_REQUESTS_CODE
          : ((error.error as { code?: string } | null)?.code ?? UNKNOWN_ERROR_CODE);

      return throwError(() => new ApiError(code, error.status));
    }),
  );
