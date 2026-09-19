import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { pageProviders, signInAs } from '../../testing/dom';
import { authGuard } from './auth.guard';

function run(url: string): boolean | UrlTree {
  return TestBed.runInInjectionContext(
    () => authGuard({} as never, { url } as never) as boolean | UrlTree,
  );
}

describe('authGuard', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: pageProviders() });
  });

  it('lets a signed-in account through', () => {
    signInAs();

    expect(run('/venues')).toBe(true);
  });

  it('sends an anonymous visitor to sign in and remembers where they were going', () => {
    const result = run('/venue-invitation?invitationId=i1&token=t');

    const tree = result as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(tree)).toBe(
      '/login?returnUrl=%2Fvenue-invitation%3FinvitationId%3Di1%26token%3Dt',
    );
  });
});
