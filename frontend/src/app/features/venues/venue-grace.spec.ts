import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenueGrace } from './venue-grace';

describe('VenueGrace', () => {
  let fixture: ComponentFixture<VenueGrace>;
  let httpMock: HttpTestingController;

  function render(canManage: boolean): void {
    TestBed.configureTestingModule({ imports: [VenueGrace], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VenueGrace);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('minutes', 15);
    fixture.componentRef.setInput('canManage', canManage);
    fixture.detectChanges();
  }

  afterEach(() => httpMock.verify());

  it('steps by five within 0–60, and the example moves with it', () => {
    render(true);
    expect(textOf(fixture, 'grace-minutes')).toContain('15');
    expect(textOf(fixture, 'grace')).toContain('19:15');

    clickOn(fixture, 'grace-more');
    fixture.detectChanges();
    expect(textOf(fixture, 'grace-minutes')).toContain('20');
    expect(textOf(fixture, 'grace')).toContain('19:20');

    clickOn(fixture, 'grace-60');
    fixture.detectChanges();
    expect(elementOf<HTMLButtonElement>(fixture, 'grace-more')!.disabled).toBe(true);

    clickOn(fixture, 'grace-0');
    fixture.detectChanges();
    expect(elementOf<HTMLButtonElement>(fixture, 'grace-less')!.disabled).toBe(true);
    expect(textOf(fixture, 'grace')).toContain('19:00');
  });

  it('saves the minutes and says so', () => {
    render(true);
    let told = 0;
    fixture.componentInstance.saved$.subscribe(() => told++);
    clickOn(fixture, 'grace-30');
    clickOn(fixture, 'grace-save');

    const request = httpMock.expectOne('/api/venues/v1/grace');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ minutes: 30 });
    request.flush({});
    fixture.detectChanges();

    expect(elementOf(fixture, 'grace-saved')).not.toBeNull();
    expect(told).toBe(1);
  });

  it('shows the minutes to somebody who may not change them, without a save', () => {
    render(false);
    expect(textOf(fixture, 'grace-minutes')).toContain('15');
    expect(elementOf(fixture, 'grace-save')).toBeNull();
    expect(elementOf<HTMLButtonElement>(fixture, 'grace-30')!.disabled).toBe(true);
  });
});
