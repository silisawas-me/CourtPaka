# CLAUDE.md

## โปรเจกต์
- **ชื่อ / เป้าหมาย:** แอปจองคอร์ทแบดมินตัน: ฝั่งผู้จอง, ฝั่งเจ้าของคอร์ท/admin บริหารจัดการ รวมถึงเรื่องภาษี (รายละเอียดใน PRD ที่ `docs/prd.md`)
- **Stack:** Frontend Angular · Backend C# ASP.NET Core Web API · Database PostgreSQL
- **Server:** Linux (Ubuntu Server 24.04 LTS) + Docker Compose + Caddy · UAT: Oracle Cloud Always Free (ARM64) · PRD: VPS region Singapore (รายละเอียด PRD หัวข้อ 9.3)
- **ข้อควรระวังบน Linux:** ใช้ image `aspnet:10.0-noble-chiseled-extra` (มี ICU/tzdata สำหรับ `Asia/Bangkok` และ culture ไทย) · PDF ต้อง embed ฟอนต์ไทย · build multi-arch (amd64 + arm64) · ใช้ LF ผ่าน `.gitattributes` · path แยกตัวพิมพ์เล็ก-ใหญ่
- **Environments:** DEV → SIT → UAT → PRD

## โครงสร้าง repo
- `backend/` · .NET 10 solution `CourtBooking.slnx` · `src/CourtBooking.Api` (ASP.NET Core minimal API, EF Core + Npgsql) · `tests/CourtBooking.Api.Tests` (xUnit + Testcontainers PostgreSQL)
- `frontend/` · Angular 22 (Vitest) · `Caddyfile` ใช้ทั้ง reverse proxy และเสิร์ฟ static
- `docker-compose.yml` · stack สำหรับ local · `.github/workflows/ci.yml` · CI
- `deploy/` · compose + `.env.example` + คู่มือสำหรับเครื่อง UAT/PRD · `.github/workflows/deploy.yml` · build image ขึ้น GHCR, รัน migration bundle, deploy แล้วตรวจ health (ยังไม่เคยรันจริง รอเครื่อง UAT)
- `docs/prd.md` · PRD
- Endpoint ของ API ทุกตัวขึ้นต้นด้วย `/api` (Caddy และ proxy ของ `ng serve` ส่งต่อตาม prefix นี้)
- **Auth:** ASP.NET Core Identity + cookie · ผู้ใช้คือ `AppUser` · การยอมรับนโยบายเก็บเป็นแถวใหม่ใน `UserConsent` (ห้ามแก้ทับ) และเวอร์ชันนโยบายมาจาก config ฝั่ง server
- **Error ของ API:** ส่งเป็น `code` ใน ProblemDetails ผ่าน `ApiProblem.Of(...)` แล้วให้ frontend แปลเป็นข้อความ (US-23) ห้ามส่งข้อความภาษาคนให้ผู้ใช้จาก backend
- **สิทธิ์:** ผูกกับสนาม ไม่ใช่ role ระดับระบบ · `VenueMembership` เก็บ role + permission flags · endpoint ใต้ `/api/venues/{venueId}` ประกาศสิทธิ์ที่ต้องใช้ตอน map ด้วย `Member`, `OwnerOnly` หรือ `Needs(VenuePermissions.X)` ใน `VenueEndpoints` ซึ่งสร้าง `VenuePermissionRequirement` ให้ · Owner มีทุกสิทธิ์เสมอและถอนไม่ได้ · สนามที่ถูกระงับหรือถูกปฏิเสธจะอ่านได้อย่างเดียว · membership ของ request ปัจจุบันอยู่ใน `CurrentVenue` (handler โหลดให้แล้ว ไม่ต้อง query ซ้ำ)
- **Config ที่ทุก environment ต้องมี:** `App:BaseUrl`, `App:PrivacyPolicyVersion` (ตรวจตอนเริ่มระบบ ถ้าไม่มีจะไม่ยอมเริ่ม) · `App:RequireSecureCookies` ปิดได้เฉพาะ dev/test · `App:ApplyMigrationsOnStartup` และ `App:SeedDevelopmentData` เปิดเฉพาะ local
- **อีเมล:** Development เท่านั้นที่ log เนื้อหาอีเมล (`LoggingEmailSender` — ใช้ดูลิงก์ยืนยัน/คำเชิญตอน dev) นอกนั้นใช้ `UndeliveredEmailSender` ที่ไม่ส่งจริงและไม่ log เนื้อหา เพราะ body มี token ใช้ครั้งเดียว ผู้ให้บริการจริงรอ US-06
- **UI:** Angular Material (M3) ธีมสร้างจากสีเขียว CourtPaka ด้วย `ng generate @angular/material:theme-color` เก็บไว้ที่ `src/_theme-colors.scss` · ใช้ token ของ Material (`var(--mat-sys-*)`) แทนการตั้งสีเอง · ฟอนต์ Noto Sans Thai เสิร์ฟเองจาก `public/fonts/` (ห้ามใช้ `@import` จาก Google Fonts อีก — เป็นลูกโซ่บล็อก render) และ preload น้ำหนัก 400 ใน `index.html`
- **วันที่บนหน้าจอ:** ใช้ `AppDatePipe` (`{{ value | appDate: i18n.locale() }}`) ซึ่งผ่าน `Intl` ทำให้ภาษาไทยได้ปี พ.ศ. (locale มาจาก `TranslationService.locale()` ที่เดียว) · datepicker ของ Material ใช้ adapter ที่ตั้ง locale ตามภาษาแอป (อยู่ในหน้า settings ไม่ใช่ global เพื่อไม่ให้ติดมากับ bundle แรก) · `DatePipe` ของ Angular ไม่มี พ.ศ. จึงไม่ใช้กับวันที่ที่ผู้ใช้อ่าน · แปลงวันที่ไป-กลับกับ API ใช้ `plainDate()` / `fromPlainDate()` และวันนี้ของสนามใช้ `venueToday()` ใน `core/i18n/plain-date.ts` (ห้าม `new Date('YYYY-MM-DD')` เพราะอ่านเป็น UTC)
- **Test กับ Material:** ตัว control จริงอยู่ข้างใน host ที่ติด `data-testid` — checkbox เป็น `<input>` ส่วน slide toggle เป็น `<button role="switch">` ใช้ `switchIn()` / `controlOf()` ใน `testing/dom.ts` และ `control()` / `is_on()` / `pick()` ใน `scripts/verify/harness.py` อย่าอ่าน `.checked` จาก host ตรง ๆ
- **ราคาและนโยบายยกเลิก:** `PriceList` + `PriceBand` และ `CancellationPolicy` + `CancellationTier` เก็บเป็นเวอร์ชันที่เพิ่มอย่างเดียว ตัวใหม่สุดคือตัวที่ใช้ (ไม่มีวันที่เริ่มใช้ เพราะ BR-05 ให้การจอง snapshot ราคา/นโยบาย ณ ตอนจอง) · ราคาเก็บเป็น `decimal(10,2)` · ตอนบันทึกราคา ระบบตรวจว่าทุกชั่วโมงที่เปิดมีราคาและไม่มีช่วงซ้อนกัน
- **คอร์ทและเวลาเปิด-ปิด:** `Court` + `CourtStatusChange` (ประวัติวันเปิด/ปิดใช้งาน ใช้ตอนคิด utilization ย้อนหลัง US-15) · `OpeningHoursSchedule` เก็บเป็นเวอร์ชันตามวันที่เริ่มใช้ ไม่ทับของเก่า · เวลาเป็นชั่วโมงเต็ม เปิด 0–23 ปิด 1–24 (24 = เที่ยงคืน) ยังไม่รองรับข้ามคืน · วันที่ทุกอย่างคิดเป็นเวลาไทยผ่าน `PlatformRequirements.BangkokToday()` ห้ามใช้ `DateTime.Today`
- **บัญชีสำหรับ dev** (seed จะทำงานเฉพาะเมื่อเปิด flag **และ** environment เป็น Development): `owner@courtpaka.local` และ `staff@courtpaka.local` รหัสผ่าน `DevPassword1` สนาม `DEV01` พร้อมคอร์ท 4 คอร์ท เวลาเปิด-ปิด 06:00–22:00 ทุกวัน และราคา 200 บาท/ชม. (18:00 เป็นต้นไป 300)

