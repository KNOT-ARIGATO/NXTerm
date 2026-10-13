<p align="center"><img src="docs/icon.png" width="96" alt="NXTerm icon"></p>

# NXTerm: โปรแกรม Terminal สำหรับ Windows

โปรแกรม terminal สำหรับ Windows ใช้ต่อ **Serial (COM)**, **SSH**, **Telnet** ได้ในหน้าต่างเดียว
มีปุ่มคำสั่งด่วน จำการเชื่อมต่อไว้กดเข้าได้ทันที และอัปเดตตัวเองจาก GitHub ได้
เขียนด้วย C# (.NET 10, WinForms)

## ดาวน์โหลด

ดาวน์โหลดจากหน้า **[Releases](../../releases/latest)** แล้วดับเบิลคลิกเปิดได้เลย ไม่มีตัวติดตั้ง มีให้เลือก 2 ไฟล์ ทำงานเหมือนกันทุกอย่าง:

| ไฟล์ | ขนาด | เครื่องต้องมี | เหมาะกับ |
|---|---|---|---|
| **`NXTerm-standalone.exe`** | ประมาณ 52 MB | ไม่ต้องติดตั้งอะไรเพิ่ม | คนส่วนใหญ่ หรือเครื่องที่ไม่มีสิทธิ์ติดตั้งโปรแกรม |
| `NXTerm.exe` | ประมาณ 7 MB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | เครื่องที่มี .NET 10 อยู่แล้ว อยากได้ไฟล์เล็ก |

ทั้งสองแบบอัปเดตตัวเองได้ และจะอัปเดตเป็นแบบเดียวกับที่ใช้อยู่เสมอ

### ความต้องการของระบบ

- **Windows 10 (เวอร์ชัน 1607 ขึ้นไป) หรือ Windows 11 แบบ 64-bit**
- Windows 11 บนเครื่อง ARM ก็ใช้ได้ (Windows จำลองการทำงานให้)
- ยังไม่มีไฟล์สำหรับ Windows แบบ 32-bit
- **RTSP viewer** ใช้ตัวถอดรหัส H.264 ที่มากับ Windows ถ้าเป็น Windows รุ่น "N" ต้องติดตั้ง Media Feature Pack จาก Microsoft เพิ่ม
- เปิดครั้งแรก Windows อาจขึ้นเตือน "Windows protected your PC" เพราะไฟล์ยังไม่ได้เซ็นรับรอง ให้กด **More info → Run anyway**

## เริ่มใช้งานใน 1 นาที

1. เปิด NXTerm แล้วหน้าต่าง **New connection** จะขึ้นมา
2. กรอกข้อมูลการเชื่อมต่อ เช่น SSH (host, user) หรือ Serial (COM port, baud)
3. กด **Save...** ทางซ้ายเพื่อตั้งชื่อเก็บไว้ ติ๊ก "Remember password" ถ้าไม่อยากพิมพ์รหัสทุกครั้ง
4. กด **Connect**

ครั้งต่อไปเปิดเมนู **Sessions** บนแถบเมนู แล้วเลือกชื่อที่บันทึกไว้ คลิกเดียวก็ต่อได้ทันที

## ฟีเจอร์

