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
 * Where each door leads. Staff with one venue go straight to its floor this minute — the page a
 * shift is spent on; anybody with more has the overview of all of them to pick from.
 */
export function landing(
  door: Door,
  venues: readonly Pick<Venue, 'id'>[],
  isPlatformAdmin: boolean,
): string {
  if (door === 'admin') {
    return isPlatformAdmin && venues.length === 0 ? '/admin/venues' : '/venues';
  }

  return venues.length === 1 ? `/venues/${venues[0].id}/now` : '/venues';
}
