# WhatsApp Lab (MAA-2102)

Proyek latihan untuk belajar sebelum mengimplementasikan MAA-2102 di ERP, hari Senin. / A practice project to learn before implementing MAA-2102 in the ERP on Monday.
Dibuat / created: 14 Nov 2026. **Tidak menyentuh repo ERP dan tidak memakai WhatsApp sungguhan** / it does not touch the ERP repo and does not use real WhatsApp.

## 1. Apa ini / What this is

- ID: Seluruh rangka fitur (tabel, antrean kirim, pengiriman terjadwal, pemicu dari API dan SQL Server, menerima pesan, batas kirim, tombol mati) sudah jalan dengan **klien WhatsApp palsu** (`FakeWhatsAppClient`). Hari Senin, hanya satu kelas yang diisi dengan pustaka sungguhan: `RealWhatsAppClient`.
- EN: The whole skeleton of the feature (tables, outbox, scheduled sending, triggers from an API and SQL Server, receiving, send limits, off switch) already works with a **fake WhatsApp client** (`FakeWhatsAppClient`). On Monday only one class is filled with the real library: `RealWhatsAppClient`.

## 2. Cara kerjanya / How it works

```text
 TRIGGERS                        OUTBOX TABLE                 DISPATCHER (background, every 2 s)
 API     POST /messages  ----+
 SQL     INSERT a row    ----+-> WhatsAppOutboundMessage ->   1. no settings row, or Off? stop
 Hangfire recurring job  ----+   (Status 0 = Pending)         2. linked? daily cap? min seconds?
                                                              3. send through IWhatsAppClient
                                                                 ok     -> Status 1 (Sent)
                                                                 failed -> retry, Status 2 after 3 tries

 Incoming group message  <-----  IWhatsAppClient.MessageReceived
   -> WhatsAppInboundMessage (known groups only, and only when ReceiveEnabled)
```

Kunci desain / design keys:
- Semua pemicu hanya **menulis ke antrean**. Hanya dispatcher yang mengirim, jadi semua pesan melewati batas yang sama. / Every trigger only **writes to the outbox**. Only the dispatcher sends, so every message passes the same limits.
- Seluruh urusan WhatsApp ada di balik **satu antarmuka** (`IWhatsAppClient`). / Everything WhatsApp-specific is behind **one interface**.
- **Off secara bawaan**: tanpa baris di `WhatsAppSettingDbSet`, tidak ada yang dikirim (pola yang sama dengan fitur wajah di ERP). / **Off by default**: no row in `WhatsAppSettingDbSet`, nothing is sent (same pattern as the face feature in the ERP).
- Kegagalan satu pesan tidak menghentikan yang lain (fail-open). / One failing message does not stop the others.

## 3. Peta ke ERP / Map to the ERP

| Lab | Padanan di ERP / ERP counterpart |
|---|---|
| `WhatsAppLab.Domain` (`Entity` dan entitas / and entities) | `BinaryPlate.Domain` (`Entity`: Id, IsDeleted, kolom audit / audit columns) |
| `WhatsAppLab.Application` (`IApplicationDbContext`, use case) | `BinaryPlate.Application` (`IApplicationDbContext`, `UseCases/...`) |
| `WhatsAppLab.Infrastructure` (`LabDbContext`, `Configurations.cs`, `DependencyInjection.cs`) | `BinaryPlate.Infrastructure` (`ApplicationDbContext`, `Persistence/Configurations`, `DependencyInjection.cs`) |
| `WhatsAppLab.Api` (`Program.cs`) | `BinaryPlate.WebAPI` |
| Nama DbSet berakhiran `DbSet` / DbSet names end with `DbSet` | Sama / same |
| `Nullable` dimatikan / disabled | Sama: kolom teks boleh kosong / same: text columns may be empty |
| Hangfire 1.8.23, EF Core 8.0.28 | Versi yang sama dengan ERP / same versions as the ERP |
| `WhatsAppSettingDbSet` tanpa baris = Off | `FaceVerificationSettingDbSet` |
| `WhatsAppGroupDbSet` (nama grup ke ID grup / group name to group ID) | Melengkapi `NotificationSetupForWork.WhatsAppGroupName` yang sudah ada / completes the existing column |

Tabel yang sudah ada di ERP dan tidak ditiru di sini / already in the ERP and not copied here: `AttendanceNotification` dan `NotificationSetupForWork`. Detailnya ada di `Analisis-Tabel-ERP-MAA-2102.md`.

## 4. Cara menjalankan / How to run

Prasyarat / needs: .NET SDK, SQL Server lokal (`localhost`, Windows Authentication).

