import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { check, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
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
    submitForm(fixture);

    httpMock.expectNone('/api/auth/me/delete');
    expect(elementOf(fixture, 'understood-error')).not.toBeNull();
  });

  it('deletes with the password, and goes home saying so', () => {
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    check(fixture, '[data-testid=delete-understood]');
    setInput(fixture, '[data-testid=delete-password]', 'CorrectHorse1');
    submitForm(fixture);

    const request = httpMock.expectOne('/api/auth/me/delete');
    expect(request.request.body).toEqual({ password: 'CorrectHorse1' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect(navigate).toHaveBeenCalledWith(['/'], { queryParams: { deleted: 1 } });
  });

  it('says why the server would not delete it yet', () => {
    check(fixture, '[data-testid=delete-understood]');
    setInput(fixture, '[data-testid=delete-password]', 'CorrectHorse1');
    submitForm(fixture);

    httpMock
      .expectOne('/api/auth/me/delete')
      .flush({ code: 'account.has_upcoming_bookings' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'delete-error')).toBe(
      TRANSLATIONS.th['error.account.has_upcoming_bookings'],
    );
  });
});
