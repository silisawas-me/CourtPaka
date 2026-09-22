# Deploy

เครื่อง UAT และ PRD รันด้วย `deploy/docker-compose.yml` ไฟล์เดียวกัน ต่างกันแค่ค่าใน `.env` ของแต่ละเครื่อง

## เตรียมเครื่องครั้งแรก (Ubuntu Server 24.04)

```bash
# 1. ผู้ใช้สำหรับ deploy และ Docker
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /home/deploy/.ssh && sudo chmod 700 /home/deploy/.ssh
# วาง public key ของ deploy key ลงใน /home/deploy/.ssh/authorized_keys

# 2. ไฟล์ของแอป
sudo -u deploy mkdir -p /opt/courtpaka
# คัดลอก deploy/docker-compose.yml ไปที่ /opt/courtpaka/docker-compose.yml
# คัดลอก deploy/.env.example ไปเป็น /opt/courtpaka/.env แล้วเติมค่าให้ครบ
# คัดลอก deploy/backup.sh และ deploy/restore-check.sh ไปที่ /opt/courtpaka/ ด้วย (ดูหัวข้อ Backup)
chmod +x /opt/courtpaka/backup.sh /opt/courtpaka/restore-check.sh
chmod 600 /opt/courtpaka/.env

# 3. Firewall และการอัปเดตความปลอดภัย (PRD 9.3)
sudo ufw allow 22,80,443/tcp && sudo ufw enable
sudo apt install -y fail2ban unattended-upgrades
```

## Secrets ที่ต้องตั้งใน GitHub (Settings → Environments)

สร้าง environment ชื่อ `uat` และ `prd` โดย `prd` ให้เปิด **Required reviewers** เพื่อให้ต้องกดอนุมัติก่อน deploy

| Secret | คืออะไร |
|---|---|
| `SSH_HOST` | IP หรือโดเมนของเครื่อง |
| `SSH_USER` | `deploy` |
| `SSH_KEY` | private key ของ deploy key |
| `SSH_PORT` | ปกติ `22` |
| `SSH_HOST_FINGERPRINT` | ลายนิ้วมือ host key ของเครื่อง ถ้าไม่ใส่ workflow จะยอมรับ host key อะไรก็ได้ |

เอาค่า `SSH_HOST_FINGERPRINT` มาจากเครื่องโดยตรง (อย่าเอาจาก `ssh-keyscan` ผ่านเน็ต):

```bash
# รันบนเครื่อง server แล้วเอาส่วน SHA256:... ไปใส่ใน secret
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub | awk '{print $2}'
```

## ขั้นตอนที่ workflow ทำ

ถ้า environment นั้นยังไม่มี `SSH_HOST` (เช่นตอนนี้ที่ยังไม่มีเครื่อง UAT) ขั้นตอน deploy จะข้ามไปเฉย ๆ พร้อมขึ้น notice — build กับ push image ยังทำตามปกติ

1. build image ของ API และ web แล้ว push ขึ้น GitHub Container Registry โดยติด tag เป็น commit SHA
2. สร้าง **migration bundle** (ไฟล์ executable ที่รัน migration ได้โดยไม่ต้องมี .NET SDK บนเครื่อง)
3. ssh เข้าเครื่อง: ดึง image ใหม่ → รัน migration bundle → `docker compose up -d --wait`
4. ตรวจ `/api/health/ready` ถ้าไม่ผ่าน ให้ย้อนกลับไปใช้ image เดิม

## ยังไม่มีอีเมลจริง

นอก Development ระบบใช้ `UndeliveredEmailSender` คือ **ไม่ส่งอีเมลออกจริง** และไม่เขียนเนื้อหา (ซึ่งมีลิงก์ยืนยันตัวตนกับลิงก์คำเชิญที่เป็น token ใช้ครั้งเดียว) ลง log — log จะบอกแค่ว่ามีอีเมลตกหล่นฉบับไหน ฉะนั้นบน UAT/PRD flow ยืนยันอีเมลและคำเชิญยังทำไม่จบ จนกว่าจะต่อผู้ให้บริการอีเมลจริงใน US-06

## เข้าสู่ระบบด้วย LINE (US-01)

ตั้ง `LINE_CHANNEL_ID` / `LINE_CHANNEL_SECRET` ใน `.env` ของเครื่อง จาก channel ใน LINE Developers
และต้องลงทะเบียน callback URL เป็น `${APP_BASE_URL}/api/auth/line/callback` ที่ฝั่ง LINE ด้วย
ถ้าเว้นว่างทั้งคู่ ระบบจะไม่แสดงปุ่ม LINE และใช้อีเมลอย่างเดียว (deploy ได้ตามปกติ)

ขอ scope `email` จาก LINE ได้เฉพาะ channel ที่ขออนุมัติแล้ว ถ้าไม่ได้ อีเมลจะเป็น null
บัญชีนั้นจะใช้เบอร์โทรเป็นช่องทางติดต่อและ **ไม่ได้รับอีเมลแจ้งเตือน** จนกว่าจะเพิ่มอีเมลเอง

