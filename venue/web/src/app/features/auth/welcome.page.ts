import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { VenueService } from '../../core/venues/venue.service';

/**
 * The first sign-in of somebody an owner added (thai-fit T1, artboard "พนักงานเข้าระบบ" 2): the
 * owner made the account, but only its person can accept the privacy policy (PDPA), so nothing
 * else opens until they do. The guard sends them here and back.
 */
@Component({
  selector: 'app-welcome-page',
  imports: [MatButtonModule, MatCardModule, MatCheckboxModule],
  templateUrl: './welcome.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WelcomePage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly i18n = inject(TranslationService);

  /** Where they were going when the guard stopped them. */
  readonly returnUrl = input<string>();

  protected readonly user = this.auth.currentUser;
  protected readonly venue = toSignal(
    inject(VenueService)
      .mine()
      .pipe(
        map((venues) => venues[0]?.name ?? ''),
        catchError(() => of('')),
      ),
    { initialValue: '' },
  );
  protected readonly accepted = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected start(): void {
    if (!this.accepted() || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.auth.consent().subscribe({
      next: () => {
        this.busy.set(false);
        const back = this.returnUrl();
        void this.router.navigateByUrl(
          back?.startsWith('/') && !back.startsWith('//') && !back.startsWith('/welcome')
            ? back
            : '/',
        );
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