```text
cd aifm-whatsapp-lab
dotnet build WhatsAppLab.sln
dotnet run --project src\WhatsAppLab.Api --urls http://localhost:5300
```
- Database `WhatsAppLab` dibuat otomatis lewat migration saat program mulai (`Lab:AutoMigrate`). / The database `WhatsAppLab` is created by the migration at startup.
- Swagger (coba semua endpoint / try every endpoint): http://localhost:5300/swagger
- Hangfire (tugas terjadwal / scheduled jobs): http://localhost:5300/hangfire
- Uji menyeluruh otomatis / automatic end-to-end test: `powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1` (database `WhatsAppLab` dihapus dan dibuat ulang / dropped and recreated).

Urutan mencoba sendiri di Swagger / to try it yourself in Swagger:
1. `PUT /settings` dengan `isEnabled: true` (tanpa ini tidak ada yang terkirim / nothing is sent without it).
2. `POST /groups/sync`, lalu `GET /groups`.
3. `POST /messages` dengan `groupName: "IT Batam"`; tunggu beberapa detik; `GET /messages` menunjukkan status Sent.
4. Lihat log API: baris `[FAKE WhatsApp] to ...`.

## 5. Urutan membaca kode / Reading order

| # | Berkas / File | Belajar apa / What to learn |
|---|---|---|
| 1 | `Domain/Entities.cs` | Lima tabel dan artinya / the five tables |
| 2 | `Application/Abstractions.cs` | `IWhatsAppClient`: kontrak yang harus diisi hari Senin / the contract to fill on Monday |
| 3 | `Application/UseCases.cs` | `DispatchDueAsync`: otak pengiriman (Off, batas harian, jeda, coba ulang) / the sending brain |
| 4 | `Infrastructure/Clients.cs` | `FakeWhatsAppClient` dan `RealWhatsAppClient` (yang kosong) / the fake and the empty real one |
| 5 | `Infrastructure/LabDbContext.cs` dan `Configurations.cs` | Cara ERP memetakan tabel / how the ERP maps tables |
| 6 | `Api/Program.cs` dan `Workers.cs` | Endpoint, pekerja latar, tugas Hangfire / endpoints, background worker, Hangfire job |
| 7 | `scripts/smoke-test.ps1` | Satu contoh setiap skenario / one example of every scenario |

## 6. Daftar kerja hari Senin / Monday checklist

1. **Buktikan dulu di luar lab**: konsol kecil yang memakai pustaka pilihan, tautkan nomor dengan QR, ambil daftar grup, kirim satu pesan ke grup IT Batam. / **Prove it outside the lab first**: a small console with the chosen library, link the number by QR, list the groups, send one message to the IT Batam group.
2. Cek sesi bertahan: matikan dan nyalakan, kirim tanpa QR baru. / Check the session survives: stop, start, send without a new QR.
3. Isi `RealWhatsAppClient` (tiga metode, lalu peristiwa `MessageReceived`). / Fill `RealWhatsAppClient` (three methods, then the `MessageReceived` event).
4. Set `WhatsApp:Client` = `Real` di `appsettings.json`. / Set `WhatsApp:Client` = `Real`.
5. Coba lewat Swagger (langkah di bagian 4). Endpoint `/fake/*` sengaja menolak saat klien Real. / Try through Swagger (section 4). The `/fake/*` endpoints refuse on purpose with the Real client.
6. Pakai pengaturan aman: `maxMessagesPerDay` kecil, `minSecondsBetweenMessages` besar, nomor yang rela hilang. / Use safe settings: a small `maxMessagesPerDay`, a large `minSecondsBetweenMessages`, a number you can afford to lose.
7. Catat hasil per nomor tiket (1 sampai 5): terbukti, belum, atau gagal. / Note the result per ticket item (1 to 5): proven, not yet, or failed.

## 6b. Langkah 1 dengan WhatsApp sungguhan / Step 1 with real WhatsApp (`poc\Step1.Console`)