> `App__Line__UseDevelopmentFake` เป็นของ local เท่านั้น (ต้องเป็น Development host ด้วย)
> เพราะหน้า LINE ปลอมของมันให้ใครก็ได้เข้าเป็นใครก็ได้

## ไฟล์สลิป (US-04)

รูปสลิปเก็บใน volume `api-slips` ที่ path `/slips` ในคอนเทนเนอร์ของ API **ไม่ได้อยู่ในฐานข้อมูล**
ฉะนั้นการ backup ต้องครอบคลุมทั้ง `db-data` และ `api-slips` ไม่งั้นกู้ฐานข้อมูลกลับมาแล้วจะได้การจองที่ไม่มีสลิป

> **ชั่วคราว** PRD หัวข้อ 9.1 วางไว้ว่าจะย้ายไป object storage (S3-compatible) ก่อนใช้งานกับสนามจริง
> ตอนนี้เก็บบนดิสก์ของเครื่องเพื่อให้ flow ทำงานได้ก่อน

## Backup (PRD 8)

`backup.sh` ดัมป์ฐานข้อมูลผ่าน container `db` ลง `deploy/backups/` (เปลี่ยนได้ด้วย `BACKUP_DIR`)
เป็นไฟล์ custom-format แล้วลบไฟล์ที่เก่ากว่า `KEEP_DAYS` (ค่าเริ่มต้น 14 วันตาม PRD 8)
ตั้งให้รันทุกวันตอนตีสองด้วย crontab ของ root บนเครื่อง:

```cron
0 2 * * * cd /opt/courtpaka && ./backup.sh >> /var/log/courtpaka-backup.log 2>&1
```

**ไฟล์สลิปไม่ได้อยู่ในดัมป์** (อยู่ใน volume `api-slips` — ดูหัวข้อถัดไป) ฉะนั้นชุด backup ต้องมีทั้งสองอย่าง
กู้คืนแต่ฐานข้อมูลจะได้การจองที่ไม่มีหลักฐานการชำระเงิน:

```cron
15 2 * * * docker run --rm -v courtpaka_api-slips:/slips -v /opt/courtpaka/backups:/out alpine tar czf /out/slips-$(date -u +\%Y\%m\%d).tar.gz -C /slips .
```

`restore-check.sh` คือการพิสูจน์ว่าไฟล์นั้นกู้คืนได้จริง — PRD 8 ให้ทำเดือนละครั้งและ**บันทึกผลไว้**
มันกู้ลงฐานข้อมูลชั่วคราวข้าง ๆ ตัวจริง เทียบจำนวนแถวกับของจริง แล้วลบทิ้ง (อ่านตัวจริงอย่างเดียว)
ไฟล์ที่ไม่ครบถูกปฏิเสธด้วย exit 1 — ทั้งไฟล์ที่ขาดกลางทาง (pg_restore ไม่ยอม) และดัมป์ที่ไม่มีข้อมูลเลย
(archive ไม่มี TABLE DATA) · **บนเครื่องที่ยังไม่มีข้อมูล** มันจะบอกตรง ๆ ว่าพิสูจน์ได้แค่ว่า archive ครบและ schema กลับมา
ไม่ได้พิสูจน์ว่าแถวถูก ให้รันอีกครั้งเมื่อมีข้อมูลจริง:

```bash
cd /opt/courtpaka && ./restore-check.sh
```

ทั้งสองสคริปต์อ่าน `POSTGRES_DB` / `POSTGRES_USER` จาก `.env` ที่อยู่**ข้าง ๆ ตัวมันเอง** และสั่ง `docker compose` ในโฟลเดอร์นั้น
ฉะนั้นต้องวางไว้ที่ `/opt/courtpaka/` ข้าง `docker-compose.yml` (ดูขั้นตอนเตรียมเครื่อง) ไม่ใช่รันจาก repo
(ตั้ง `COMPOSE` ได้ถ้าต้องชี้ไปที่ไฟล์อื่น เช่นตอนทดสอบกับ stack local)

ดัมป์ลง `/opt/courtpaka/backups/` (โหมด 700, ไฟล์ 600 เพราะมีอีเมล/เบอร์โทร/เลขผู้เสียภาษีของทุกคน)
ถ้าจะส่งออกนอกเครื่อง ให้ rsync/rclone โฟลเดอร์นี้ทั้งก้อน — PRD 8 ต้องการสำเนานอกเครื่องด้วย

## Rollback

```bash
cd /opt/courtpaka
# แก้ API_IMAGE / WEB_IMAGE ใน .env ให้ชี้ไปที่ commit ก่อนหน้า แล้ว
docker compose pull && docker compose up -d --wait
```

> **ยังไม่ได้ทดสอบกับเครื่องจริง** ไฟล์ชุดนี้เขียนไว้ล่วงหน้า และจะรันจริงครั้งแรกตอนตั้งเครื่อง UAT
