import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../testing/translations';
import { check, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { invitationIn, RegisterPage } from './register.page';

const LINK = '/venue-invitation?invitationId=i1&token=t0k';

describe('RegisterPage from a staff invitation link (thai-fit T1)', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [RegisterPage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function open() {
    const fixture = TestBed.createComponent(RegisterPage);
    fixture.componentRef.setInput('returnUrl', LINK);
    fixture.detectChanges();
    httpMock.expectOne('/api/auth/privacy-policy').flush({ version: '2026-09-01' });
    return fixture;
  }

  it('reads the invitation out of the link it came back to', () => {
    expect(invitationIn(LINK)).toEqual({ invitationId: 'i1', token: 't0k' });
    expect(invitationIn('/venues')).toBeNull();
    expect(invitationIn('/venue-invitation?invitationId=i1')).toBeNull();
  });

  it('signs up with a phone and no address, then goes on to take the seat', () => {
    const fixture = open();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    expect(elementOf(fixture, 'register-by-link')).not.toBeNull();

    setInput(fixture, '#phone', '081-234-5678');
    setInput(fixture, '#password', 'CorrectHorse1');
    check(fixture, 'input[type="checkbox"]');
    submitForm(fixture);

    const registration = httpMock.expectOne('/api/auth/register');
    expect(registration.request.body).toEqual({
      email: null,
      password: 'CorrectHorse1',
      privacyPolicyVersion: '2026-09-01',
      language: 'th',
      phoneNumber: '081-234-5678',
      invitationId: 'i1',
      invitationToken: 't0k',
    });
    registration.flush(null, { status: 201, statusText: 'Created' });

    // Signed in with the number they just gave, then sent back to the link.
    const login = httpMock.expectOne('/api/auth/login');
    expect(login.request.body).toEqual({ email: '081-234-5678', password: 'CorrectHorse1' });
    login.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush({
      id: 'u1',
      email: null,
      emailConfirmed: false,
      language: 'th',
      isPlatformAdmin: false,
      cannotBookBecause: null,
    });

    expect(navigate).toHaveBeenCalledWith(LINK);
  });

  it('asks for a phone or an address before sending anything', () => {
    const fixture = open();
    setInput(fixture, '#password', 'CorrectHorse1');
    check(fixture, 'input[type="checkbox"]');

    submitForm(fixture);
    fixture.detectChanges();

    expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['register.needPhoneOrEmail']);
    httpMock.expectNone('/api/auth/register');
  });
});
