import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { check, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { LineRegisterPage } from './line-register.page';

describe('LineRegisterPage', () => {
  let fixture: ComponentFixture<LineRegisterPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [LineRegisterPage],
      providers: pageProviders([{ path: 'book', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  /** Renders the page and answers what it asks as it starts: who is waiting, and the policy. */
  function render(pending: object | null = { name: 'ปกป้อง', email: 'player@example.com' }): void {
    fixture = TestBed.createComponent(LineRegisterPage);
    fixture.componentRef.setInput('returnUrl', '/book');
    fixture.detectChanges();

    httpMock.expectOne('/api/auth/privacy-policy').flush({ version: '2026-09-01' });
    const waiting = httpMock.expectOne('/api/auth/line/pending');
    if (pending) {
      waiting.flush(pending);
    } else {
      waiting.flush({ code: 'auth.line_expired' }, { status: 404, statusText: 'Not Found' });
    }
    fixture.detectChanges();
  }

  it('greets whoever LINE said came back, and says which address it shared', () => {
    render();

    expect(textOf(fixture, 'line-greeting')).toContain('ปกป้อง');
    expect(textOf(fixture, 'line-shared-email')).toContain('player@example.com');
  });

  it('makes no account until the policy is accepted', () => {
    render();

    submitForm(fixture);

    httpMock.expectNone('/api/auth/line/complete');
    expect(elementOf(fixture, 'policy-error')).not.toBeNull();
  });

  it('sends the version the server gave it, and goes where the booker was heading', () => {
    const navigate = vi
      .spyOn(TestBed.inject(Router), 'navigateByUrl')
      .mockResolvedValue(true as unknown as boolean);
    render();

    setInput(fixture, '[data-testid=line-phone]', '081-234-5678');
    check(fixture, '[data-testid=line-accept-policy]');
    submitForm(fixture);

    const completed = httpMock.expectOne('/api/auth/line/complete');
    expect(completed.request.body).toEqual({
      privacyPolicyVersion: '2026-09-01',
      language: 'th',
      phoneNumber: '081-234-5678',
    });
    completed.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush({
      id: 'u1',
      email: 'player@example.com',
      emailConfirmed: false,
      language: 'th',
      isPlatformAdmin: false,
      phoneNumber: '0812345678',
      hasPassword: false,
      signsInWithLine: true,
      cannotBookBecause: null,
    });

    expect(navigate).toHaveBeenCalledWith('/book');
  });

  it('says so in Thai when the wait between LINE and here ran out', () => {
    render(null);

    expect(textOf(fixture, 'line-error')).toBe(TRANSLATIONS.th['error.auth.line_expired']);
  });

  it('says a number is needed when LINE shared no address', () => {
    render({ name: 'ปกป้อง', email: null });

    expect(elementOf(fixture, 'line-shared-email')).toBeNull();
    expect(elementOf(fixture, 'line-phone')).not.toBeNull();
  });
});