## คำสั่ง
ต้องมี: .NET SDK 10, Node 24 LTS, Docker Desktop ที่เปิดอยู่ (integration test ใช้ Testcontainers)

| งาน | คำสั่ง (รันจาก root ของ repo) |
|---|---|
| เปิด PostgreSQL สำหรับ dev | `docker compose up -d db` |
| รัน API (dev, http://localhost:5230) | `dotnet run --project backend/src/CourtBooking.Api` |
| รัน frontend (dev, http://localhost:4200 proxy `/api` ไป API) | `cd frontend && npm start` |
| รันทั้งระบบเหมือน production (http://localhost:8080) | `docker compose --profile full up -d --build` |
| ติดตั้ง dependencies | `dotnet restore backend` · `cd backend && dotnet tool restore` · `cd frontend && npm ci` |
| สร้าง migration ใหม่ | `cd backend && dotnet tool run dotnet-ef migrations add <ชื่อ> --project src/CourtBooking.Api --output-dir Data/Migrations` |
| Apply migration ลง database | `cd backend && dotnet tool run dotnet-ef database update --project src/CourtBooking.Api` |
| Test backend ทั้งหมด | `dotnet test backend` |
| Test backend บางตัว | `dotnet test backend --filter "FullyQualifiedName~HealthEndpointTests"` |
| Test frontend | `cd frontend && npm test -- --watch=false` |
| Build (เหมือน CI) | `dotnet build backend -c Release -warnaserror` · `cd frontend && npm run build` |
| Build image arm64 (UAT) | `docker buildx build --platform linux/arm64 backend` |
| ตรวจ health | `GET /api/health/live` (process) · `GET /api/health/ready` (รวม database) |
| ตรวจ flow จริงบนเบราว์เซอร์ | `python scripts/verify/venue_settings.py` · `venue_pricing.py` · `venue_ui.py` (ต้องเปิด stack ด้วย `--profile full` ก่อน ดู `scripts/verify/README.md`) |

## วิธีทำงาน
- **ทำงานจากเป้าหมาย:** งานแต่ละชิ้นผูกกับ user story / acceptance criteria ใน `docs/prd.md` ถ้า requirement ไม่ชัด ให้ถามก่อนลงมือ
- **Verify เองก่อนบอกว่าเสร็จ:** รัน test + lint ให้ผ่าน และเปิดแอปจริงตรวจ flow ที่แก้ (งาน UI ใช้ `webapp-testing` แนบ screenshot) ห้ามบอกว่าเสร็จถ้ายังไม่ได้ตรวจ ถ้าตรวจไม่ได้ให้บอกตรง ๆ ว่าข้ามอะไรไป
- **Dev loop ต้องให้ Claude รันเองได้ครบ:** ถ้าขั้นตอนไหนต้องทำด้วยมือ (seed data, env var, login) ให้เขียน script หรือบันทึกไว้ในไฟล์นี้
- **Test มาพร้อมโค้ด:** ฟีเจอร์ใหม่หรือ bug fix ต้องมี test ที่ครอบคลุม
- **ใส่ event/logging** ใน flow สำคัญ เพื่อให้วัดผลและเก็บ feedback หลัง deploy ได้

## Git / PR
- ห้าม commit ตรงเข้า `main` ให้แตก branch `feat/…`, `fix/…`
- ก่อนเปิด PR: `/simplify` → `/code-review` → `/security-review`
- PR description ต้องมี: สิ่งที่เปลี่ยน, วิธีที่ตรวจ (ผล test / screenshot), ความเสี่ยงและวิธี rollback

## ข้อห้าม
- ห้าม commit secrets / `.env` ให้ใช้ `.env.example` แทน
- ห้ามแตะ PRD/production config โดยไม่ได้รับอนุมัติ
- ห้ามปิด / skip test เพื่อให้ผ่าน

## ภาษา
- สื่อสารกับผู้ใช้เป็นภาษาไทย ส่วนโค้ด ชื่อตัวแปร และ commit message ใช้ภาษาอังกฤษ
