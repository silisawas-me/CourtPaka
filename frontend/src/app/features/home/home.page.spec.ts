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
      signInAs(account.email ?? 'owner@example.com', account);
      fixture.detectChanges();
    }
  }

  /*
   * The venue side only (docs/plan/cut-booker.md, D12): two doors, one for the people who run a
   * venue and one for the people who work its counter, and no way to sign yourself up.
   */
  it('offers the admin door and the staff door, and nothing to sign up with', () => {
    render();

    expect(elementOf(fixture, 'home-door-admin')?.getAttribute('href')).toBe('/login?as=admin');
    expect(elementOf(fixture, 'home-door-staff')?.getAttribute('href')).toBe('/login?as=staff');
    expect(fixture.nativeElement.querySelector('a[href^="/register"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('a[href^="/book"]')).toBeNull();
  });

  it('leads somebody signed in to their venues', () => {
    render({ email: 'owner@example.com', emailConfirmed: true });

    expect(elementOf(fixture, 'home-my-venues')?.getAttribute('href')).toBe('/venues');
    expect(elementOf(fixture, 'home-door-admin')).toBeNull();
  });

  it('names the account that is signed in', () => {
    render({ email: 'owner@example.com', emailConfirmed: true });

    expect(textOf(fixture, 'signed-in-as')).toContain('owner@example.com');
  });

  /*
   * An account made through LINE before the sign-up doors were taken out may have no address at
   * all, so the line that names it has to hold that rather than an empty space (US-01).
   */
  it('has something to call an account with no address', () => {
    render({ email: null, emailConfirmed: false, hasPassword: false });

    expect(textOf(fixture, 'signed-in-as')).not.toBe('');
  });
});
