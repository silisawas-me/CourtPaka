import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Pages that need an account send anonymous visitors to sign in and bring them back afterwards.
 * The app boots without waiting for the session, so a guarded page waits for it here — once, on
 * the first navigation; after that the answer is already in hand.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth
    .whenReady()
    .pipe(
      map(
        (user) =>
          user !== null ||
          router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } }),
      ),
    );
};