Konsol kecil di luar lab untuk membuktikan satu hal: kirim satu pesan ke grup. Memakai paket **WhatsApp.Client 1.0.7** (otomatisasi browser dengan Playwright, port C# dari whatsapp-web.js). / A small console outside the lab to prove one thing: send one message to a group. It uses the **WhatsApp.Client 1.0.7** package (browser automation with Playwright, a C# port of whatsapp-web.js).

**Yang sudah dicek tentang paketnya / What was checked about the package**
- Lisensi Apache-2.0, target .NET 8, pembuat "WhatsApp.Client Contributors", repo `github.com/darthlotu5/WhatsAppDotnet`, bergantung pada Microsoft.Playwright 1.40.0 dan FFMpegCore. / Licence Apache-2.0, targets .NET 8, repo above, depends on Microsoft.Playwright and FFMpegCore.
- Paketnya hanya berisi pustaka (`lib/`), **tanpa skrip atau target yang berjalan saat build**. / The package holds only the library, with **no script or target that runs at build**.
- README paket menulis `using WhatsApp.Client;` tetapi namespace sebenarnya `WhatsAppDotnet`; saya perbaiki lewat kompiler. / The package README says `WhatsApp.Client` but the real namespace is `WhatsAppDotnet`; fixed through the compiler.
- **Belum dicek**: isi kode pustaka (hanya dll), seberapa terawat, dan perilakunya di WhatsApp. Anggap sebagai kode tidak tepercaya yang memegang sesi nomor Anda. / **Not checked**: the library code itself (only the dll), how well it is maintained, and how it behaves on WhatsApp. Treat it as untrusted code that holds your number's session.

**Hari H, berurutan / on the day, in order** (jalankan dari folder `aifm-whatsapp-lab` / run from the lab folder)
1. Sekali saja, pasang browser Playwright (unduhan besar) / once, install the Playwright browser (large download):
   ```text
   dotnet build poc\Step1.Console
   powershell -ExecutionPolicy Bypass -File poc\Step1.Console\bin\Debug\net8.0\playwright.ps1 install chromium
   ```
2. Tautkan nomor (browser terlihat; di HP: WhatsApp > Perangkat tertaut > Tautkan perangkat, pindai QR). **Nomor Anda tidak diketik di mana pun.** / Link the number (browser visible; on the phone: Linked devices > Link a device, scan the QR). **Your number is not typed anywhere.**
   ```text
   dotnet run --project poc\Step1.Console -- link
   ```
3. Lihat grup (tambahkan `--members` untuk jumlah anggota, tiket butir 2) / list the groups (add `--members` for the member count, ticket item 2):
   ```text
   dotnet run --project poc\Step1.Console -- groups
   ```
4. Uji kering dulu (tidak mengirim) / dry run first (sends nothing):
   ```text
   dotnet run --project poc\Step1.Console -- send "IT Batam" "Test dari POC"
   ```
5. Kirim **satu** pesan / send **one** message: tambahkan `--yes` / add `--yes`.
6. Matikan program, nyalakan lagi, jalankan `groups` tanpa scan QR: itu bukti **sesi bertahan** (langkah 1a). / Stop the program, start it again, run `groups` with no QR scan: that proves the **session survives** (step 1a).
7. `listen` selama 60 detik sambil menulis di grup dari HP lain: bukti **menerima pesan** (butir 4). / `listen` for 60 seconds while writing in the group from another phone: proof of **receiving** (item 4).

**Aturan aman / safety rules**
- Nomor yang rela hilang; sedikit pesan; jangan menjalankan WhatsApp Web lain pada nomor yang sama saat uji. / A number you can lose; few messages; do not run another WhatsApp Web on the same number during the test.
- Folder sesi ada di `%LOCALAPPDATA%\aifm-whatsapp-lab\session`, **di luar** proyek. Folder itu setara kunci akun: jangan di-commit, di-zip, atau dikirim. / The session folder is **outside** the project. It equals a key to the account: never commit, zip, or send it.
- Konsol menolak teks lebih dari 500 karakter dan hanya mengirim satu pesan per jalan. / The console refuses text over 500 characters and sends one message per run.
- Catat hasil tiap langkah di `README-MAA-2102.md` (tabel log). / Note each result in the work log table.

**Status / status**: dibangun dan dikompilasi (0 error), mode `help` jalan. **Belum dijalankan terhadap WhatsApp sama sekali**: itu menunggu Anda dan nomor Anda. / built and compiled (0 errors), `help` mode runs. **Never run against WhatsApp yet**: that waits for you and your number.

## 7. Yang belum dibuktikan lab ini / What this lab does not prove

- Tidak ada WhatsApp sungguhan: perilaku pustaka, QR, sesi, dan blokir nomor tidak teruji. / No real WhatsApp: library behaviour, the QR, the session, and number blocking are not tested.
- `WhatsAppSession.SessionData` belum dienkripsi (hanya kolomnya). Di sistem nyata harus dienkripsi dengan kunci di luar database. / `SessionData` is not encrypted yet (only the column). A real system must encrypt it with a key outside the database.
- Belum terhubung ke tabel ERP (`NotificationSetupForWork.WhatsAppGroupJid` belum ditambah) dan tidak membahas layanan AST yang sudah ada. / Not connected to the ERP tables, and the existing AST service is not addressed.
- Belum ada autentikasi di endpoint. / No authentication on the endpoints yet.
- Pesan masuk hanya dicatat dan ditandai diproses; "memprosesnya" adalah bagian yang Anda tulis. / Incoming messages are only recorded and marked processed; "processing" is the part you write.

## 8. Hasil uji / Test result

`scripts/smoke-test.ps1`, 14 Nov 2026: **13 lulus, 0 gagal dari 13** / **13 PASS, 0 FAIL of 13** (Off bawaan, sinkron grup, pesan tidak terkirim saat Off, terkirim setelah dinyalakan, pemicu API, pemicu SQL Server, jam terjadwal, tugas berulang Hangfire, gagal lalu coba ulang, batas harian, menerima pesan, mati lagi, grup tak dikenal ditolak). Semuanya dengan klien palsu. / All with the fake client.
