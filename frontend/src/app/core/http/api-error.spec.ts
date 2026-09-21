import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ApiError, apiErrorInterceptor } from './api-error';

describe('apiErrorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  async function failureOf(answer: Promise<unknown>): Promise<ApiError> {
    try {
      await answer;
    } catch (failure) {
      return failure as ApiError;
    }
    throw new Error('expected the request to fail');
  }

  it('reads the code from a JSON problem body', async () => {
    const answer = firstValueFrom(http.get('/api/thing'));
    httpMock
      .expectOne('/api/thing')
      .flush({ code: 'thing.gone' }, { status: 404, statusText: 'Not Found' });

    const failure = await failureOf(answer);
    expect(failure.code).toBe('thing.gone');
    expect(failure.status).toBe(404);
  });

  /** A file request's refusal arrives as a Blob; the code inside it is still the code (US-23). */
  it('reads the code from a problem body that came back as a Blob', async () => {
    const answer = firstValueFrom(http.get('/api/file', { responseType: 'blob' }));
    httpMock.expectOne('/api/file').flush(
      new Blob([JSON.stringify({ code: 'complaint.not_open' })], {
        type: 'application/problem+json',
      }),
      { status: 409, statusText: 'Conflict' },
    );

    const failure = await failureOf(answer);
    expect(failure.code).toBe('complaint.not_open');
  });

  it('falls back to unknown when a Blob body is not a problem', async () => {
    const answer = firstValueFrom(http.get('/api/file', { responseType: 'blob' }));
    httpMock.expectOne('/api/file').flush(new Blob(['<html>'], { type: 'text/html' }), {
      status: 502,
      statusText: 'Bad Gateway',
    });

    const failure = await failureOf(answer);
    expect(failure.code).toBe('unknown');
  });
});
