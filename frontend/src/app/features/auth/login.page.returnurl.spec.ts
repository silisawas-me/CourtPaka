import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { lineSignInAvailable, pageProviders, setInput, submitForm } from '../../testing/dom';
import { LoginPage } from './login.page';

describe('LoginPage returnUrl', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: pageProviders([
        { path: '', children: [] },
        { path: 'venues', children: [] },
      ]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function signIn(returnUrl?: string): ComponentFixture<LoginPage> {
    const fixture = TestBed.createComponent(LoginPage);
    fixture.componentRef.setInput('returnUrl', returnUrl);
    fixture.detectChanges();
    lineSignInAvailable();

    setInput(fixture, '#email', 'player@example.com');
    setInput(fixture, '#password', 'CorrectHorse1');
    submitForm(fixture);

    httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush({
      id: 'u1',
      email: 'player@example.com',
      emailConfirmed: true,
      language: 'th',
    });
    fixture.detectChanges();
    return fixture;
  }

  it('returns to the page the guard interrupted', async () => {
    await signIn('/venues').whenStable();

    expect(TestBed.inject(Router).url).toBe('/venues');
  });

  it('ignores a return address that points off this site', async () => {
    await signIn('//evil.example.com').whenStable();

    expect(TestBed.inject(Router).url).toBe('/');
  });
});
