import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  Venue,
  VenueInvitation,
  VenueMember,
  VenuePermission,
  VENUE_PERMISSIONS,
  VenueService,
} from '../../core/venues/venue.service';
import { FieldError } from '../../shared/field-error';

@Component({
  selector: 'app-venue-detail-page',
  imports: [ReactiveFormsModule, RouterLink, FieldError],
  templateUrl: './venue-detail.page.html',
})
export class VenueDetailPage implements OnInit {
  private readonly venues = inject(VenueService);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);

  protected readonly i18n = inject(TranslationService);
  protected readonly permissions = VENUE_PERMISSIONS;

  protected readonly venueId = this.route.snapshot.paramMap.get('venueId')!;
  protected readonly venue = signal<Venue | null>(null);
  protected readonly members = signal<VenueMember[]>([]);
  protected readonly invitations = signal<VenueInvitation[]>([]);
  protected readonly loading = signal(true);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly inviteErrorKey = signal<string | null>(null);
  protected readonly inviting = signal(false);

  /** Only an owner sees member management; staff see the roster read-only. */
  protected readonly isOwner = computed(() => {
    const email = this.auth.currentUser()?.email?.toLowerCase();
    return this.members().some(
      (member) => member.role === 'Owner' && member.email.toLowerCase() === email,
    );
  });

  protected readonly inviteForm = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    VerifySlip: [true],
    ManageBookings: [true],
    CloseCourt: [true],
    ViewReports: [false],
    ManageSettings: [false],
  });

  ngOnInit(): void {
    this.reload();
  }

  protected invite(): void {
    this.inviteForm.markAllAsTouched();
    if (this.inviteForm.controls.email.invalid || this.inviting()) {
      return;
    }

    this.inviting.set(true);
    this.inviteErrorKey.set(null);
    const values = this.inviteForm.getRawValue();

    this.venues.invite(this.venueId, values.email, this.selectedPermissions()).subscribe({
      next: (invitation) => {
        this.inviting.set(false);
        this.invitations.set([
          ...this.invitations().filter((i) => i.email !== invitation.email),
          invitation,
        ]);
        this.inviteForm.controls.email.reset('');
      },
      error: (error: unknown) => {
        this.inviting.set(false);
        this.inviteErrorKey.set(errorKey(error));
      },
    });
  }

  protected togglePermission(
    member: VenueMember,
    permission: VenuePermission,
    granted: boolean,
  ): void {
    const next = granted
      ? [...member.permissions, permission]
      : member.permissions.filter((held) => held !== permission);

    this.venues.changePermissions(this.venueId, member.userId, next).subscribe({
      next: () => this.reload(),
      error: (error: unknown) => this.errorKey.set(errorKey(error)),
    });
  }

  protected remove(member: VenueMember): void {
    this.venues.removeMember(this.venueId, member.userId).subscribe({
      next: () => this.reload(),
      error: (error: unknown) => this.errorKey.set(errorKey(error)),
    });
  }

  protected has(member: VenueMember, permission: VenuePermission): boolean {
    return member.permissions.includes(permission);
  }

  /** Just the day; the exact hour of an invitation's expiry is noise for the owner. */
  protected expiryDate(invitation: VenueInvitation): string {
    return invitation.expiresAt.slice(0, 10);
  }

  private selectedPermissions(): VenuePermission[] {
    const values = this.inviteForm.getRawValue();
    return this.permissions.filter((permission) => values[permission]);
  }

  private reload(): void {
    this.loading.set(true);
    // Invitations are owner-only, so a staff member's request for them is expected to fail.
    forkJoin({
      venue: this.venues.get(this.venueId),
      members: this.venues.members(this.venueId),
    }).subscribe({
      next: ({ venue, members }) => {
        this.venue.set(venue);
        this.members.set(members);
        this.loading.set(false);
        if (this.isOwner()) {
          this.venues.invitations(this.venueId).subscribe({
            next: (invitations) => this.invitations.set(invitations),
            error: () => this.invitations.set([]),
          });
        }
      },
      error: (error: unknown) => {
        this.errorKey.set(errorKey(error));
        this.loading.set(false);
      },
    });
  }
}
