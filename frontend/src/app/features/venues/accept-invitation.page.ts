import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { Venue, VenueService } from '../../core/venues/venue.service';

type AcceptState = 'working' | 'done' | 'failed' | 'signInRequired';

@Component({
  selector: 'app-accept-invitation-page',
  imports: [RouterLink],
  templateUrl: './accept-invitation.page.html',
})
export class AcceptInvitationPage implements OnInit {
  private readonly venues = inject(VenueService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);
  protected readonly state = signal<AcceptState>('working');
  protected readonly errorKey = signal('venues.accept.invalid');
  protected readonly venue = signal<Venue | null>(null);

  ngOnInit(): void {
    const parameters = this.route.snapshot.queryParamMap;
    const invitationId = parameters.get('invitationId');
    const token = parameters.get('token');

    if (!invitationId || !token) {
      this.state.set('failed');
      return;
    }

    // The invitation belongs to an address, so the API needs to know who is accepting.
    if (!this.auth.currentUser()) {
      this.state.set('signInRequired');
      return;
    }

    this.venues.acceptInvitation(invitationId, token).subscribe({
      next: (venue) => {
        this.venue.set(venue);
        this.state.set('done');
        // The link is single use; keep the token out of the browser history.
        void this.router.navigate([], { replaceUrl: true, queryParams: {} });
      },
      error: (error: unknown) => {
        this.errorKey.set(errorKey(error));
        this.state.set('failed');
      },
    });
  }
}
