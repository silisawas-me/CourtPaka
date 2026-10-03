# แอปฝั่งผู้จอง — เว็บ · เว็บใน LINE (LIFF) · แอปมือถือ (+ แอปมือถือสนาม)

> **Handoff:** ไฟล์นี้เขียนให้ session ถัดไป (หรือคนถัดไป) หยิบไปทำต่อได้โดยไม่ต้องย้อนอ่านแชต
> เริ่มที่ "ก่อนลงมือ" แล้วทำตาม "ลำดับงาน" ทีละขั้น · ทุกขั้นที่ทำเสร็จเพิ่มบรรทัดใน "บันทึกการทำงาน" ท้ายไฟล์

## ที่มา

- 2026-09-28 (#103 `13cf827`) เจ้าของสั่งตัดฝั่งผู้จองออกจาก frontend ทั้งหมด (ดู `docs/plan/cut-booker.md`)
  **backend ไม่ถูกแตะ** API ของผู้จองยังอยู่ครบและมี test คุมอยู่
- 2026-10-03 เจ้าของบอกว่า "เราจะมีแอพฝั่งผู้จองแน่นอน"
- 2026-10-04 เจ้าของสั่งเตรียมทำแอปฝั่งคนจอง **สามช่องทาง: เว็บ · แอปมือถือ · เว็บที่เปิดใน LINE (LIFF)**
- PRD D6 วางไว้เป็นเฟส: MVP = เว็บ (PWA) + LINE Login · เฟส 2 = LIFF + แจ้งเตือนทาง LINE (US-34 ทำแล้วฝั่ง backend) · เฟส 3 = แอปมือถือ (F12)

## ตัดสินแล้ว (2026-10-04)

- **ระบบมี 5 ตัว:** API (ตัวเดียว ใช้ร่วม) · เว็บสนาม · **แอปมือถือสนาม** · เว็บคนจอง (รวม LIFF) · แอปมือถือคนจอง
- **แอปมือถือเป็น native ด้วย React Native + Expo** — เจ้าของ: "ไม่เอา mobile app กิ๊กก๊อก แบบเหมือน webview ไม่เอา อยากให้แอพไว เสถียร" และเน้นง่าย / deploy ง่าย / track ง่าย / อัตโนมัติทั้ง Android และ iOS
  - build + ส่ง store: EAS Build + EAS Submit · แก้บั๊กไม่ต้องรอรีวิว: EAS Update · crash/error: Sentry · event การใช้งาน: ระบบ event ของ backend (PRD 8.1)
  - ภาษา TypeScript → ใช้ `th.json`/`en.json`, type ของ API และฟังก์ชันเงิน/วันที่ ร่วมกับเว็บผ่าน `shared/`
  - **ยอมรับแล้ว:** หน้าจอมีสองชุด (Angular บนเว็บ, React Native บนมือถือ) กฎเงินและสิทธิ์อยู่ที่ API ชุดเดียว
  - **ไม่ใช้ Capacitor / WebView**
- **โครงโฟลเดอร์ (ยอมรับแล้ว ยังไม่ได้ย้าย):**

```
repo/
├── api/              API (เดิม backend/)
├── venue/web/        เว็บสนาม (เดิม frontend/)
├── venue/mobile/     แอปสนาม (Expo)
├── booker/web/       เว็บคนจอง + LIFF
├── booker/mobile/    แอปคนจอง (Expo)
├── shared/           ไฟล์ภาษา + type ของ API ใช้ร่วม
├── deploy/ · docs/ · scripts/
```

## เป้าหมาย

ผู้เล่นแบดหาสนาม ดูตารางว่าง จอง จ่ายด้วย QR พร้อมเพย์ + อัปโหลดสลิป ดู/ยกเลิกการจอง และเข้าคิวรอได้
จากสามที่: เบราว์เซอร์มือถือและในแอป LINE (เว็บชุดเดียวกัน) · แอป native ที่ติดตั้งจาก App Store / Play Store (Expo)
ครอบ PRD US-01 ถึง US-07, US-27 (คิวรอ), US-34 (แจ้งเตือนทาง LINE) ตาม acceptance criteria ใน `docs/prd.md` 5.1

## ก่อนลงมือ — กฎของงานนี้ (จากเจ้าของ ใช้ทุกขั้น)

1. **หน้าจอใหม่ต้องวาดเป็น Design artifact ก่อน** ให้เจ้าของอนุมัติ แล้วค่อยเขียนโค้ด **ตาม design ตรง ๆ** ไม่ใช่เอาหน้าเก่ามาแต่งใหม่
2. **ถ้าจะเสียเงิน บอกก่อนและรอคำตอบ** เสนอทางฟรีก่อนเสมอ (บัญชี Apple/Google, LINE OA แพ็กเกจเสียเงิน, บริการตรวจสลิป, อีเมล)
3. ทุกงาน = branch → commit → PR (สิ่งที่เปลี่ยน / วิธีที่ตรวจ / ความเสี่ยงและ rollback) → CI ผ่านครบ → squash merge → กลับ main
4. คุยกับเจ้าของเป็นภาษาไทย · โค้ดและ commit เป็นภาษาอังกฤษ
5. **ห้ามแตะ PRD (`docs/prd.md`) และ config ของเครื่อง production โดยไม่ได้รับอนุมัติ**
6. เขียนบันทึกลงไฟล์นี้หลังทุกขั้น

## สิ่งที่มีอยู่แล้ว (ใช้ต่อได้ทันที)

### API ฝั่งผู้จอง (ไม่ต้องเขียนใหม่)

| เรื่อง | Endpoint | Story |
|---|---|---|
| ค้นหาสนาม (ไม่ต้อง login) | `GET /api/venues/search?q=` | US-02 |
| ตารางว่างของวัน | `GET /api/venues/{id}/availability?date=` (poll ส่ง `refresh=true`) | US-02 |
| จอง (เป็น `Held` 15 นาที) | `POST /api/bookings` | US-03 |
| QR + ยอดที่ต้องโอน | `GET /api/bookings/{id}/payment` | US-04 |
| ส่งสลิป / ดูสลิป | `POST`·`GET /api/bookings/{id}/slip` | US-04 |
| การจองของฉัน (server แบ่ง upcoming/past) | `GET /api/bookings` · `GET /api/bookings/{id}` | US-05 |
| ยกเลิก (ยอดคืนจาก server) | `POST /api/bookings/{id}/cancel` | US-05 |
| คิวรอ | `POST`·`GET /api/waitlist` · `DELETE /api/waitlist/{entryId}` | US-27 |
| บัญชี | `register` · `verify-email` · `login` · `logout` · `me` · `me/language` · `me/phone` · `me/consent` · `me/delete` ใต้ `/api/auth` | US-01, PDPA |
| LINE Login | `/api/auth/line` (`start` → LINE → `callback` → `pending` → `complete` ซึ่งเป็นตัวที่ยอมรับนโยบาย) | US-01 |
| แจ้งเตือนผู้จอง | `Jobs/BookerMail` (อีเมล) + `BookerReach` (LINE ถ้ามี token) — **ปิดอยู่** ด้วย `App:TellBookers=false` | US-06, US-34 |

กฎทั้งหมด (ราคา snapshot, กันจองซ้อนด้วย exclusion constraint, hold 15 นาที, มัดจำ, ยอดคืน) อยู่ที่ server แล้ว
**หน้าจอห้ามคำนวณเงินหรือสิทธิ์เอง** — อ่านจากคำตอบของ server ตามที่ CLAUDE.md อธิบายไว้ในแต่ละหัวข้อ

### หน้าจอเดิมที่ลบไป (ใช้ดูตรรกะได้ ห้ามเอามาเป็นหน้าตา)

อยู่ใน git ที่ commit ก่อน `13cf827`: `git show 13cf827^:frontend/src/app/features/booking/<ไฟล์>`
- `venue-search.page.*` · `availability.page.*` (grid + poll 10 วิ + `refresh=true`) · `day-picker.ts`
- `booking.page.*` (QR + สลิป + countdown) · `my-bookings.page.*` (ยกเลิก + ยอดคืน) · `waitlist-card.*`
- spec ของแต่ละหน้า + verify script `booking_grid`, `booking`, `payment`, `my_bookings`, `waitlist`, `line_login`, `line_notices`, `perf/grid_lcp.py`

## ต้องให้เจ้าของตัดสินก่อน (ห้ามเดา)

| # | คำถาม | ตัวเลือก | ที่แนะนำ |
|---|---|---|---|
| B1 | แอปผู้จองแยกจากแอปสนาม หรืออยู่แอปเดียวกัน | — | ✅ **แยก** ตามโครงโฟลเดอร์ข้างบน (2026-10-04) |
| B2 | โดเมนของแอปผู้จอง | `baanpaka.com` (ราก) · `app.baanpaka.com` · อื่น ๆ | **`baanpaka.com`** — `webapp.baanpaka.com` เป็นของแอปสนามอยู่แล้ว |
| B3 | แอปมือถือทำแบบไหน | — | ✅ **React Native + Expo** (native ไม่ใช่ WebView) ทั้งแอปสนามและแอปคนจอง (2026-10-04) |
| B4 | ค่าใช้จ่ายที่เลี่ยงไม่ได้ของแอปมือถือ | Apple Developer (รายปี) · Google Play (ครั้งเดียว) · EAS (มีแพ็กเกจฟรี จำนวน build จำกัด; build บน GitHub Actions ได้ฟรีเพราะ repo public) — **ต้องเช็กราคาล่าสุดก่อนถาม** | ทำเว็บ + LIFF ให้เสร็จก่อน แอปมือถือรออนุมัติเงิน |
| B5 | LINE: ใช้ channel ไหน | LINE Login channel (มีโค้ดรองรับแล้ว) + LIFF app + LINE OA (Messaging API) | ตรวจโควต้าข้อความฟรีของ OA ล่าสุดก่อนเปิด US-34 |
| B6 | การสมัครของผู้จอง | ตอนนี้ `App:OpenSignUp=false` (สมัครได้เฉพาะคนที่ถูกเชิญ, D17) | เปิดสมัครให้**ผู้จอง** แต่การสร้างสนามต้องผ่าน `OwnerInvitation` เท่านั้น — ต้องแก้ backend ให้สองเรื่องนี้แยกกัน |
| B7 | อีเมลจริง | ยังไม่ได้เลือกผู้ให้บริการ | ต้องมีก่อนเปิดผู้จอง (ยืนยันอีเมลก่อนจอง US-01) — เทียบตัวฟรีให้เลือก |
| B8 | ตรวจสลิปอัตโนมัติ | เลือก SlipOK แล้ว **ยังไม่สมัคร** (2026-10-03) | ไม่ขวางแอปผู้จอง สนามตรวจเองได้ (หน้า "ตรวจสลิป" มีแล้ว) |

## แนวทางเทคนิค

```
booker/web (Angular) ─┬── เบราว์เซอร์มือถือ (PWA)
                      └── ใน LINE (LIFF: liff.init แล้ว login ด้วย LINE ทันที)
booker/mobile (Expo)  ──── แอป native iOS / Android
venue/web · venue/mobile ── ฝั่งสนาม
        │  /api  (เว็บ: cookie session · แอป: token — ต้องเพิ่ม)
        ▼
api/ (.NET) ── PostgreSQL
```

- **โครงสร้าง:** `booker/web` เป็นโปรเจกต์ Angular ของตัวเอง (build แยก bundle แยก งบ bundle แรกของตัวเอง) · โค้ดที่เว็บสนามกับเว็บคนจองใช้ร่วม (i18n, `baht`/`appDate`/`clock` pipe, `plain-date`, api-error interceptor) ย้ายไป `shared/` — ไม่ก๊อปโค้ด · ส่วนที่แอป Expo ใช้ร่วมได้คือไฟล์ภาษา, type ของ API และฟังก์ชันล้วน (เงิน/วันที่) ไม่ใช่ component
- **LIFF:**
  - โหลด LIFF SDK **แบบ dynamic เฉพาะตอนเปิดใน LINE** (ไม่ให้ติด bundle แรกของเว็บปกติ)
  - login ใน LIFF ต้องได้ session ของเรา: ต้องมี endpoint ใหม่ที่รับ ID token ของ LIFF แล้ว **ตรวจกับ LINE ที่ server** (ห้ามเชื่อ token ที่ไม่ได้ตรวจ) แล้วเข้าทางเดียวกับ `LineLoginEndpoints` (pending → ยอมรับนโยบายเอง → complete)
  - กฎเดิมยังใช้: อีเมลที่ LINE ให้มาห้ามใช้จับคู่บัญชีเดิม
  - CSP ใน Caddyfile ของแอปผู้จองต้องอนุญาต script/connect ของโดเมน LINE — **อ่านเรื่อง hash ของ inline script ใน CLAUDE.md ก่อนแก้ CSP**
- **แอปมือถือ (Expo):** ⚠️ **backend ต้องเพิ่มการล็อกอินแบบ token** (ตอนนี้เป็น cookie ซึ่งเหมาะกับเว็บ) — ออกแบบให้ใช้กฎเดิมทั้งหมด (lockout, ระงับบัญชี, security stamp, ลบบัญชี, PDPA consent) · เก็บ token ใน secure storage ของเครื่อง · LINE Login ใน native ต้องตรวจ token ที่ server เหมือน LIFF · ลิงก์จากอีเมล/LINE ต้องเปิดแอปได้ (universal link / app link) · กุญแจเซ็นแอปเก็บเป็น secret ที่ใช้ได้เฉพาะ build จาก main (repo เป็น public)
- **ลิงก์ในข้อความถึงผู้จอง:** `BookerMail` สร้างลิงก์จาก `App:BaseUrl` + `/bookings/{id}` (`Jobs/BookerMail.cs:316`) — ถ้าแอปผู้จองอยู่คนละโดเมน (B2) ต้องเพิ่ม config แยก เช่น `App:BookerBaseUrl` และแอปผู้จองต้องมี route `/bookings/:id`
- **เปิดแจ้งเตือนผู้จองคืน:** `App:TellBookers=true` เมื่อมีหน้า `/bookings/:id` และมีอีเมลจริงแล้ว (ดูหัวข้อ "⚠️ ปิดอยู่" ใน CLAUDE.md)
- **Performance:** หน้า grid คือหน้าที่ PRD 8 วัด LCP — เอา `scripts/perf/grid_lcp.py` กลับจาก git มาวัด · บทเรียนเดิมอยู่ใน CLAUDE.md หัวข้อ "ประสิทธิภาพหน้า grid" (eager route, prefetch, defer ปฏิทินและคิวรอ, ภาษาอังกฤษไม่อยู่ใน bundle แรก)

## ลำดับงาน (แต่ละขั้น = 1 PR · ✅ = merge แล้ว)

- [ ] **0. เจ้าของตอบ B1–B8** (อย่างน้อย B1, B2, B6, B7 ก่อนเริ่มขั้น 2)
- [ ] **1. ออกแบบ (Design artifact)** mobile-first ตาม brand badPaka: ค้นหาสนาม · หน้าสนาม + ตารางว่าง · สรุปการจอง · จ่ายเงิน (QR + นับถอยหลัง + อัปโหลดสลิป) · การจองของฉัน + ยกเลิก · คิวรอ · สมัคร/เข้าสู่ระบบ (อีเมล + LINE) · ยอมรับนโยบาย · บัญชี/ลบบัญชี · หน้าตอนเปิดใน LINE → **รออนุมัติ**
- [ ] **R. ย้ายโฟลเดอร์** ตามโครงข้างบน (`backend/` → `api/`, `frontend/` → `venue/web/`) + `shared/` · PR เดียว แตะ CI, docker-compose, Dockerfile, deploy workflow, scripts, CLAUDE.md, verify README — ต้องผ่าน CI ครบและ `local.py` ก่อน merge · ทำก่อนขั้น 2
- [ ] **2. โครงแอป:** project ใหม่ + route + i18n + CI (build/test/format/งบ bundle) + Caddy/compose เสิร์ฟโดเมนแอปผู้จอง + CSP · หน้าแรกเปล่าขึ้นบน local
- [ ] **3. backend สำหรับผู้จอง:** แยก "สมัครผู้จอง" ออกจาก "สร้างสนาม" (B6) · `App:BookerBaseUrl` · test
- [ ] **4. หน้าจอเว็บตาม design** ทีละกลุ่ม (ค้นหา+grid → จอง+จ่าย → การจองของฉัน → คิวรอ → บัญชี) แต่ละกลุ่มมี spec + verify script (ดัดแปลงจากของเดิมใน git) + `fits_a_phone`
- [ ] **5. เปิดแจ้งเตือนผู้จอง:** `TellBookers=true` เมื่อมีอีเมลจริง (B7) · `booker_mail.py`
- [ ] **6. LIFF:** endpoint รับ ID token + หน้าเปิดใน LINE + CSP · ทดสอบกับ LIFF จริง (ต้องมี channel จาก B5)
- [ ] **7. แอปมือถือคนจอง (Expo):** token auth ที่ backend → โครง `booker/mobile` + EAS (build/submit/update) + Sentry → หน้าจอตาม design (ต้องวาด design ของแอปด้วย) · ส่ง store หลังเจ้าของอนุมัติค่าใช้จ่าย (B4)
- [ ] **7b. แอปมือถือสนาม (Expo):** `venue/mobile` ใช้ token auth เดียวกัน · ต้องคุยกับเจ้าของว่าแอปสนามเริ่มจากหน้าไหนก่อน (เช่น ตอนนี้ / ไทม์ไลน์ / เช็กอิน / รับเงิน) และวาด design ก่อน
- [ ] **8. วัด LCP หน้า grid** บนเครื่อง PRD ตาม PRD 8

## ความเสี่ยงที่รู้แล้ว

- bundle แรกของแอปผู้จองโตเร็วถ้าใส่ Material form ตรงในหน้า grid (เคยโต 131 kB, ดู US-27 ใน CLAUDE.md)
- CSP ที่ hash ไม่ตรง ทำให้สไตล์หายเงียบ ๆ (#131) — `doors.py` ตรวจไว้ ต้องมีตัวตรวจแบบเดียวกันในแอปผู้จอง
- เปิดสมัครโดยไม่แยกสิทธิ์สร้างสนาม = ใครก็สมัครสนามได้ (เหตุที่ปิดไว้ใน D17)
- ยังไม่มีอีเมลจริง = ผู้จองยืนยันอีเมลไม่ได้ = จองไม่ได้ (ยกเว้นบัญชี LINE ที่มีเบอร์โทร)
- เครื่อง PRD (`webapp.baanpaka.com`) ยังติดตั้งไม่เสร็จ — รอสิทธิ์ SSH จากเพื่อน (ดูหน้า "ขึ้นเว็บ badPaka บน webapp.baanpaka.com")

## บันทึกการทำงาน

รูปแบบ: `YYYY-MM-DD · ขั้น · สิ่งที่ทำ / ที่เจอ / ที่ตัดสิน`

- 2026-10-04 · — · เจ้าของสั่งเตรียมแอปผู้จองสามช่องทาง (เว็บ / มือถือ / LIFF) · เขียนแผนนี้เป็น handoff · สำรวจ: API ผู้จองครบ, หน้าเดิมอยู่ใน `13cf827^`, `TellBookers` ปิด, `OpenSignUp` ปิด, ลิงก์ในอีเมลชี้ `App:BaseUrl/bookings/{id}` · รอเจ้าของตอบ B1–B8
- 2026-10-04 · — · เจ้าของตัดสิน: ระบบมี 5 ตัว (API · เว็บสนาม · แอปสนาม · เว็บคนจอง · แอปคนจอง) · แอปมือถือเป็น native (React Native + Expo) ไม่เอา WebView · ยอมรับโครงโฟลเดอร์ `api/` `venue/{web,mobile}` `booker/{web,mobile}` `shared/` · เพิ่มขั้น R (ย้ายโฟลเดอร์) และ 7b (แอปสนาม)
