import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/home/home.page').then((m) => m.HomePage),
  },
  {
    path: 'register',
    loadComponent: () => import('./features/auth/register.page').then((m) => m.RegisterPage),
  },
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage),
  },
  {
    path: 'verify-email',
    loadComponent: () => import('./features/auth/verify-email.page').then((m) => m.VerifyEmailPage),
  },
  {
    path: 'resend-verification',
    loadComponent: () =>
      import('./features/auth/resend-verification.page').then((m) => m.ResendVerificationPage),
  },
  {
    path: 'book',
    loadComponent: () =>
      import('./features/booking/venue-search.page').then((m) => m.VenueSearchPage),
  },
  {
    path: 'bookings/:bookingId',
    loadComponent: () => import('./features/booking/booking.page').then((m) => m.BookingPage),
    canActivate: [authGuard],
  },
  {
    path: 'book/:venueId',
    loadComponent: () =>
      import('./features/booking/availability.page').then((m) => m.AvailabilityPage),
  },
  {
    path: 'venues',
    canActivate: [authGuard],
    loadComponent: () => import('./features/venues/venues.page').then((m) => m.VenuesPage),
  },
  {
    path: 'venues/:venueId',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/venues/venue-detail.page').then((m) => m.VenueDetailPage),
  },
  {
    path: 'venues/:venueId/settings',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/venues/venue-settings.page').then((m) => m.VenueSettingsPage),
  },
  {
    path: 'venue-invitation',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/venues/accept-invitation.page').then((m) => m.AcceptInvitationPage),
  },
  { path: '**', redirectTo: '' },
];
