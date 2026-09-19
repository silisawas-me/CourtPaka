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

1. build image ของ API และ web แล้ว push ขึ้น GitHub Container Registry โดยติด tag เป็น commit SHA
2. สร้าง **migration bundle** (ไฟล์ executable ที่รัน migration ได้โดยไม่ต้องมี .NET SDK บนเครื่อง)
3. ssh เข้าเครื่อง: ดึง image ใหม่ → รัน migration bundle → `docker compose up -d --wait`
4. ตรวจ `/api/health/ready` ถ้าไม่ผ่าน ให้ย้อนกลับไปใช้ image เดิม

## ยังไม่มีอีเมลจริง

นอก Development ระบบใช้ `UndeliveredEmailSender` คือ **ไม่ส่งอีเมลออกจริง** และไม่เขียนเนื้อหา (ซึ่งมีลิงก์ยืนยันตัวตนกับลิงก์คำเชิญที่เป็น token ใช้ครั้งเดียว) ลง log — log จะบอกแค่ว่ามีอีเมลตกหล่นฉบับไหน ฉะนั้นบน UAT/PRD flow ยืนยันอีเมลและคำเชิญยังทำไม่จบ จนกว่าจะต่อผู้ให้บริการอีเมลจริงใน US-06

## Rollback

```bash
cd /opt/courtpaka
# แก้ API_IMAGE / WEB_IMAGE ใน .env ให้ชี้ไปที่ commit ก่อนหน้า แล้ว
docker compose pull && docker compose up -d --wait
```

> **ยังไม่ได้ทดสอบกับเครื่องจริง** ไฟล์ชุดนี้เขียนไว้ล่วงหน้า และจะรันจริงครั้งแรกตอนตั้งเครื่อง UAT
