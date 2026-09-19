import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * Pages that need an account send anonymous visitors to sign in and bring them back afterwards.
 * The session is resolved before bootstrap, so this never has to wait for a request.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return (
    auth.currentUser() !== null ||
    router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } })
  );
};
