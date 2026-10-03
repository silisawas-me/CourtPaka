import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { AdminUsersPage } from './users.page';

function user(overrides: Record<string, unknown> = {}) {
  return {
    id: 'u1',
    email: 'player@example.com',
    emailConfirmed: true,
    suspendedAt: null,
    isPlatformAdmin: false,
    ...overrides,
  };
}

describe('AdminUsersPage', () => {
  let fixture: ComponentFixture<AdminUsersPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [AdminUsersPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AdminUsersPage);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function searchFor(query: string, answer: object[] = [user()]): void {
    setInput(fixture, '[data-testid=user-query]', query);
    submitForm(fixture, 'form.search');
    httpMock
      .expectOne(
        (request) => request.url === '/api/admin/users' && request.params.get('q') === query,
      )
      .flush(answer);
    fixture.detectChanges();
  }

  function open(detail: object = { user: user(), history: [] }): void {
    clickOn(fixture, 'open-user-u1');
    httpMock.expectOne('/api/admin/users/u1').flush(detail);
    fixture.detectChanges();
  }

  it('lists who the search found, with a suspended account marked', () => {
    searchFor('play', [user({ suspendedAt: '2026-09-20T10:00:00Z' })]);

    expect(textOf(fixture, 'user-u1')).toContain('player@example.com');
    expect(elementOf(fixture, 'suspended-badge')).not.toBeNull();
  });

  it('says so when nobody matches', () => {
    searchFor('nobody', []);

    expect(textOf(fixture, 'no-users')).toBe(TRANSLATIONS.th['admin.users.none']);
  });

  it('shows the server’s answer when the search is too short', () => {
    setInput(fixture, '[data-testid=user-query]', 'ab');
    submitForm(fixture, 'form.search');
    httpMock
      .expectOne((request) => request.url === '/api/admin/users')
      .flush({ code: 'admin.query_too_short' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'search-error')).toBe(TRANSLATIONS.th['error.admin.query_too_short']);
  });

  it('will not suspend without a reason', () => {
    searchFor('play');
    open();

    submitForm(fixture, 'form.decide');

    httpMock.expectNone('/api/admin/users/u1/suspend');
    expect(elementOf(fixture, 'reason-error')).not.toBeNull();
  });

  it('suspends with the reason written, and marks the account in the list', () => {
    searchFor('play');
    open();

    setInput(fixture, '[data-testid=standing-reason]', 'โกงการจอง');
    submitForm(fixture, 'form.decide');
    const request = httpMock.expectOne('/api/admin/users/u1/suspend');
    expect(request.request.body).toEqual({ reason: 'โกงการจอง' });
    request.flush({
      user: user({ suspendedAt: '2026-09-21T10:00:00Z' }),
      history: [
        {
          suspended: true,
          reason: 'โกงการจอง',
          changedByEmail: 'admin@example.com',
          changedAt: '2026-09-21T10:00:00Z',
        },
      ],
    });
    fixture.detectChanges();

    expect(elementOf(fixture, 'suspended-badge')).not.toBeNull();
    expect(textOf(fixture, 'detail-u1')).toContain('โกงการจอง');
    expect(textOf(fixture, 'standing-decide')).toBe(TRANSLATIONS.th['admin.users.reinstate']);
    // Done, the empty reason field is a fresh one, not a mistake.
    expect(elementOf(fixture, 'reason-error')).toBeNull();
  });

  it('says so when an account cannot be opened, outside any row', () => {
    searchFor('play');

    clickOn(fixture, 'open-user-u1');
    httpMock
      .expectOne('/api/admin/users/u1')
      .flush({ code: 'unknown' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(elementOf(fixture, 'open-error')).not.toBeNull();
    expect(elementOf(fixture, 'detail-u1')).toBeNull();
  });

  it('drops an account asked for earlier once another is asked for', () => {
    searchFor('play', [user(), user({ id: 'u2', email: 'other@example.com' })]);

    clickOn(fixture, 'open-user-u1');
    const first = httpMock.expectOne('/api/admin/users/u1');
    clickOn(fixture, 'open-user-u2');
    httpMock
      .expectOne('/api/admin/users/u2')
      .flush({ user: user({ id: 'u2', email: 'other@example.com' }), history: [] });
    fixture.detectChanges();

    expect(first.cancelled).toBe(true);
    expect(elementOf(fixture, 'detail-u2')).not.toBeNull();
  });

  it('offers no door on a platform admin', () => {
    searchFor('admin', [user({ isPlatformAdmin: true })]);
    open({ user: user({ isPlatformAdmin: true }), history: [] });

    expect(elementOf(fixture, 'standing-decide')).toBeNull();
  });
});
