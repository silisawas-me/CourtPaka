# syntax=docker/dockerfile:1
# Static Angular build served by Caddy, which also reverse-proxies /api to the API container.
FROM --platform=$BUILDPLATFORM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM caddy:2-alpine
COPY Caddyfile /etc/caddy/Caddyfile
COPY --from=build /app/dist/court-booking-web/browser /srv
