# คำศัพท์ที่ใช้ได้ (token + primitive)

ทุกอย่างในนี้มีอยู่แล้ว ไม่ต้องสร้างใหม่ ถ้าสิ่งที่ต้องการไม่อยู่ในรายการ ให้สงสัยตัวเองก่อนว่าออกแบบผิดทางหรือเปล่า

## สี — `var(--mat-sys-…)`

ธีมสร้างจากเขียวเดิม `#1d7a4c` ด้วย `ng generate @angular/material:theme-color` (ผลลัพธ์อยู่ใน `venue/web/src/_theme-colors.scss` ห้ามแก้มือ) แล้ว **block เดียวใน `styles.scss` เขียนทับทุก token ด้วย palette ของ badPaka** — ค่าจริงอ่านจาก block นั้น ไม่ใช่จาก schematic

| ใช้กับ | token | คู่ข้อความ |
|---|---|---|
| ปุ่ม/ลิงก์หลัก สิ่งที่อยากให้กด | `--mat-sys-primary` | `--mat-sys-on-primary` |
| พื้นที่เน้นแบบนุ่ม (badge เปิดอยู่, ภาษาที่เลือก) | `--mat-sys-secondary-container` | `--mat-sys-on-secondary-container` |
| ไฮไลต์รอง/ตกแต่ง | `--mat-sys-tertiary` · `--mat-sys-tertiary-container` | `--mat-sys-on-tertiary…` |
| ข้อผิดพลาด | `--mat-sys-error` · `--mat-sys-error-container` | `--mat-sys-on-error…` |
| พื้นหลังหน้า | `--mat-sys-surface-container-low` | `--mat-sys-on-surface` |
| การ์ด/แผงที่ลอยขึ้นมา | `--mat-sys-surface-container` → `…-high` → `…-highest` | `--mat-sys-on-surface` |
| ข้อความรอง คำอธิบาย | — | `--mat-sys-on-surface-variant` |
| เส้นคั่น ขอบ | `--mat-sys-outline-variant` (จาง) · `--mat-sys-outline` (ชัด) | — |

ฟอนต์ที่ Material ไม่มีช่องให้: `--font-display` (Bricolage — หัวข้อ ตัวเลขใหญ่) · `--font-mono` (Plex Mono — เวลา ราคาที่เรียงคอลัมน์)

ความลึกใช้ `--mat-sys-level0`…`level5` (ปกติ `level1`–`level2` พอ) มุมใช้ `--mat-sys-corner-small/medium/large/extra-large/full`
ต้องการเฉดกลาง ๆ ให้ผสมจาก token: `color-mix(in srgb, var(--mat-sys-primary) 12%, transparent)`

## ตัวอักษร — `font: var(--mat-sys-…)`

`display-large|medium|small` · `headline-large|medium|small` · `title-large|medium|small` · `body-large|medium|small` · `label-large|medium|small`

ใช้ทั้ง shorthand เสมอ (`font: var(--mat-sys-body-medium);`) เพราะ token รวม family/size/weight/line-height มาให้คู่กันแล้ว
มีแค่น้ำหนัก 400 และ 500 — token ไหนที่แอปใช้อยู่ก็อยู่ในสองค่านี้ ถ้าอยากได้ "หนากว่านี้" ให้เปลี่ยนไปใช้ token ที่ใหญ่ขึ้นแทนการเพิ่มน้ำหนัก

ลำดับที่แอปใช้อยู่: ชื่อแบรนด์/หัวหน้าต้อนรับ = `display-small` · หัวข้อในการ์ด = `title-medium` · เนื้อหา = `body-large` · คำอธิบายรอง = `body-medium` (คู่กับ `.muted`) · ป้าย/ปุ่มเล็ก = `label-large`

## ระยะห่างและโครง — จาก `venue/web/src/styles.scss`

| class | ทำอะไร |
|---|---|
| `.shell` | คอลัมน์กลางหน้า กว้างสุด `--page-width` (64rem) พร้อม gutter — `app.html` ครอบ `router-outlet` ไว้แล้ว หน้าใหม่ไม่ต้องทำเอง |
| `.columns` | สองคอลัมน์เมื่อกว้าง ≥ 60rem นอกนั้นซ้อนกัน |
| `.stack` | grid ตั้ง gap 0.75rem |
| `.row` | flex นอน wrap ได้ gap 0.5rem |
| `.spacer` | ดันของที่เหลือไปท้ายแถว |
| `.entry` | หนึ่งแถวในรายการ พร้อมเส้นคั่นล่าง (ใบสุดท้ายไม่มี) |
| `.badge` / `.badge-on` | ป้ายบอกสถานะ (ไม่ใช้ `mat-chip` เพราะไม่ต้องการ ripple/focus) |
| `.field` | form field เต็มความกว้างการ์ด |
| `.muted` / `.success` / `.form-error` | ข้อความรอง / สำเร็จ / ผิดพลาด |
| `.day-row` · `.hour-pickers` · `.band-row` | แถวฟอร์มของเวลาเปิด-ปิดและราคา |

ตัวแปรระยะ: `--page-gutter` (1rem) · `--page-width` (64rem) · `--stack` (1rem)
ระยะย่อยใช้ทวีคูณของ 0.25rem เท่านั้น (0.25 / 0.5 / 0.75 / 1 / 1.5 / 2 / 3)

## จุดตัดหน้าจอที่แอปใช้อยู่ — ใช้ซ้ำ อย่าตั้งใหม่

| จุดตัด | ใช้ตอน |
|---|---|
| `max-width: 30rem` | ฟอร์มที่ต้องพับเป็นบรรทัดเดียวต่อช่อง (`.day-row`) |
| `max-width: 34rem` | ลิงก์บนบาร์พับเป็นเมนู (`app.scss`) |
| `min-width: 56rem` | หน้าต้อนรับแบ่งสองฝั่ง (`login.page.scss`) |
| `min-width: 60rem` | `.columns` แยกเป็นสองคอลัมน์ |

## Motion

`prefers-reduced-motion: reduce` ต้องตัด transition ทิ้ง · transition ไม่เกิน 200ms และทำเฉพาะ `opacity`/`transform`/`background` ·
ห้าม animation ที่หน่วงการอ่านตอนเข้าหน้า · การรอที่ผู้ใช้ต้องเห็นใช้ `mat-progress-bar` ที่มีอยู่แล้ว ไม่ใช่ spinner ใหม่
