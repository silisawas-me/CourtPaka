import { Venue } from '../../core/venues/venue.service';

/**
 * The two ways into the venue side (docs/plan/cut-booker.md, D12–D15): the owner or manager of a
 * venue, and its staff. Same accounts, same password — the door decides where a person lands and
 * refuses the wrong one with a word about which door is theirs.
 *
 * This is way-finding, not permission: every venue endpoint still checks the person's role at
 * that venue on the server. Somebody who comes in the wrong door gains nothing by it.
 */
export type Door = 'admin' | 'staff';

export function isDoor(value: string | undefined): value is Door {
  return value === 'admin' || value === 'staff';
}

/**
 * Whether this person belongs behind this door, or the translation key that says why not. Read
 * from the roles the server gave for each venue, never worked out here.
 */
export function refusal(
  door: Door,
  venues: readonly Pick<Venue, 'role'>[],
  isPlatformAdmin: boolean,
): string | null {
  if (door === 'admin') {
    return isPlatformAdmin || venues.some((venue) => venue.role === 'Owner')
      ? null
      : 'login.door.notAnOwner';
  }

  return venues.length > 0 ? null : 'login.door.notStaff';
}

/**
 * Where each door leads: the first page of the owner app (docs/plan/owner-app.md). An admin
 * opens on every venue's schedule at once; staff with one venue on that venue's timeline, which
 * is what the design shows a member of staff; anybody with more has the overview to pick from.
 */
export function landing(
  door: Door,
  venues: readonly Pick<Venue, 'id'>[],
  isPlatformAdmin: boolean,
): string {
  if (door === 'admin') {
    return isPlatformAdmin && venues.length === 0 ? '/admin/venues' : '/venues';
  }

  return venues.length === 1 ? `/venues/${venues[0].id}/bookings` : '/venues';
}
