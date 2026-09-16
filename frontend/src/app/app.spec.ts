import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  async function renderWithHealthResponse(respond: (url: string) => void): Promise<string | undefined> {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    respond('/api/health/ready');
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    return element.querySelector('[data-testid="api-status"]')?.textContent?.trim();
  }

  it('shows the API status when the API is healthy', async () => {
    const status = await renderWithHealthResponse((url) =>
      httpMock.expectOne(url).flush({ status: 'Healthy', checks: [] }),
    );

    expect(status).toBe('Healthy');
  });

  it('shows Unhealthy when the API reports its database is down', async () => {
    const status = await renderWithHealthResponse((url) =>
      httpMock
        .expectOne(url)
        .flush({ status: 'Unhealthy', checks: [] }, { status: 503, statusText: 'Service Unavailable' }),
    );

    expect(status).toBe('Unhealthy');
  });

  it('shows unreachable when the API cannot be contacted', async () => {
    const status = await renderWithHealthResponse((url) =>
      httpMock.expectOne(url).error(new ProgressEvent('error'), { status: 0 }),
    );

    expect(status).toBe('unreachable');
  });
});
