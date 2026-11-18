# AIFM WhatsApp Gateway Lab (MAA-2102)

Layanan .NET mandiri untuk mengirim dan menerima pesan grup WhatsApp lewat WhatsApp Web (scan QR, tanpa WhatsApp Business API), untuk beberapa nomor, dengan database SQL Server sendiri dan API yang bisa dipanggil sistem lain (ERP). Proyek ini lab belajar dan uji, di luar repo ERP.

Status per 18 Nov 2026 (rinci di `HASIL-POC-14-NOV-2026.md`, baris 1-32):

| Bagian | Status |
|---|---|
| Kirim teks ke grup, terjadwal, pemicu API / SQL Server / Hangfire | Terbukti dengan WhatsApp sungguhan |
| Terima pesan teks grup | Terbukti, dengan bug: pesan akun sendiri kadang ikut tersimpan. Perbaikan ditulis 18 Nov, belum diuji di halaman asli |
| Beberapa nomor, variasi teks, daftar grup yang diizinkan, media (audio, video, gambar) | Lulus smoke test dengan klien Fake (35 dari 35); belum diuji dengan WhatsApp sungguhan |
| Kirim file lewat WhatsApp Web | Kode ditulis, selektor masih dugaan, belum dijalankan |
| Terima file dari WhatsApp Web | Belum dibuat |
| Autentikasi API, jalan di server | Belum ada |

