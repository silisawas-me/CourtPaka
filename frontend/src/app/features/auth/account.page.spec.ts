import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import {
  check,
  elementOf,
  pageProviders,
  setInput,
  signInAs,
  submitForm,
  textOf,
} from '../../testing/dom';
import { AccountPage } from './account.page';

describe('AccountPage', () => {
  let fixture: ComponentFixture<AccountPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [AccountPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AccountPage);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  it('will not delete until the person says they understand and gives the password', () => {
    submitForm(fixture, '[data-testid=delete-form]');

    httpMock.expectNone('/api/auth/me/delete');
    expect(elementOf(fixture, 'understood-error')).not.toBeNull();
  });

  it('deletes with the password, and goes home saying so', () => {
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    check(fixture, '[data-testid=delete-understood]');
    setInput(fixture, '[data-testid=delete-password]', 'CorrectHorse1');
    submitForm(fixture, '[data-testid=delete-form]');

    const request = httpMock.expectOne('/api/auth/me/delete');
    expect(request.request.body).toEqual({ password: 'CorrectHorse1' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect(navigate).toHaveBeenCalledWith(['/'], { queryParams: { deleted: 1 } });
  });

  it('says why the server would not delete it yet', () => {
    check(fixture, '[data-testid=delete-understood]');
    setInput(fixture, '[data-testid=delete-password]', 'CorrectHorse1');
    submitForm(fixture, '[data-testid=delete-form]');

    httpMock
      .expectOne('/api/auth/me/delete')
      .flush({ code: 'account.has_upcoming_bookings' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'delete-error')).toBe(
      TRANSLATIONS.th['error.account.has_upcoming_bookings'],
    );
  });
  it('saves the number a venue would call, and says so', () => {
    signInAs('player@example.com', { phoneNumber: null, cannotBookBecause: 'auth.phone_required' });
    fixture.detectChanges();

    // Until there is a number, the page says what stands between this account and a booking.
    expect(textOf(fixture, 'cannot-book')).toBe(TRANSLATIONS.th['error.auth.phone_required']);

    setInput(fixture, '[data-testid=account-phone]', '081-234-5678');
    submitForm(fixture, '[data-testid=phone-form]');

    const saved = httpMock.expectOne('/api/auth/me/phone');
    expect(saved.request.body).toEqual({ phoneNumber: '081-234-5678' });
    saved.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush({
      id: 'u0',
      email: 'player@example.com',
      emailConfirmed: true,
      language: 'th',
      isPlatformAdmin: false,
      phoneNumber: '0812345678',
      hasPassword: true,
      signsInWithLine: false,
      cannotBookBecause: null,
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'phone-saved')).toBe(TRANSLATIONS.th['account.phoneSaved']);
    expect(elementOf(fixture, 'cannot-book')).toBeNull();
  });

  it('asks a LINE account to confirm at LINE, and deletes without a password', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    signInAs('', { email: null, hasPassword: false, signsInWithLine: true });
    fixture.detectChanges();

    // No password to ask for: the way to confirm is the same trip to LINE as signing in.
    expect(elementOf(fixture, 'delete-password')).toBeNull();
    // A POST from this page, which a link from another site cannot make the browser send.
    const confirm = elementOf(fixture, 'confirm-with-line') as HTMLButtonElement;
    const form = confirm.closest('form')!;
    expect(form.getAttribute('method')).toBe('post');
    expect(form.getAttribute('action')).toBe(
      '/api/auth/line/start?purpose=confirm&returnUrl=%2Faccount',
    );

    fixture.componentRef.setInput('line', 'confirmed');
    fixture.detectChanges();
    expect(textOf(fixture, 'line-confirmed')).toBe(TRANSLATIONS.th['account.delete.lineConfirmed']);

    check(fixture, '[data-testid=delete-understood]');
    submitForm(fixture, '[data-testid=delete-form]');

    const deleted = httpMock.expectOne('/api/auth/me/delete');
    expect(deleted.request.body).toEqual({ password: null });
    deleted.flush(null, { status: 204, statusText: 'No Content' });

    expect(navigate).toHaveBeenCalledWith(['/'], { queryParams: { deleted: 1 } });
  });
});
