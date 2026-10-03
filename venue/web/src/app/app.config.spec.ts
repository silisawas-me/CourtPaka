import { ActivatedRouteSnapshot } from '@angular/router';
import { isTheWayIn, pathOf } from './app.config';

/** A route tree the depth the router hands over: a root with the page somewhere under it. */
function tree(...paths: (string | undefined)[]): ActivatedRouteSnapshot {
  const nodes = [undefined, ...paths].map(
    (path) =>
      ({
        routeConfig: path === undefined ? null : { path },
        firstChild: null,
      }) as unknown as ActivatedRouteSnapshot,
  );
  nodes.forEach((node, at) => {
    (node as { firstChild: ActivatedRouteSnapshot | null }).firstChild = nodes[at + 1] ?? null;
  });
  return nodes[0];
}

describe('which navigation is the way in', () => {
  /*
   * The router hands over the root of each tree, whose own `routeConfig` is null. Read as the
   * page it would skip every navigation, including the two this exists to draw.
   */
  it('reads the page at the bottom, not the root handed over', () => {
    expect(pathOf(tree('login'))).toBe('login');
    expect(pathOf(tree('venues', ':venueId', 'money'))).toBe('money');
    expect(pathOf(tree())).toBeUndefined();
  });

  it('is the front page and a door, in either direction', () => {
    expect(isTheWayIn('', 'login')).toBe(true);
    expect(isTheWayIn('', 'register')).toBe(true);
    expect(isTheWayIn('login', '')).toBe(true);
    expect(isTheWayIn('register', '')).toBe(true);
  });

  /*
   * Everything else is a new screen and must stay one. A view transition snapshots the whole
   * document, and the court grid is the page PRD 8 measures — it pays for nothing it did not ask
   * for.
   */
  it('is nothing else, and least of all the court grid', () => {
    expect(isTheWayIn('', 'book/:venueId')).toBe(false);
    expect(isTheWayIn('book/:venueId', '')).toBe(false);
    expect(isTheWayIn('login', 'register')).toBe(false);
    expect(isTheWayIn('venues/:venueId/bookings', 'venues/:venueId/money')).toBe(false);
    expect(isTheWayIn(undefined, undefined)).toBe(false);
  });
});
