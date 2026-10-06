<p align="center"><img src="WOG_Trainer/logo.png" width="128" alt="WOG Helper"></p>

# WOG Helper

เครื่องมือช่วยเล่นสำหรับ **War of Genesis Idle Loot** (Steam)

## ฟีเจอร์

**Loot Log** (อ่านอย่างเดียว ไม่ส่งอะไรไป server)
- รายการของที่ได้ล่าสุด ชื่อเป็นสีตามเกรด
- ยอดรวมแต่ละไอเทม และได้ต่อชั่วโมง
- Gold / EXP ต่อชั่วโมง
- Export CSV

**Automation** (เรียกฟังก์ชันของเกมเอง เหมือนกดปุ่มเอง)
- **Auto Equip:** ใส่ของที่ดีที่สุดจากกระเป๋า โดยดู stat หลัก → จำนวนออปชันสุ่ม → เกรด ข้ามของที่ล็อกไว้
- **Auto Sort:** จัดกระเป๋าทุก N วินาที และเคลียร์เครื่องหมาย New
- **Auto Training:** อัป Training ช่องที่ถูกที่สุดเมื่อ gold พอ โดยเก็บ gold ไว้ตาม Reserve ที่ตั้ง

ย่อหน้าต่างแล้วจะไปอยู่ที่ถาดขวาล่าง ค่าที่ตั้งไว้จำใน `settings.json`

## วิธีใช้

1. เปิดเกมให้เข้าหน้า idle
2. โหลด zip จาก [Releases](../../releases) แตกไฟล์ให้ `WOG Helper.exe` กับ `WOGHook.dll` อยู่โฟลเดอร์เดียวกัน
3. เปิด `WOG Helper.exe` แล้วกด **Connect**
4. ไปแท็บ **Automation** แล้วติ๊กฟีเจอร์ที่ต้องการ

ปิดเกมแล้วเปิดใหม่ต้องกด Connect ใหม่ทุกครั้ง

## ข้อจำกัด

การต่อสู้ทั้งหมดคำนวณที่ server (stage = server battle tick, dungeon = `gs_dungeon_battle`)
ดังนั้น heal / ดาเมจ / ความเร็ว / ตีทีเดียวตาย **ทำในเครื่องไม่ได้** แก้ได้แค่ภาพบนจอ

## ⚠️ คำเตือน

- เกมนี้ออนไลน์ การใช้โปรแกรมช่วยเล่นอาจผิดข้อตกลงของเกม ใช้ด้วยความเสี่ยงของคุณเอง
- Auto Equip ไม่ได้ดูว่าออปชันสุ่มเป็น stat อะไร **ล็อกของที่อยากเก็บไว้ก่อนเปิดใช้**

## Build

ต้องมี .NET 10 SDK และ Visual Studio (C++ x64 build tools) หรือ MinGW-w64

```
build.bat
```

ไฟล์ที่ได้จะอยู่ใน `TrainerBuild\`

## โครงสร้าง

```
WOGHook\      C++ DLL ที่ inject เข้าเกม: detour NTSManager.ManagedUpdate (main thread)
              แล้วรัน JS ใน PuerTS ผ่าน puerts.dll Eval
WOG_Trainer\  C# WinForms app (WOG Helper)
js\           loot_log.js, auto.js (embed เข้า exe ตอน build)
```

เวลาเกมอัปเดต ถ้าขึ้น "Unsupported game version" แปลว่า prologue ของ `ManagedUpdate` เปลี่ยน ต้องแก้ hook
