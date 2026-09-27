import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CurrentUser } from '../../core/auth/auth.service';
import { elementOf, pageProviders, signInAs, textOf } from '../../testing/dom';
import { HomePage } from './home.page';

describe('HomePage', () => {
  let fixture: ComponentFixture<HomePage>;

  /** The page as somebody arrives at it — signed in as the account given, or not at all. */
  function render(account?: Partial<CurrentUser>): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [HomePage], providers: pageProviders() });
    fixture = TestBed.createComponent(HomePage);
    fixture.detectChanges();

    if (account) {
      signInAs(account.email ?? 'player@example.com', account);
      fixture.detectChanges();
    }
  }

  /*
   * Finding a court is what this is for, and it is offered before anybody is asked to make an
   * account: asking somebody to sign up before they have seen a court is asking them to trust
   * something they have not been shown.
   */
  it('offers to find a court without asking anybody to sign in first', () => {
    render();

    expect(elementOf(fixture, 'home-find')).not.toBeNull();
    expect(elementOf(fixture, 'home-sign-in')).not.toBeNull();
    expect(elementOf(fixture, 'home-sign-up')).not.toBeNull();
  });

  it('still leads with finding a court once somebody is signed in', () => {
    render({ email: 'player@example.com', emailConfirmed: true, cannotBookBecause: null });

    expect(elementOf(fixture, 'home-find')).not.toBeNull();
    expect(elementOf(fixture, 'home-my-bookings')).not.toBeNull();
    expect(elementOf(fixture, 'home-sign-in')).toBeNull();
  });

  it('names the account that is signed in', () => {
    render({ email: 'player@example.com', emailConfirmed: true, cannotBookBecause: null });

    expect(textOf(fixture, 'signed-in-as')).toContain('player@example.com');
  });

  /*
   * What stands between this account and a booking is the server's answer, not the screen's — the
   * page prints the code it was given rather than working the rule out a second time.
   */
  it('says what is in the way of booking, in the words the server used', () => {
    render({
      email: 'player@example.com',
      emailConfirmed: false,
      cannotBookBecause: 'auth.email_not_confirmed',
    });

    const standing = elementOf<HTMLElement>(fixture, 'verification-state');
    expect(standing!.classList).toContain('blocked');
    expect(standing!.textContent).not.toBe('');
  });

  it('and says nothing is, when nothing is', () => {
    render({ email: 'player@example.com', emailConfirmed: true, cannotBookBecause: null });

    expect(elementOf<HTMLElement>(fixture, 'verification-state')!.classList).not.toContain(
      'blocked',
    );
  });

  /*
   * An account with no password confirms through LINE and may have no address at all, so the line
   * that names it has to hold that rather than printing an empty space (US-01).
   */
  it('has something to call an account with no address', () => {
    render({ email: null, emailConfirmed: false, cannotBookBecause: null, hasPassword: false });

    expect(textOf(fixture, 'signed-in-as')).not.toBe('');
  });
});
