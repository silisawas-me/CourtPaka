import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { check, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { CancellationPolicyEditor } from './cancellation-policy';

const DEFAULT_POLICY = {
  id: '00000000-0000-0000-0000-000000000000',
  createdAt: '0001-01-01T00:00:00+00:00',
  tiers: [{ hoursBefore: 24, refundPercent: 100 }],
};

describe('CancellationPolicyEditor', () => {
  let fixture: ComponentFixture<CancellationPolicyEditor>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [CancellationPolicyEditor],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function render(policy: object = DEFAULT_POLICY, canManage = true): void {
    fixture = TestBed.createComponent(CancellationPolicyEditor);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('canManage', canManage);
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/cancellation-policy').flush(policy);
    fixture.detectChanges();
  }

  it('shows the terms a venue starts with', () => {
    render();

    expect(textOf(fixture, 'tier-24')).toContain('24');
    expect(textOf(fixture, 'tier-24')).toContain('100%');
    expect(textOf(fixture, 'policy-otherwise')).toBe(TRANSLATIONS.th['cancellation.otherwise']);
  });

  it('publishes the steps as they are edited', () => {
    render();

    check(fixture, '[data-testid="add-tier"]');
    setInput(fixture, '[data-testid="tier-row-1"] input[type="number"]', '6');
    submitForm(fixture);

    const request = httpMock.expectOne('/api/venues/v1/cancellation-policy');
    expect(request.request.body).toEqual({
      tiers: [
        { hoursBefore: 24, refundPercent: 100 },
        { hoursBefore: 6, refundPercent: 50 },
      ],
    });

    request.flush({
      id: 'c1',
      createdAt: '2026-09-19T00:00:00Z',
      tiers: [
        { hoursBefore: 24, refundPercent: 100 },
        { hoursBefore: 6, refundPercent: 50 },
      ],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'tier-6')).toContain('50%');
  });

  it('offers no more than three steps', () => {
    render();

    for (let step = 0; step < 2; step++) {
      check(fixture, '[data-testid="add-tier"]');
    }

    expect(elementOf(fixture, 'tier-row-2')).not.toBeNull();
    expect(elementOf<HTMLButtonElement>(fixture, 'add-tier')?.disabled).toBe(true);
  });

  it('translates a refusal', () => {
    render();

    submitForm(fixture);
    httpMock
      .expectOne('/api/venues/v1/cancellation-policy')
      .flush({ code: 'pricing.tiers_not_in_order' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'policy-error')).toBe(
      TRANSLATIONS.th['error.pricing.tiers_not_in_order'],
    );
  });

  it('offers no editor to someone without the permission', () => {
    render(DEFAULT_POLICY, false);

    expect(elementOf(fixture, 'tier-row-0')).toBeNull();
    expect(elementOf(fixture, 'add-tier')).toBeNull();
    expect(textOf(fixture, 'tier-24')).toContain('100%');
  });
});
