import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

/**
 * A walk-in was sold from the top bar (owner app PR-3). The modal lives in the frame, not in the
 * page under it, so the page — the timeline, or the board of right now — hears about it here and
 * reads its day again rather than showing a court as free that has just been sold.
 */
@Injectable({ providedIn: 'root' })
export class WalkInEvents {
  readonly sold = new Subject<{ venueId: string }>();

  /** A page asks for the walk-in on a court and hour somebody tapped (the timeline's empty cell). */
  readonly open = new Subject<{ venueId: string; courtId: string; hour: number }>();
}
