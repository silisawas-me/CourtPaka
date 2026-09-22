import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { pageProviders } from '../../testing/dom';
import { DayPicker } from './day-picker';

/** A host, because what this component does is follow an input and answer with an output. */
@Component({
  imports: [DayPicker],
  template: `
    <app-day-picker
      [day]="day()"
      [min]="min"
      [max]="max"
      [openOnArrival]="open"
      (picked)="picked = $event"
    />
  `,
})
class Host {
  readonly day = signal(new Date(2026, 8, 23));
  readonly min = new Date(2026, 8, 20);
  readonly max = new Date(2026, 9, 20);
  open = false;
  picked: Date | null = null;
}

describe('DayPicker', () => {
  let fixture: ComponentFixture<Host>;

  function field(): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#day')!;
  }

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [Host],
      providers: pageProviders(),
    }).compileComponents();
    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
  });

  it('shows the day it is given, and follows it when the page moves', () => {
    expect(field().value).toContain('2569');
    const first = field().value;

    fixture.componentInstance.day.set(new Date(2026, 8, 25));
    fixture.detectChanges();

    expect(field().value).not.toBe(first);
  });

  it('answers with the day that was chosen', () => {
    const chosen = new Date(2026, 8, 25);
    fixture.debugElement.children[0].componentInstance.field.setValue(chosen);
    field().dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(fixture.componentInstance.picked).toEqual(chosen);
  });

  it('opens itself when it arrives because somebody asked for it', async () => {
    const asked = TestBed.createComponent(Host);
    asked.componentInstance.open = true;
    asked.detectChanges();
    await asked.whenStable();
    asked.detectChanges();

    // The press that downloaded the calendar is the press that opens it: nobody presses twice.
    expect(document.querySelector('mat-calendar')).not.toBeNull();
    asked.destroy();
  });

  it('stays shut when it was only fetched ahead of time', async () => {
    await fixture.whenStable();
    fixture.detectChanges();

    expect(document.querySelector('mat-calendar')).toBeNull();
  });
});