| หมวด | รายละเอียด |
|---|---|
| การเชื่อมต่อ | **Serial (COM)**, **SSH2**, **Telnet**, Raw TCP |
| **Session ที่บันทึกไว้** | เก็บค่า COM port / SSH ไว้เป็นชื่อ แล้วต่อได้ในคลิกเดียว จำรหัสผ่านได้ (เข้ารหัสด้วย Windows) และสร้างไอคอนลัดบน Desktop ได้ |
| **คำสั่งด่วน** | ปุ่มคำสั่งจัดเป็นกลุ่มตามแท็บ อยู่ใต้ terminal ในหน้าต่างเดียวกัน หรือแยกเป็นหน้าต่างลอยก็ได้ |
| **เครื่องมือเสริม: RTSP viewer** | ดูภาพสดจากกล้อง IP หรือ Raspberry Pi ในหน้าต่างแยก เปิดใช้เมื่อต้องการ ดู[รายละเอียด](#เครื่องมือเสริม-rtsp-viewer) |
| **อัปเดตอัตโนมัติ** | เช็กเวอร์ชันใหม่จาก GitHub Releases กดปุ่มเดียวก็อัปเดตแล้วเปิดโปรแกรมใหม่ให้ |
| Serial | เลือก baud, data bits, parity, stop bits, flow control ได้ หน่วงเวลาส่งได้ ส่ง Break ได้ และแสดงชื่ออุปกรณ์ เช่น `COM3: USB-SERIAL CH340` |
| SSH | ล็อกอินด้วย password, keyboard-interactive หรือ private key ตรวจ host key เหมือน `known_hosts` และส่ง/รับไฟล์ด้วย SCP ได้ |
| การแสดงผล | VT100/VT220/xterm, สี 16/256/true color, ใช้กับ vim, htop, mc, top, less ได้ |
| ภาษา | UTF-8, **ภาษาไทย (สระบน/ล่าง/วรรณยุกต์)**, TIS-620, ภาษาจีน/ญี่ปุ่น/เกาหลี |
| **ไฮไลต์คำ** | ลงสีคำสำคัญบนหน้าจอให้อัตโนมัติ: ผ่าน/สำเร็จเป็นสีเขียว, error/fail เป็นสีแดง, warning เป็นสีเหลือง ปรับได้ที่ **Setup → Highlight keywords...** |
| Log | บันทึกเป็น text ล้วน ใส่ timestamp ทุกบรรทัด หรือเก็บเป็น binary |
| โอนไฟล์ | ส่งไฟล์ text/binary, ลากไฟล์มาวางเพื่อส่ง, XMODEM, SSH SCP |
| Macro | สคริปต์สั่งงานอัตโนมัติ (ไฟล์ `.ttl`) ดู[รายการคำสั่ง](#macro) |

## Session ที่บันทึกไว้

- **บันทึก:**
  - ในหน้าต่าง New connection (Alt+N) กด **Save...**
  - หรือตอนกำลังต่ออยู่ เลือกเมนู **File → Save connection as session...**
- **ต่อ:**
  - เมนู **Sessions** บนแถบเมนู
  - หรือดับเบิลคลิกชื่อในหน้าต่าง New connection
- **แก้ไข:** เลือก session แล้วแก้ค่าแล้วกด Connect โปรแกรมจะถามว่าจะบันทึกการเปลี่ยนแปลงไหม
- **Rename / Delete:** อยู่ใต้รายการ session
- **Desktop shortcut:** สร้างไอคอน "NXTerm - ชื่อ" บน Desktop ดับเบิลคลิกแล้วต่อทันที (เปิดด้วย `NXTerm.exe "/S=ชื่อ"`)
- **รหัสผ่าน:** เข้ารหัสด้วย Windows DPAPI อ่านได้เฉพาะบัญชี Windows ของคุณบนเครื่องนี้ ถ้าเอาไฟล์ค่าตั้งไปเครื่องอื่น ระบบจะถามรหัสใหม่

## คำสั่งด่วน (Quick commands)

ปุ่มคำสั่งอยู่ใต้ terminal ในหน้าต่างเดียวกัน กด **Alt+K** เพื่อซ่อน/แสดง

- **แท็บ = กลุ่ม** ส่วน **ปุ่ม = คำสั่ง** กดแล้วส่งเข้า terminal ทันที
- **ตำแหน่ง:**
  - **Separate window** แยกเป็นหน้าต่างลอย
  - **Dock in main window** กลับมาอยู่ใต้ terminal
  - ลากเส้นแบ่งเพื่อปรับความสูงได้
- **เพิ่ม:**
  - `+ Group` เพื่อเพิ่มแท็บ
  - `+ Command` หรือปุ่ม `+` ท้ายแท็บเพื่อเพิ่มคำสั่ง
- **แก้ไข:**
  - คลิกขวาที่ปุ่มเพื่อแก้ไข (Edit / Duplicate / Move / Delete)
  - คลิกขวาที่แท็บเพื่อจัดการกลุ่ม
- **ตัวเลือกของแต่ละคำสั่ง:**
  - ส่งหลายบรรทัด และหน่วงเวลาระหว่างบรรทัดได้
  - เลือกได้ว่าจะกด Enter ให้หรือไม่
  - ใช้รหัสพิเศษได้ เช่น `\x03` = Ctrl+C
  - **ถามก่อนส่ง** สำหรับคำสั่งอันตราย
  - เปลี่ยนสีปุ่มได้
  - ชนิด **Macro** (รอ prompt ก่อนส่งคำสั่งถัดไป)
- **Alt+1..9** ส่งคำสั่งที่ 1–9 ของแท็บปัจจุบัน
- **Export / Import** เป็นไฟล์ JSON เพื่อแชร์ให้ทีม

## เครื่องมือเสริม: RTSP viewer

เครื่องมือเสริมจะไม่แสดงจนกว่าจะเปิดใช้ เพื่อไม่ให้รกหน้าจอ

1. ติ๊ก **Setup → Extra tools → RTSP viewer** ปุ่ม **RTSP** จะขึ้นที่มุมขวาของแถบเมนู (เอาติ๊กออกปุ่มก็หายไป)
2. กดปุ่ม **RTSP** จะเปิดหน้าต่างดูภาพแยกออกมา เปิดได้หลายหน้าต่างพร้อมกัน
3. ใส่ address ของกล้อง เช่น `rtsp://user:password@192.168.1.10:554/stream1` แล้วกด **Connect**

- **Snapshot** บันทึกภาพปัจจุบันเป็น PNG หรือ JPEG
- **ดับเบิลคลิกที่ภาพ** หรือกด F11 เพื่อดูเต็มจอ (กด Esc เพื่อออก)
- **Always on top** ให้หน้าต่างภาพลอยอยู่เหนือโปรแกรมอื่น
- ถ้าสัญญาณหลุด โปรแกรมจะต่อใหม่เองทุก 3 วินาที
- address ที่เคยใช้จะถูกจำไว้ และเข้ารหัสด้วย Windows เพราะมักมีรหัสผ่านกล้องอยู่ด้วย

**ข้อจำกัด**
- รองรับวิดีโอ **H.264** ผ่าน RTSP แบบ TCP ถ้ากล้องตั้งเป็น H.265 ให้เปลี่ยนเป็น H.264 หรือใช้ sub-stream ที่เป็น H.264
- ยังไม่มีเสียง
- ใช้ตัวถอดรหัส H.264 ที่มากับ Windows จึงไม่ต้องติดตั้ง VLC หรือ FFmpeg เพิ่ม

## อัปเดตโปรแกรม

- โปรแกรมเช็กเวอร์ชันใหม่จาก GitHub วันละครั้ง ถ้ามีจะขึ้นลิงก์ **Update available** ที่มุมขวาล่าง
- กดลิงก์นั้นแล้วกด **Update now** โปรแกรมจะดาวน์โหลด ตรวจไฟล์ (ขนาด + SHA-256) เปลี่ยนเป็นเวอร์ชันใหม่ แล้วเปิดใหม่ให้เอง ถ้าต่อ session อยู่ก็จะต่อกลับให้ด้วย
- เช็กเองได้ที่ **Help → Check for updates...** หรือปิดการเช็กอัตโนมัติที่ **Help → Check automatically**
- ถ้าวาง NXTerm.exe ไว้ใน `Program Files` ตัวโปรแกรมจะเขียนทับตัวเองไม่ได้ ให้ดาวน์โหลดจากหน้า Release แทน (ปุ่ม Release page)

## ปุ่มลัด

| ปุ่ม | ทำอะไร |
|---|---|
| Alt+N | เชื่อมต่อใหม่ / จัดการ session |
| Alt+I | ตัดการเชื่อมต่อ |
| Alt+D | เปิดหน้าต่างใหม่ด้วยการเชื่อมต่อเดิม |
| Alt+K | ซ่อน/แสดงคำสั่งด่วน |
| Alt+1 ถึง Alt+9 | ส่งคำสั่งด่วนลำดับที่ 1–9 ของแท็บที่เลือกอยู่ |
| Alt+C / Ctrl+Shift+C / Ctrl+Insert | คัดลอก |
| Alt+V / Ctrl+Shift+V / Shift+Insert / คลิกขวา | วาง |
| Alt+R | วางแล้วกด Enter |
| Alt+B | ส่ง Break |
| Alt+M | รัน macro |
| Shift+PageUp / PageDown, ล้อเมาส์ | เลื่อนดูข้อความเก่า |
| ดับเบิลคลิก | เลือกทั้งคำ |

## Command line

```
NXTerm.exe "/S=Raspberry Pi"            (เปิด session ที่บันทึกไว้)
NXTerm.exe 192.168.1.10 /ssh /user=pi
NXTerm.exe admin@server:2222 /ssh /keyfile=C:\keys\id_ed25519
NXTerm.exe router.local /telnet
NXTerm.exe /C=3 /BAUD=115200            (COM3 ที่ 115200)
NXTerm.exe /C=3 /BAUD=9600 /M=boot.ttl  (เชื่อมต่อแล้วรัน macro)
NXTerm.exe host /L=C:\logs\session.log  (เริ่มบันทึก log ทันที)
```

ไฟล์ค่าตั้งอยู่ที่ `%APPDATA%\NXTerm\` (`NXTerm.json` สำหรับค่าตั้งและ session ส่วน `QuickCommands.json` สำหรับคำสั่งด่วน)

## Macro

Macro คือสคริปต์ที่สั่งให้โปรแกรมพิมพ์คำสั่งและรอคำตอบแทนเรา บันทึกเป็นไฟล์ `.ttl` ตัวอย่างเช่น ล็อกอินบอร์ดผ่าน Serial:

```
; login.ttl
connect '/C=3 /BAUD=115200'
timeout = 10
sendln ''
wait 'login:'
sendln 'root'
wait 'Password:'
passwordbox 'Enter password' 'Login'
sendln inputstr
wait '# ' 'incorrect'
if result = 2 then
  messagebox 'Wrong password' 'Login'
  end
endif
logopen 'C:\logs\board.log' 0 0
for i 1 3
  sendln 'cat /proc/uptime'
  wait '# '
  pause 1
next
logclose
```

| หมวด | คำสั่ง |
|---|---|
| รับ-ส่ง | `send`, `sendln`, `wait`, `waitln`, `recvln`, `waitregex`, `flushrecv`, `sendbreak` |
| เชื่อมต่อ | `connect`, `disconnect`, `testlink` |
| ควบคุม | `if ... then / elseif / else / endif`, `if <เงื่อนไข> <คำสั่ง>`, `while / endwhile`, `for / next`, `break`, `goto`, `call / return`, `end` |
| เวลา | `pause` (วินาที), `mpause` (มิลลิวินาที), `gettime`, `getdate` |
| Log | `logopen`, `logclose`, `logwrite` |
| หน้าต่าง | `messagebox`, `yesnobox`, `inputbox`, `passwordbox`, `statusbox`, `dispstr`, `clearscreen`, `settitle`, `beep` |
| ข้อความ | `int2str`, `str2int`, `strconcat`, `strcopy`, `strlen`, `strcompare`, `strscan` |

ตัวแปรพิเศษ:

- `result`: ผลของ `wait` (ลำดับของคำที่เจอ หรือ 0 ถ้าหมดเวลา)
- `inputstr`: ข้อความจาก `inputbox`, `waitln` และ `recvln`
- `timeout` / `mtimeout`: เวลารอสูงสุดของ `wait` (0 = รอไม่จำกัด)
- `matchstr`, `groupmatchstr1..9`: ผลของ `waitregex`

## สำหรับนักพัฒนา

```bash
dotnet run --project NXTerm
```

```bash
dotnet run --project NXTerm.Tests
```

### ออกเวอร์ชันใหม่บน GitHub

1. แก้โค้ดแล้ว push ขึ้น GitHub (CI จะ build และรันเทสต์ให้ทุกครั้ง)
2. สร้าง tag เวอร์ชันแล้ว push:

```bash
git tag v1.2.0
```

```bash
git push origin v1.2.0
```

3. GitHub Actions (`.github/workflows/release.yml`) จะทำต่อเองทั้งหมด:
   - รันเทสต์
   - build `NXTerm.exe` และ `NXTerm-standalone.exe` ใส่เวอร์ชันจาก tag (build บนเครื่องเองได้ด้วย `tools/publish.ps1`)
   - ใส่ชื่อ repo ไว้ในตัวโปรแกรมเพื่อใช้เช็กอัปเดต
   - สร้างหน้า Release พร้อมแนบไฟล์
4. โปรแกรมที่ผู้ใช้ติดตั้งไว้จะเห็นเวอร์ชันใหม่เอง

tag ที่มีขีด เช่น `v1.3.0-beta.1` จะเป็น pre-release ซึ่งโปรแกรมจะไม่เสนอให้อัปเดต

ถ้า build เองบนเครื่อง โปรแกรมจะไม่รู้ว่าต้องเช็กอัปเดตจาก repo ไหน ให้ใส่ `<GitHubRepo>owner/repo</GitHubRepo>` ใน `NXTerm/NXTerm.csproj` ส่วนไอคอนสร้างใหม่ได้ด้วย `tools/make-icon.ps1`

### โครงสร้างโปรเจกต์

```
NXTerm/
  Program.cs, MainForm.cs        หน้าต่างหลัก เมนู และการเชื่อมทุกส่วนเข้าด้วยกัน
  AppSettings.cs                 ค่าตั้ง (JSON)
  Emulation/                     ตัวจำลองหน้าจอ VT/xterm และส่วนวาดหน้าจอ
  Connections/                   Serial, Telnet/Raw TCP, SSH, session ที่บันทึกไว้
  QuickCommands/                 แผงคำสั่งด่วน (อยู่ในหน้าต่างหลักหรือแยกหน้าต่าง)
  Updates/                       เช็กและติดตั้งอัปเดตจาก GitHub Releases
  Rtsp/                          เครื่องมือเสริม RTSP viewer (ตัวรับ RTSP, ตัวถอดรหัส H.264, หน้าต่างแสดงภาพ)
  Macro/, Transfer/, Logging/    Macro, XMODEM, ระบบบันทึก log
  Dialogs/                       หน้าต่างตั้งค่าต่าง ๆ
  Resources/NXTerm.ico            ไอคอนโปรแกรม
NXTerm.Tests/                     ชุดทดสอบ
tools/make-icon.ps1              สคริปต์สร้างไอคอน
.github/workflows/               CI และ Release อัตโนมัติ
```

## สัญญาอนุญาต

- โค้ดของ NXTerm เผยแพร่ภายใต้ [MIT License](LICENSE) ใช้ แก้ไข และแจกต่อได้ฟรี รวมถึงใช้เชิงพาณิชย์
- ไลบรารีที่รวมอยู่ใน `NXTerm.exe` (SSH.NET, BouncyCastle, ไลบรารี .NET ของ Microsoft) เป็น MIT ทั้งหมด ประกาศลิขสิทธิ์อยู่ใน [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) และเปิดอ่านในโปรแกรมได้ที่ **Help → Open-source licenses**
- NXTerm เป็นโปรเจกต์อิสระ เขียนโค้ดขึ้นใหม่ทั้งหมด

## สิ่งที่ยังไม่มี

- ZMODEM, Kermit (มี XMODEM กับ SCP)
- SSH port forwarding, SSH agent และ X11 forwarding
- macro ยังไม่มีคำสั่งจัดการไฟล์ (`fileopen`, `fileread` ฯลฯ) และ `include`
