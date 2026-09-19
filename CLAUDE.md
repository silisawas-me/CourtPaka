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
- `docs/prd.md` · PRD
- Endpoint ของ API ทุกตัวขึ้นต้นด้วย `/api` (Caddy และ proxy ของ `ng serve` ส่งต่อตาม prefix นี้)
- **Auth:** ASP.NET Core Identity + cookie · ผู้ใช้คือ `AppUser` · error ของ API ส่งเป็น `code` ใน ProblemDetails แล้วให้ frontend แปลเป็นข้อความ (US-23) · endpoint ที่ต้องยืนยันอีเมลแล้วใช้ policy `AuthorizationPolicies.EmailConfirmed`

## คำสั่ง
ต้องมี: .NET SDK 10, Node 24 LTS, Docker Desktop ที่เปิดอยู่ (integration test ใช้ Testcontainers)

| งาน | คำสั่ง (รันจาก root ของ repo) |
|---|---|
| เปิด PostgreSQL สำหรับ dev | `docker compose up -d db` |
| รัน API (dev, http://localhost:5230) | `dotnet run --project backend/src/CourtBooking.Api` |
| รัน frontend (dev, http://localhost:4200 proxy `/api` ไป API) | `cd frontend && npm start` |
| รันทั้งระบบเหมือน production (http://localhost:8080) | `docker compose --profile full up -d --build` |
| ติดตั้ง dependencies | `dotnet restore backend` · `dotnet tool restore --tool-manifest backend/dotnet-tools.json` · `cd frontend && npm ci` |
| สร้าง migration ใหม่ | `dotnet tool run dotnet-ef migrations add <ชื่อ> --project backend/src/CourtBooking.Api --output-dir Data/Migrations` |
| Apply migration ลง database | `dotnet tool run dotnet-ef database update --project backend/src/CourtBooking.Api` |
| Test backend ทั้งหมด | `dotnet test backend` |
| Test backend บางตัว | `dotnet test backend --filter "FullyQualifiedName~HealthEndpointTests"` |
| Test frontend | `cd frontend && npm test -- --watch=false` |
| Build (เหมือน CI) | `dotnet build backend -c Release -warnaserror` · `cd frontend && npm run build` |
| Build image arm64 (UAT) | `docker buildx build --platform linux/arm64 backend` |
| ตรวจ health | `GET /api/health/live` (process) · `GET /api/health/ready` (รวม database) |

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
