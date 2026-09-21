import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { firstValueFrom, isObservable, Observable, of } from 'rxjs';
import { pageProviders, signInAs } from '../../testing/dom';
import { authGuard } from './auth.guard';

function run(url: string): Promise<boolean | UrlTree> {
  const result = TestBed.runInInjectionContext(() => authGuard({} as never, { url } as never));
  return firstValueFrom(
    isObservable(result)
      ? (result as Observable<boolean | UrlTree>)
      : of(result as boolean | UrlTree),
  );
}

describe('authGuard', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: pageProviders() });
  });

  it('lets a signed-in account through', async () => {
    signInAs();

    expect(await run('/venues')).toBe(true);
  });

  it('sends an anonymous visitor to sign in and remembers where they were going', async () => {
    const answer = run('/venue-invitation?invitationId=i1&token=t');
    TestBed.inject(HttpTestingController)
      .expectOne('/api/auth/me')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    const tree = (await answer) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(tree)).toBe(
      '/login?returnUrl=%2Fvenue-invitation%3FinvitationId%3Di1%26token%3Dt',
    );
  });

  /** The app no longer waits for the session at boot; a guarded page waits for it instead. */
  it('waits for the first look at the session before deciding', async () => {
    let decided = false;
    const answer = run('/venues').then((result) => {
      decided = true;
      return result;
    });
    await Promise.resolve();
    expect(decided).toBe(false);

    TestBed.inject(HttpTestingController)
      .expectOne('/api/auth/me')
      .flush({ id: 'u0', email: 'user@example.com', emailConfirmed: true, language: 'th' });

    expect(await answer).toBe(true);
  });
});
