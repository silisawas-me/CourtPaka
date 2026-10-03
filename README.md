# badPaka

แอปจองคอร์ทแบดมินตัน: ผู้เล่นจองคอร์ทเอง สนามตรวจสลิปและจัดการการจอง และ platform ดูแลสนามที่เข้าร่วม
ข้อกำหนดทั้งหมดอยู่ใน [`docs/prd.md`](docs/prd.md) — ทุกอย่างในโค้ดอ้างอิงถึง user story ในนั้น

- **Frontend** Angular 22 + Material (M3) · mobile-first · ไทย/อังกฤษ · ติดตั้งเป็น PWA ได้
- **Backend** ASP.NET Core 10 minimal API + EF Core · PostgreSQL 17
- **ที่รัน** Docker Compose + Caddy บน Linux (UAT: Oracle Cloud ARM64 · PRD: VPS สิงคโปร์)

## เริ่มเล่นกับมันใน 2 คำสั่ง

ต้องมี Docker Desktop ที่เปิดอยู่

```bash
docker compose --profile full up -d --build   # ทั้งระบบที่ http://localhost:8080
```

บัญชีสำหรับ dev (สร้างให้อัตโนมัติ รหัสผ่าน `DevPassword1`):
`owner@courtpaka.local` (เจ้าของสนาม) · `staff@courtpaka.local` (พนักงาน) · `admin@courtpaka.local` (platform)
สนามตัวอย่างมี 4 คอร์ท เปิด 06:00–22:00 ราคา 200 บาท/ชม. (ตั้งแต่ 18:00 เป็น 300)

จะแยกรันทีละฝั่งตอนพัฒนาก็ได้ (API ที่ 5230, frontend ที่ 4200) — คำสั่งทั้งหมดอยู่ในตารางใน
[`CLAUDE.md`](CLAUDE.md) ซึ่งเป็นคู่มือการทำงานกับ repo นี้: กติกาของโค้ด ข้อตกลงที่ตัดสินไปแล้ว และกับดักที่เคยเจอ

## ทดสอบ

```bash
dotnet test api                    # xUnit + Testcontainers (ต้องมี Docker)
cd venue/web && npm test -- --watch=false
python scripts/verify/booking_grid.py  # ขับเบราว์เซอร์จริงบน stack ที่เปิดอยู่
```

`scripts/verify/` คือชุดตรวจ flow จริงด้วย Playwright ([README ของมัน](scripts/verify/README.md)) ·
`scripts/perf/grid_lcp.py` วัดความเร็วหน้าตารางคอร์ทตามเกณฑ์ PRD 8 · `scripts/load/` คือ load test (k6)

CI รัน backend, frontend, docker build, browser check และ OWASP ZAP baseline ทุก pull request

## ขึ้นเครื่องจริง

[`deploy/`](deploy/README.md) มี compose, `.env.example`, สคริปต์ backup และคู่มือของเครื่อง UAT/PRD
การ deploy เป็น GitHub Actions: build image ขึ้น GHCR → รัน migration → สลับ container → เช็ก health

## สถานะ

กำลังอยู่ใน M3 ตามแผนใน PRD หัวข้อ 10.2 — จองครบวงจรแล้ว (ค้นหา, ตาราง, จอง, PromptPay + สลิป,
ตรวจสลิป, ยกเลิก/คืนเงิน, ขายหน้าเคาน์เตอร์, แจ้งเตือนทางอีเมล, dashboard ของสนามและ platform, เรื่องร้องเรียน)
ที่ยังเหลือคือเอกสารภาษี (US-07, US-16) และค่าคอมมิชชัน (US-21) ซึ่งรอคำตอบจากนักบัญชีและอัตราที่จะใช้
