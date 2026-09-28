import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { InviteOwner } from './invite-owner';

describe('InviteOwner', () => {
  let fixture: ComponentFixture<InviteOwner>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [InviteOwner], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(InviteOwner);
    fixture.detectChanges();
    httpMock.expectOne('/api/admin/owner-invitations').flush([
      {
        id: 'i1',
        email: 'old@example.com',
        createdAt: '2026-09-01T00:00:00Z',
        expiresAt: '2026-09-15T00:00:00Z',
        acceptedAt: null,
      },
    ]);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('sends an invitation in the language chosen, and lists it first', () => {
    // One sent before and never used, long since run out.
    expect(textOf(fixture, 'owner-invitation-old@example.com')).toContain(
      TRANSLATIONS.th['inviteOwner.state.expired'],
    );

    setInput(fixture, '[data-testid="invite-owner-email"]', ' owner@example.com ');
    clickOn(fixture, 'invite-owner-lang-en');
    clickOn(fixture, 'invite-owner-send');

    const sent = httpMock.expectOne('/api/admin/owner-invitations');
    expect(sent.request.body).toEqual({ email: 'owner@example.com', language: 'en' });
    sent.flush({
      id: 'i2',
      email: 'owner@example.com',
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + 86_400_000).toISOString(),
      acceptedAt: null,
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'invite-owner-done')).toContain('owner@example.com');
    expect(textOf(fixture, 'owner-invitation-owner@example.com')).toContain(
      TRANSLATIONS.th['inviteOwner.state.waiting'],
    );
  });

  it('says why an invitation was refused', () => {
    setInput(fixture, '[data-testid="invite-owner-email"]', 'taken@example.com');
    clickOn(fixture, 'invite-owner-send');
    httpMock
      .expectOne('/api/admin/owner-invitations')
      .flush({ code: 'auth.already_has_account' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'invite-owner-error')).toBe(
      TRANSLATIONS.th['error.auth.already_has_account'],
    );
    expect(elementOf(fixture, 'invite-owner-done')).toBeNull();
  });
});