Risiko yang harus diingat: otomasi WhatsApp Web tidak resmi dan melanggar ketentuan WhatsApp, jadi nomor bisa diblokir. Tampilan WhatsApp Web bisa berubah dan memutus fitur. Folder login (`%LOCALAPPDATA%\aifm-whatsapp-lab\session\`) adalah kunci akun: jangan di-commit, di-zip, atau dikirim.

## 1. Cara kerjanya

```text
 PEMICU (semua hanya menulis antrean)        DATABASE (SQL Server)            PEKERJA (satu proses)
 API      POST /api/whatsapp/messages  --+
 SQL      INSERT ke outbox             --+--> WhatsAppOutboundMessageDbSet --> Dispatcher, tiap 2 detik, per nomor:
 Hangfire job berulang (cron, UTC)     --+    (Status 0 Pending)               Off? tertaut? jam kirim? batas harian?
                                                                              gelombang? jeda + jitter? grup diizinkan?
                                                                              -> klien WhatsApp nomor itu
                                                                                 berhasil: Sent, gagal: coba ulang (maks 3)
 Pesan masuk grup  <---------------------  loop terima tiap 10 detik (hanya grup yang dikenal dan diizinkan)
                   --> WhatsAppInboundMessageDbSet (+ file ke WhatsAppMediaDbSet)
```

Kunci desain:
- Pemicu hanya menulis ke outbox; hanya dispatcher yang mengirim, jadi semua pesan lewat aturan yang sama.
- Semua yang khas WhatsApp ada di balik satu antarmuka `IWhatsAppClient`. Ada dua isi: `FakeWhatsAppClient` (tanpa WhatsApp, untuk uji) dan `RealWhatsAppClient` (Playwright mengendalikan Edge di WhatsApp Web).
- Satu nomor = satu sesi = satu klien = satu folder login. Batas dan pengaturan per nomor.
- Off secara bawaan: nomor tanpa baris pengaturan, atau `isEnabled = false`, tidak mengirim apa pun. Nomor baru selalu mulai Off.
- Kegagalan satu pesan atau satu nomor tidak menghentikan yang lain.

## 2. Menjalankan

Prasyarat: .NET 8 SDK, SQL Server lokal (`localhost`, Windows Authentication), Microsoft Edge terpasang.

```text
cd aifm-whatsapp-lab
dotnet build WhatsAppLab.sln
dotnet run --project src\WhatsAppLab.Api --urls http://localhost:5300
```

| Alamat | Isi |
|---|---|
| http://localhost:5300/ | Dashboard uji: login QR, pengaturan, kirim, file, grup, outbox, inbox |
| http://localhost:5300/swagger | Semua endpoint |
| http://localhost:5300/hangfire | Job terjadwal |

Konfigurasi penting (`src/WhatsAppLab.Api/appsettings.json`, bisa ditimpa environment variable, misalnya `WhatsApp__Client=Fake`):

| Kunci | Arti | Bawaan |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | Database | `WhatsAppLabReal` |
| `WhatsApp:Client` | `Real` atau `Fake` | `Real` |
| `WhatsApp:AutoConnect` | Sambung ulang nomor yang pernah tertaut saat start | `true` |
| `WhatsApp:ShowBrowser` | Tampilkan jendela Edge (untuk debug) | `false` |
| `Media:Root` | Folder file media | `%LOCALAPPDATA%\aifm-whatsapp-lab\media` |
| `Media:MaxMegabytes` | Batas ukuran file (batas kami, bukan batas WhatsApp) | `25` |
| `WHATSAPP_PROFILE_ROOT` (env) | Folder induk login browser | `%LOCALAPPDATA%\aifm-whatsapp-lab\session` |

Mematikan dengan aman: tekan Disconnect untuk tiap nomor dulu, baru hentikan proses. Pada 16 Nov login hilang setelah proses dihentikan paksa.

## 3. Menguji

**A. Tanpa WhatsApp (aman, untuk belajar alur):**
```text
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
```
Memakai klien Fake, port 5310, database `WhatsAppLab` (dihapus dan dibuat ulang), folder media sementara. Hasil terakhir: 35 lulus dari 35. Hasil per uji ditulis ke `scripts\smoke-results.csv`.

**B. Dengan dashboard dan WhatsApp sungguhan** (pakai nomor yang rela hilang, minta izin sebelum mengirim ke grup orang lain):
1. Pilih nomor di atas (atau Add number), Connect, pindai QR dari HP: WhatsApp > Perangkat tertaut > Tautkan perangkat.
2. Sync chats / groups. Centang Allowed hanya untuk grup uji.
3. Isi pengaturan (Fill the safe preset), centang Enabled, Save settings.
4. Pilih template atau tulis sendiri; boleh lampirkan file; Queue message.
5. Pantau outbox (Pending, Sent, Failed) dan log di bawah halaman.

**C. Lewat HTTP:** `lab-api.http` berisi contoh semua endpoint (VS Code REST Client atau Visual Studio).

## 4. API

Semua di bawah `/api/whatsapp`. Tambahkan `?sessionId=...` (atau `sessionId` di body) untuk memilih nomor; tanpa itu dipakai nomor pertama.

| Metode dan jalur | Fungsi |
|---|---|
| `GET /sessions`, `POST /sessions {name}` | Daftar nomor, tambah nomor (mulai Off) |
| `GET /status` | Status login, QR, jumlah Pending / Sent / Failed |
| `POST /connection/connect`, `/disconnect`, `/reset` | Login: buka WhatsApp Web, tutup (login tetap), lupakan login |
| `GET /settings`, `PUT /settings` | On/off, pacing, terima pesan |
| `POST /groups/sync`, `GET /groups` | Ambil grup dari WhatsApp, daftar grup |
| `PUT /groups/allowed {groupJids:[...]}` | Daftar grup yang diizinkan (kosong = tanpa batas) |
| `POST /messages {groupName, body, scheduledFor?, mediaId?}` | Antre pesan; `body` boleh memakai token |
| `POST /messages/preview {body}` | Lihat hasil token tanpa mengantre |
| `GET /messages` | 50 baris outbox terakhir |
| `POST /media` (multipart, field `file`) | Unggah audio, video, atau gambar; hasil: id, tipe, ukuran, SHA-256 |
| `GET /media/{id}`, `GET /media/{id}/info` | Unduh file, info file |
| `GET /inbound` | 50 pesan masuk terakhir (dengan `mediaId` bila ada file) |
| `POST /announcements {id, groupName, body, cron}`, `DELETE`, `.../run-now` | Pengumuman berulang Hangfire (cron UTC) |
| `/fake/*` | Alat uji, hanya dengan klien Fake |

Token teks (diisi saat pesan masuk antrean, termasuk tiap putaran job Hangfire): `{time}`, `{date}`, `{datetime}`, `{rand}` (4 angka), `{a|b|c}` (satu pilihan acak). Kurung kurawal lain dibiarkan.

Pemicu SQL Server: insert langsung ke `WhatsAppOutboundMessageDbSet` dengan `Status = 0`. Isi `SessionId` untuk memilih nomor; kosong berarti nomor pertama. Baris SQL tetap dicek terhadap daftar grup yang diizinkan.

## 5. Tabel

| Tabel | Isi |
|---|---|
| `WhatsAppSessionDbSet` | Satu baris per nomor: nama, `ProfileKey` (folder login), status |
| `WhatsAppSettingDbSet` | Pengaturan per nomor (`SessionId`): on/off, batas harian, jeda, jitter, jam kirim, gelombang, terima |
| `WhatsAppGroupDbSet` | Grup per nomor: nama, jumlah anggota, `IsActive`, `IsAllowed` |
| `WhatsAppOutboundMessageDbSet` | Outbox: grup, teks, jadwal, status, percobaan, galat, sumber (Api / SqlTrigger / Scheduled), `MediaId` |
| `WhatsAppInboundMessageDbSet` | Pesan masuk: grup, pengirim, teks, tipe, `MediaId` |
| `WhatsAppMediaDbSet` | File: tipe, mime, ukuran, SHA-256, path relatif di `Media:Root` |

Migrasi EF Core ada di `src/WhatsAppLab.Infrastructure/Migrations` dan berjalan otomatis saat start (`Lab:AutoMigrate`). Data lama dari versi satu nomor dipindah ke nomor pertama saat start.

## 6. Urutan membaca kode

| # | Berkas | Yang dipelajari |
|---|---|---|
| 1 | `src/WhatsAppLab.Domain/Entities.cs` | Enam tabel dan artinya |
| 2 | `src/WhatsAppLab.Application/Abstractions.cs` | Kontrak: `IWhatsAppClient`, registry klien, antarmuka use case |
| 3 | `src/WhatsAppLab.Application/UseCases.cs` | `DispatchDueAsync` (otak pengiriman), media, grup, pesan masuk |
| 4 | `src/WhatsAppLab.Application/MessageText.cs` | Token variasi teks |
| 5 | `src/WhatsAppLab.Infrastructure/Clients.cs` | Fake, Real (Playwright), dan registry satu klien per nomor |
| 6 | `src/WhatsAppLab.Infrastructure/LabDbContext.cs`, `Configurations.cs`, `DependencyInjection.cs` | Pemetaan tabel dan pendaftaran layanan, pola yang sama dengan ERP |
| 7 | `src/WhatsAppLab.Api/Program.cs`, `Workers.cs` | Endpoint, dispatcher latar, job Hangfire |
| 8 | `scripts/smoke-test.ps1` | Satu contoh untuk tiap skenario |

## 7. Peta ke ERP (dicek di repo ERP, 18 Nov 2026)

| Lab | Padanan di ERP (`aifm_erp/src`) |
|---|---|
| `Domain/Entities.cs`, `Entity` | `BinaryPlate.Domain` (Id, IsDeleted, kolom audit) |
| `IApplicationDbContext` di `Abstractions.cs` | `BinaryPlate.Application/Common/Interfaces/Persistence/IApplicationDbContext.cs` |
| Antarmuka use case | `BinaryPlate.Application/Common/Interfaces/UseCases/<Modul>/` |
| Use case | `BinaryPlate.Application/UseCases/<Modul>/` |
| `DependencyInjection.cs` (`AddScoped<...>`) | `BinaryPlate.Infrastructure/DependencyInjection.cs`, tempat ERP mendaftarkan use case |
| `LabDbContext`, `Configurations.cs` | `BinaryPlate.Infrastructure/Persistence/ApplicationDbContext.cs` dan konfigurasinya |
| Endpoint di `Program.cs` | Controller di `BinaryPlate.WebAPI/Controllers/<Modul>/` |
| Hangfire (`AddHangfire`, cron) | ERP sudah memakai Hangfire: `AddHangfire` di `Infrastructure/DependencyInjection.cs` dengan connection string `HangfireConnection` sendiri, dan job berulang lewat `IRecurringJobManager` (contoh: `WebAPI/Hubs/EmployeeHub.cs`). Jangan menambah Hangfire kedua |
| Dispatcher (`BackgroundService`) | Belum ada `BackgroundService` atau `IHostedService` di ERP |
| Pengiriman WhatsApp yang sudah ada | ERP sudah mengirim lewat layanan AST (`Application/Common/Helpers/WhatsApp.cs`, kirim saja, berdasarkan nama grup), tabel `AttendanceNotification` dan `NotificationSetupForWork`. Lihat `Analisis-Tabel-ERP-MAA-2102.md` |

Dua cara menghubungkan ke ERP:
1. **Disarankan, sesuai arahan CW (17 Nov):** gateway ini berdiri sendiri, ERP memanggil API-nya (`POST /messages`, `GET /inbound`). Di ERP cukup satu use case kecil yang memanggil HTTP, di tempat yang sekarang memanggil AST. Browser per nomor tidak masuk ke proses web ERP.
2. Ditanam di ERP: tabel ke `ApplicationDbContext`, use case ke `UseCases`, dispatcher sebagai hosted service baru. Tidak disarankan, karena tiap nomor menjalankan browser di proses web ERP.

Sebelum masuk ERP masih perlu: API key per sistem pemanggil, uji media dengan WhatsApp sungguhan, jalan di server, dan perbaikan bug pesan sendiri di penerimaan.

## 8. Dokumen di repo ini

| Berkas | Isi |
|---|---|
| `HASIL-POC-14-NOV-2026.md` | Log semua uji (baris 1-32) |
| `POC-REPORT-MAA-2102.md` / `.id.md` (+ PDF) | Laporan POC untuk CW |
| `DESAIN-SISTEM-MAA-2102(.id).html` (+ PDF) | Gambar arsitektur dan alur dispatcher |
| `STUDY-NEXT-PHASE-MAA-2102.md`, `STUDI-FASE-LANJUT-MAA-2102.md` (+ PDF) | Studi implementasi dan keamanan fase lanjut |
| `Analisis-Tabel-ERP-MAA-2102.md` | Tabel ERP yang terkait |
| `Panduan-WhatsApp-Lab.pdf` | Panduan lama (14 Nov, versi satu nomor dan klien Fake) |
| `scripts/build-poc-report.ps1`, `scripts/translate-design-id.ps1` | Membuat ulang laporan dan gambar |
| `poc/Step1.Console` | Percobaan pertama dengan paket WhatsApp.Client (gagal karena CSP, disimpan sebagai catatan) |
