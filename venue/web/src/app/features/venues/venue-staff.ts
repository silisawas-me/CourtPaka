import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { Observable } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  StaffPasscode,
  VENUE_PERMISSIONS,
  VenueMember,
  VenuePermission,
  VenueService,
} from '../../core/venues/venue.service';

/** What the artboard ticks for somebody new: the desk, and nothing that touches money or setup. */
const ADD_DEFAULT: readonly VenuePermission[] = ['ManageBookings'];

/**
 * Who works at this branch, as the "พนักงาน (เพิ่มแล้วเข้าได้เลย · passcode)" artboard draws it
 * (docs/plan/thai-fit.md T1, the owner's decision of 2026-10-03): their permissions and refund
 * limit, and adding somebody — a name, the phone they sign in with, what they may do. They can
 * work at once with the six-digit passcode shown here once; a lost one is replaced with "ตั้ง
 * passcode ใหม่". The owner alone sees this tab: every door behind it is OwnerOnly at the server.
 */
@Component({
  selector: 'app-venue-staff',
  templateUrl: './venue-staff.html',
  styleUrls: ['./venue-settings-tab.scss', './venue-staff.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenueStaff {
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  /** Kept for the page that hosts the tab. */
  readonly venueName = input('');

  protected readonly permissions = VENUE_PERMISSIONS;
  protected readonly members = signal<VenueMember[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  /** The member whose "remove" was pressed once, waiting for the second press. */
  protected readonly removing = signal<string | null>(null);
  /** The member whose change was just saved, for a quiet "saved" beside the row. */
  protected readonly savedFor = signal<string | null>(null);

  protected readonly name = signal('');
  protected readonly phone = signal('');
  protected readonly granted = signal<readonly VenuePermission[]>(ADD_DEFAULT);
  /** The passcode just made, for one person, shown once (only its hash is kept). */
  protected readonly made = signal<StaffPasscode | null>(null);
  protected readonly hidden = signal(false);
  protected readonly copied = signal(false);

  protected readonly canAdd = computed(
    () => this.name().trim() !== '' && this.phone().trim() !== '' && !this.busy(),
  );

  /** "482 913": two groups of three, easier to read out at the counter. */
  protected readonly shownCode = computed(() => {
    const code = this.made()?.passcode ?? '';
    return this.hidden() ? '••• •••' : `${code.slice(0, 3)} ${code.slice(3)}`;
  });

  constructor() {
    effect(() => this.read(this.venueId()));
  }

  /** What a row is known by: the name the counter uses, then the address, then the phone. */
  protected who(member: VenueMember): string {
    return member.name || member.email || member.phone || '';
  }

  /** The line under the name: the phone a passcode account signs in with, else the address. */
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

  protected add(): void {
    if (!this.canAdd()) {
      return;
    }
    this.made.set(null);
    this.run(
      this.venues.addStaff(this.venueId(), {
        name: this.name().trim(),
        phone: this.phone().trim(),
        permissions: this.granted(),
      }),
      (added) => {
        this.show(added);
        this.name.set('');
        this.phone.set('');
        this.granted.set(ADD_DEFAULT);
      },
    );
  }

  /** A new passcode for somebody who lost theirs; the old one stops working at once. */
  protected newPasscode(member: VenueMember): void {
    this.run(this.venues.newPasscode(this.venueId(), member.userId), (fresh) => this.show(fresh));
  }

  protected copy(): void {
    const code = this.made()?.passcode;
    if (!code) {
      return;
    }
    navigator.clipboard?.writeText(code).then(
      () => this.copied.set(true),
      () => this.copied.set(false),
    );
  }

  private show(answer: StaffPasscode): void {
    this.made.set(answer);
    this.hidden.set(false);
    this.copied.set(false);
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
    this.venues.members(venueId).subscribe({
      next: (members) => this.members.set(members),
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }
}
