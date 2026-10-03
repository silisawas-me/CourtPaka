import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { forkJoin, Observable } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  VENUE_PERMISSIONS,
  VenueInvitation,
  VenueMember,
  VenuePermission,
  VenueService,
} from '../../core/venues/venue.service';

/** What the artboard ticks for somebody new: the desk, and nothing that touches money or setup. */
const INVITE_DEFAULT: readonly VenuePermission[] = ['ManageBookings'];

/**
 * Who works at this branch (docs/plan/thai-fit.md T1, artboard "พนักงาน"): their permissions and
 * refund limit, and inviting somebody new. Staff at a Thai court often have only LINE and a phone,
 * so an invitation needs a name, not an address — the server hands back a link once, which the
 * owner sends over LINE or copies. The owner alone sees this tab: every door behind it is
 * OwnerOnly at the server.
 */
@Component({
  selector: 'app-venue-staff',
  imports: [AppDateTimePipe],
  templateUrl: './venue-staff.html',
  styleUrls: ['./venue-settings-tab.scss', './venue-staff.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenueStaff {
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  /** The branch's name, which the LINE message says the invitation is from. */
  readonly venueName = input('');

  protected readonly permissions = VENUE_PERMISSIONS;
  protected readonly members = signal<VenueMember[]>([]);
  protected readonly invitations = signal<VenueInvitation[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  /** The member whose "remove" was pressed once, waiting for the second press. */
  protected readonly removing = signal<string | null>(null);
  /** The member whose change was just saved, for a quiet "saved" beside the row. */
  protected readonly savedFor = signal<string | null>(null);

  protected readonly name = signal('');
  protected readonly phone = signal('');
  protected readonly email = signal('');
  protected readonly granted = signal<readonly VenuePermission[]>(INVITE_DEFAULT);
  /** The invitation just made, with the link the server shows this once. */
  protected readonly made = signal<VenueInvitation | null>(null);
  protected readonly copied = signal(false);

  protected readonly canInvite = computed(
    () => (this.name().trim() !== '' || this.email().trim() !== '') && !this.busy(),
  );

  /** What LINE opens with: who is inviting, to where, and the link on a line of its own. */
  protected readonly lineUrl = computed(() => {
    const link = this.made()?.link;
    if (!link) {
      return null;
    }
    const text = [
      `${this.i18n.t('staff.invite.message')} ${this.venueName()}`.trim(),
      this.i18n.t('staff.invite.messageTail'),
      link,
    ].join('\n');
    return `https://line.me/R/msg/text/?${encodeURIComponent(text)}`;
  });

  constructor() {
    effect(() => this.read(this.venueId()));
  }

  /** What a row is known by: the name the counter uses, then the address, then the phone. */
  protected who(member: VenueMember): string {
    return member.name || member.email || member.phone || '';
  }

  protected contact(member: VenueMember): string {
    const known = [member.email, member.phone].filter(Boolean).join(' · ');
    return known || this.i18n.t('staff.noContact');
  }

  protected initial(member: VenueMember): string {
    return [...this.who(member)][0] ?? '·';
  }

  protected toggle(member: VenueMember, permission: VenuePermission, on: boolean): void {
    const next = on
      ? [...member.permissions, permission]
      : member.permissions.filter((one) => one !== permission);
    this.save(member, this.venues.changePermissions(this.venueId(), member.userId, next));
  }

  protected setLimit(member: VenueMember, typed: string): void {
    const limit = Number(typed);
    if (typed.trim() === '' || !Number.isFinite(limit) || limit < 0) {
      return;
    }
    if (limit === member.refundLimitBaht) {
      return;
    }
    this.save(
      member,
      this.venues.changePermissions(this.venueId(), member.userId, member.permissions, limit),
    );
  }

  protected remove(member: VenueMember): void {
    if (this.removing() !== member.userId) {
      this.removing.set(member.userId);
      return;
    }
    this.run(this.venues.removeMember(this.venueId(), member.userId), () =>
      this.removing.set(null),
    );
  }

  protected grant(permission: VenuePermission, on: boolean): void {
    this.granted.update((now) =>
      on ? [...now, permission] : now.filter((one) => one !== permission),
    );
  }

  protected invite(): void {
    if (!this.canInvite()) {
      return;
    }
    this.made.set(null);
    this.copied.set(false);
    this.run(
      this.venues.invite(this.venueId(), {
        name: this.name().trim() || null,
        phone: this.phone().trim() || null,
        email: this.email().trim() || null,
        permissions: this.granted(),
      }),
      (invitation) => {
        this.made.set(invitation as VenueInvitation);
        this.name.set('');
        this.phone.set('');
        this.email.set('');
        this.granted.set(INVITE_DEFAULT);
      },
    );
  }

  protected copy(): void {
    const link = this.made()?.link;
    if (!link) {
      return;
    }
    navigator.clipboard?.writeText(link).then(
      () => this.copied.set(true),
      () => this.copied.set(false),
    );
  }

  protected revoke(invitation: VenueInvitation): void {
    this.run(this.venues.revokeInvitation(this.venueId(), invitation.id), () => {
      if (this.made()?.id === invitation.id) {
        this.made.set(null);
      }
    });
  }

  private save(member: VenueMember, door: Observable<void>): void {
    this.savedFor.set(null);
    this.run(door, () => this.savedFor.set(member.userId));
  }

  private run<T>(door: Observable<T>, after?: (value: T) => void): void {
    this.busy.set(true);
    this.error.set(null);
    door.subscribe({
      next: (value) => {
        this.busy.set(false);
        after?.(value);
        this.read(this.venueId());
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }

  private read(venueId: string): void {
    forkJoin({
      members: this.venues.members(venueId),
      invitations: this.venues.invitations(venueId),
    }).subscribe({
      next: ({ members, invitations }) => {
        this.members.set(members);
        this.invitations.set(invitations);
      },
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }
}
