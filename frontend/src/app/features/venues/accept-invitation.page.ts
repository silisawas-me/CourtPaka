import { Component, inject, input, OnInit, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { Venue, VenueService } from '../../core/venues/venue.service';

type AcceptState = 'working' | 'done' | 'failed';

@Component({
  selector: 'app-accept-invitation-page',
  imports: [RouterLink],
  templateUrl: './accept-invitation.page.html',
})
export class AcceptInvitationPage implements OnInit {
  private readonly venues = inject(VenueService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);
  protected readonly state = signal<AcceptState>('working');
  protected readonly pageError = signal('venues.accept.invalid');
  protected readonly venue = signal<Venue | null>(null);

  /** The guard sends anonymous visitors to sign in and back again, so a session exists here. */
  readonly invitationId = input<string>();
  readonly token = input<string>();

  ngOnInit(): void {
    const invitationId = this.invitationId();
    const token = this.token();

    if (!invitationId || !token) {
      this.state.set('failed');
      return;
    }

    this.venues.acceptInvitation(invitationId, token).subscribe({
      next: (venue) => {
        this.venue.set(venue);
        this.state.set('done');
        // The link is single use; keep the token out of the browser history.
        void this.router.navigate([], { replaceUrl: true, queryParams: {} });
        // The account now belongs to one more venue.
        this.auth.loadCurrentUser().subscribe({ error: () => undefined });
      },
      error: (error: unknown) => {
        this.pageError.set(errorKey(error));
        this.state.set('failed');
      },
    });
  }
}
