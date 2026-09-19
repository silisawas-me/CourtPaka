import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  STAFF_DEFAULT_PERMISSIONS,
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
  imports: [ReactiveFormsModule, RouterLink, FieldError, DatePipe],
  templateUrl: './venue-detail.page.html',
})
export class VenueDetailPage implements OnInit {
  private readonly venues = inject(VenueService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly permissions = VENUE_PERMISSIONS;

  /** Bound from the route, so the id stays right when this page gains child routes. */
  readonly venueId = input.required<string>();

  protected readonly venue = signal<Venue | null>(null);
  protected readonly members = signal<VenueMember[]>([]);
  protected readonly invitations = signal<VenueInvitation[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);
  protected readonly inviteError = signal<string | null>(null);
  protected readonly inviting = signal(false);

  /** The API reports what the caller may do here; the page never infers it from the roster. */
  protected readonly isOwner = computed(() => this.venue()?.role === 'Owner');

  /** Set per member so the template does not scan an array on every change detection pass. */
  protected readonly rows = computed(() =>
    this.members().map((member) => ({ member, granted: new Set(member.permissions) })),
  );

  protected readonly inviteForm = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    permissions: this.formBuilder.nonNullable.group(
      Object.fromEntries(
        VENUE_PERMISSIONS.map((permission) => [
          permission,
          [STAFF_DEFAULT_PERMISSIONS.includes(permission)],
        ]),
      ) as Record<VenuePermission, [boolean]>,
    ),
  });

  ngOnInit(): void {
    forkJoin({
      venue: this.venues.get(this.venueId()),
      members: this.venues.members(this.venueId()),
    }).subscribe({
      next: ({ venue, members }) => {
        this.venue.set(venue);
        this.members.set(members);
        this.loading.set(false);

        if (venue.role === 'Owner') {
          this.venues.invitations(this.venueId()).subscribe({
            next: (invitations) => this.invitations.set(invitations),
            error: () => this.invitations.set([]),
          });
        }
      },
      error: (error: unknown) => {
        this.pageError.set(errorKey(error));
        this.loading.set(false);
      },
    });
  }

  protected invite(): void {
    this.inviteForm.markAllAsTouched();
    if (this.inviteForm.controls.email.invalid || this.inviting()) {
      return;
    }

    this.inviting.set(true);
    this.inviteError.set(null);
    const email = this.inviteForm.controls.email.value;

    this.venues.invite(this.venueId(), email, this.selectedPermissions()).subscribe({
      next: (invitation) => {
        this.inviting.set(false);
        // Re-inviting replaces the pending one on the server, so the list mirrors that.
        this.invitations.update((pending) => [
          ...pending.filter((item) => item.email !== invitation.email),
          invitation,
        ]);
        this.inviteForm.controls.email.reset('');
      },
      error: (error: unknown) => {
        this.inviting.set(false);
        this.inviteError.set(errorKey(error));
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

    this.venues.changePermissions(this.venueId(), member.userId, next).subscribe({
      // The server accepted the exact list we sent, so patch it in rather than refetch the page.
      next: () =>
        this.members.update((list) =>
          list.map((item) =>
            item.userId === member.userId ? { ...item, permissions: next } : item,
          ),
        ),
      error: (error: unknown) => this.pageError.set(errorKey(error)),
    });
  }

  protected remove(member: VenueMember): void {
    this.venues.removeMember(this.venueId(), member.userId).subscribe({
      next: () =>
        this.members.update((list) => list.filter((item) => item.userId !== member.userId)),
      error: (error: unknown) => this.pageError.set(errorKey(error)),
    });
  }

  private selectedPermissions(): VenuePermission[] {
    const selected = this.inviteForm.controls.permissions.getRawValue();
    return VENUE_PERMISSIONS.filter((permission) => selected[permission]);
  }
}
