import { Routes } from '@angular/router';

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
    path: 'venues',
    loadComponent: () => import('./features/venues/venues.page').then((m) => m.VenuesPage),
  },
  {
    path: 'venues/:venueId',
    loadComponent: () =>
      import('./features/venues/venue-detail.page').then((m) => m.VenueDetailPage),
  },
  {
    path: 'venue-invitation',
    loadComponent: () =>
      import('./features/venues/accept-invitation.page').then((m) => m.AcceptInvitationPage),
  },
  { path: '**', redirectTo: '' },
];
