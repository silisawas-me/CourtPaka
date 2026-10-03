import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { clickOn, elementOf, pageProviders } from '../../testing/dom';
import { WelcomePage } from './welcome.page';

describe('WelcomePage (first sign-in of staff an owner added)', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [WelcomePage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('starts only once the person themselves accepts the policy, then goes where they were going', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(WelcomePage);
    fixture.componentRef.setInput('returnUrl', '/venues/v1/timeline');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/mine').flush([{ id: 'v1', name: 'อารีย์' }]);
    fixture.detectChanges();

    expect((elementOf(fixture, 'welcome-start') as HTMLButtonElement).disabled).toBe(true);
    (
      fixture.nativeElement.querySelector(
        '[data-testid="welcome-accept"] input',
      ) as HTMLInputElement
    ).click();
    fixture.detectChanges();
    clickOn(fixture, 'welcome-start');

    httpMock.expectOne('/api/auth/privacy-policy').flush({ version: '2026-09-01' });
    const consent = httpMock.expectOne('/api/auth/me/consent');
    expect(consent.request.body).toEqual({ privacyPolicyVersion: '2026-09-01' });
    consent.flush(null);
    httpMock.expectOne('/api/auth/me').flush({ id: 'u1', email: null, needsConsent: false });

    expect(navigate).toHaveBeenCalledWith('/venues/v1/timeline');
  });
});
