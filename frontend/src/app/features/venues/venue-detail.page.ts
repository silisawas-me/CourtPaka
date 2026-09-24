import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
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
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { VenueAddressPipe } from '../../shared/venue-address.pipe';

@Component({
  selector: 'app-venue-detail-page',
  imports: [
    VenueAddressPipe,
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSlideToggleModule,
    AppDatePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './venue-detail.page.html',
})
export class VenueDetailPage {
  private readonly venues = inject(VenueService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly permissions = VENUE_PERMISSIONS;

  /** Bound from the route, so the id stays right when this page gains child routes. */
  readonly venueId = input.required<string>();

  protected readonly venue = signal<Venue | null>(null);
  protected readonly members = signal<VenueMember[]>([]);

  /** The one notice that can be turned off, and it is turned off per venue (PRD US-17). */
  protected readonly slipEmails = signal(true);
  protected readonly slipEmailsError = signal<string | null>(null);
  protected readonly invitations = signal<VenueInvitation[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);
  protected readonly memberError = signal<string | null>(null);
  protected readonly inviteError = signal<string | null>(null);
  protected readonly inviting = signal(false);
  protected readonly savingMember = signal<string | null>(null);

  /** The API reports what the caller may do here; the page never infers it from the roster. */
  protected readonly isOwner = computed(() => this.venue()?.role === 'Owner');

  /** A frozen venue refuses every write, so its controls are read-only too (PRD US-20). */
  protected readonly canManage = computed(
    () => this.isOwner() && this.venue()?.status === 'Approved',
  );

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

  constructor() {
    // The router reuses this component when only the id changes, so the load follows the input
    // rather than running once: otherwise the page would keep showing the previous venue.
    effect(() => this.load(this.venueId()));
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
        // The server replaces any pending invitation for the same mailbox, matching on a
        // case-insensitive address, so the list mirrors that rule.
        this.invitations.update((pending) => [
          ...pending.filter((item) => !sameAddress(item.email, invitation.email)),
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
    // Read the live list, not the row captured when the template rendered, so quick successive
    // ticks build on each other instead of overwriting one another.
    const current = this.members().find((item) => item.userId === member.userId);
    if (!current || this.savingMember() !== null) {
      return;
    }

    const previous = current.permissions;
    const next = granted
      ? [...previous, permission]
      : previous.filter((held) => held !== permission);

    this.savingMember.set(member.userId);
    this.memberError.set(null);
    // Show the new state at once. Putting it back on refusal is what resets the checkbox: the
    // browser has already ticked it, so only a change in the bound value writes the box back.
    this.setPermissions(member.userId, next);

    this.venues.changePermissions(this.venueId(), member.userId, next).subscribe({
      next: () => this.savingMember.set(null),
      error: (error: unknown) => {
        this.savingMember.set(null);
        this.setPermissions(member.userId, previous);
        // A failed checkbox must not take the whole page down with it.
        this.memberError.set(errorKey(error));
      },
    });
  }

  private setPermissions(userId: string, permissions: VenuePermission[]): void {
    this.members.update((list) =>
      list.map((item) => (item.userId === userId ? { ...item, permissions } : item)),
    );
  }

  protected remove(member: VenueMember): void {
    this.savingMember.set(member.userId);
    this.memberError.set(null);

    this.venues.removeMember(this.venueId(), member.userId).subscribe({
      next: () => {
        this.savingMember.set(null);
        this.members.update((list) => list.filter((item) => item.userId !== member.userId));
      },
      error: (error: unknown) => {
        this.savingMember.set(null);
        this.memberError.set(errorKey(error));
      },
    });
  }

  /**
   * Says whether this member wants to hear each time a slip arrives. Sent as it is switched
   * rather than behind a save button: there is one setting and no way to get it half-right.
   */
  protected chooseSlipEmails(wanted: boolean): void {
    this.slipEmailsError.set(null);
    this.slipEmails.set(wanted);

    this.venues.chooseSlipEmails(this.venueId(), wanted).subscribe({
      error: (failure: unknown) => {
        // Put back what the server still holds, so the switch never says something untrue.
        this.slipEmails.set(!wanted);
        this.slipEmailsError.set(errorKey(failure));
      },
    });
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.venue.set(null);
    this.members.set([]);
    this.invitations.set([]);
    this.pageError.set(null);
    this.memberError.set(null);

    forkJoin({
      venue: this.venues.get(venueId),
      members: this.venues.members(venueId),
    }).subscribe({
      next: ({ venue, members }) => {
        this.venue.set(venue);
        this.members.set(members);
        // Set from what the server holds rather than left at its default, so the switch never
        // says something the venue did not choose.
        this.slipEmails.set(venue.wantsSlipEmails);
        this.loading.set(false);

        // Only fetched when the invite section can be shown, so a frozen venue asks for nothing.
        if (venue.role === 'Owner' && venue.status === 'Approved') {
          this.venues.invitations(venueId).subscribe({
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

  private selectedPermissions(): VenuePermission[] {
    const selected = this.inviteForm.controls.permissions.getRawValue();
    return VENUE_PERMISSIONS.filter((permission) => selected[permission]);
  }
}

function sameAddress(left: string, right: string): boolean {
  return left.localeCompare(right, undefined, { sensitivity: 'accent' }) === 0;
}
