import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { AvailabilityPage } from './features/booking/availability.page';

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
    path: 'register/line',
    loadComponent: () =>
      import('./features/auth/line-register.page').then((m) => m.LineRegisterPage),
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
    path: 'venues/:venueId/bookings',
    data: { venueShell: true },
    loadComponent: () =>
      import('./features/venues/venue-bookings.page').then((m) => m.VenueBookingsPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/money',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/money.page').then((m) => m.MoneyPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/dashboard',
    data: { venueShell: true },
    loadComponent: () =>
      import('./features/venues/venue-dashboard.page').then((m) => m.VenueDashboardPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/closures',
    data: { venueShell: true },
    loadComponent: () =>
      import('./features/venues/court-closures.page').then((m) => m.CourtClosuresPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/series',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/series.page').then((m) => m.SeriesPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/slip-queue',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/slip-queue.page').then((m) => m.SlipQueuePage),
    canActivate: [authGuard],
  },
  {
    path: 'account',
    loadComponent: () => import('./features/auth/account.page').then((m) => m.AccountPage),
    canActivate: [authGuard],
  },
  {
    path: 'bookings',
    loadComponent: () =>
      import('./features/booking/my-bookings.page').then((m) => m.MyBookingsPage),
    canActivate: [authGuard],
  },
  {
    path: 'bookings/:bookingId',
    loadComponent: () => import('./features/booking/booking.page').then((m) => m.BookingPage),
    canActivate: [authGuard],
  },
  {
    path: 'book/:venueId',
    component: AvailabilityPage,
  },
  {
    path: 'admin/dashboard',
    loadComponent: () =>
      import('./features/admin/dashboard.page').then((m) => m.AdminDashboardPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/complaints',
    loadComponent: () =>
      import('./features/admin/complaints.page').then((m) => m.AdminComplaintsPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/users',
    loadComponent: () => import('./features/admin/users.page').then((m) => m.AdminUsersPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/venues',
    loadComponent: () => import('./features/admin/venues.page').then((m) => m.AdminVenuesPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues',
    canActivate: [authGuard],
    loadComponent: () => import('./features/venues/venues.page').then((m) => m.VenuesPage),
  },
  {
    path: 'venues/apply',
    loadComponent: () => import('./features/venues/apply.page').then((m) => m.VenueApplyPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId',
    data: { venueShell: true },
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/venues/venue-detail.page').then((m) => m.VenueDetailPage),
  },
  {
    path: 'venues/:venueId/settings',
    data: { venueShell: true },
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
