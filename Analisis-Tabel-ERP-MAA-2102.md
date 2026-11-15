# Analisis tabel ERP untuk MAA-2102 / ERP table analysis for MAA-2102

Untuk catatan pribadi / for my own notes. 13 Nov 2026. Tujuan / purpose: menentukan tabel mana yang dipakai ulang dan mana yang ditambah, supaya fitur baru menyatu dengan ERP / decide which tables are reused and which are added, so the new feature fits the ERP.
Asal tiap pernyataan / source of each statement: **[Repo]** (sudah dicek di kode / checked in the code), **[Tiket]**, **[Call]**, **[Asumsi]** (rancangan atau dugaan saya, belum terbukti / my design or guess, not proven).

---

## 1. Temuan penting: ERP sudah punya pengiriman WhatsApp ke grup / Key finding: the ERP already sends WhatsApp to groups

| ID | EN |
|---|---|
| Helper `WhatsApp` mengirim pesan ke grup lewat **layanan luar** (AST WhatsApp API di `astapi.advastech.com.sg`): ambil token, lalu kirim ke **nama grup**. Hanya mengirim, **tidak menerima**. **[Repo: `Application/Common/Helpers/WhatsApp.cs`]** | The `WhatsApp` helper sends to a group through an **external service** (the AST WhatsApp API at `astapi.advastech.com.sg`): get a token, then send to a **group name**. Send only, **no receiving**. |
| Dipakai untuk notifikasi kehadiran: `WhatsAppNotification.AttAlert` membaca tabel `AttendanceNotification`, mencari nama grup di `NotificationSetupForWork`, lalu mengirim dan mencatat status (-1 belum, 0 gagal, 1 berhasil). **[Repo]** | Used for attendance notifications: `WhatsAppNotification.AttAlert` reads `AttendanceNotification`, finds the group name in `NotificationSetupForWork`, sends, and records the status (-1 not sent, 0 failed, 1 success). |
| Ada tombol uji untuk admin (`SubmitWACommand`, `AdminFunctionUseCase.WhatsAppFunctionTest`). **[Repo]** | There is an admin test action (`SubmitWACommand`, `AdminFunctionUseCase.WhatsAppFunctionTest`). |
| Tugas berulang Hangfire mencantumkan "Send Late Attendance Notification" (`Pages/POC/Hangfire/HF_DailyJob.razor.cs`). **[Repo]** | The Hangfire recurring jobs list includes "Send Late Attendance Notification". |
| Satu jalur sudah dialihkan ke notifikasi aplikasi (`AttAppsAlert`, nama grup diisi "Apps Nofitication"). **[Repo]** | One path was already moved to in-app notifications (`AttAppsAlert`, group name set to "Apps Nofitication"). |
| Kredensial layanan AST tertulis langsung di kode helper. Jangan disalin ke fitur baru; simpan di konfigurasi. **[Repo]** | The AST service credentials are written directly in the helper code. Do not copy them to the new feature; keep them in configuration. |

**Artinya untuk MAA-2102 / What it means for MAA-2102**
- ID: Fitur baru berbeda dari yang ada: mengirim lewat **ID grup**, bisa **menerima** pesan, dan **tanpa layanan luar**. Jangan membuat tabel yang menggandakan `AttendanceNotification` atau `NotificationSetupForWork`. **[Asumsi]**
- EN: The new feature differs from the existing one: it sends by **group ID**, can **receive** messages, and has **no outside service**. Do not create tables that duplicate `AttendanceNotification` or `NotificationSetupForWork`. **[Assumption]**
- ID: Saya **belum tahu** siapa pemilik layanan AST dan apakah itu yang dimaksud CW ("orang yang pernah berhasil"). Jangan dinyatakan sebagai fakta. Cukup dicatat sebagai temuan di update.
- EN: I **do not know** who owns the AST service or whether it is what CW meant ("someone has done it"). Do not state it as fact. Just note it as a finding in the update.

---

## 2. Tabel yang ada dan perlakuannya / Existing tables and how to treat them

| Tabel / Table | Isi sekarang / Now holds | Perlakuan / Treatment |
|---|---|---|
| `NotificationSetupForWork` (`EntityWithOptionalTenant`) | `PortfolioId`, `TypeOfServiceCodeId`, `WhatsAppGroupName`, `WhatsAppGroupInviteLink`, `IsEnabled` **[Repo]** | **Dipakai ulang / reuse.** Tetap jadi tempat mengatur grup per portofolio. Usulan: tambah kolom `WhatsAppGroupJid` (nullable) supaya nama grup bisa dipetakan ke ID grup **[Asumsi]** / reuse as the place that sets the group per portfolio; proposal: add a nullable `WhatsAppGroupJid` column so a group name maps to a group ID |
| `AttendanceNotification` (di `MDDutyContext`, tanpa kelas dasar / no base class) | Antrean notifikasi kehadiran: `SendTime`, `Contents`, `SendStatus`, `WhatsappGroupName`, `isactive` **[Repo]** | **Tidak diubah / leave as is.** Khusus kehadiran. Nanti bisa mengirim lewat antrean baru tanpa mengubah skemanya **[Asumsi]** / attendance only; it can later send through the new outbox without a schema change |
| `PlatformEventLog`, `AuditLog` | Log kejadian dan audit **[Repo]** | **Kandidat dipakai ulang** untuk catatan audit (siapa menautkan nomor, siapa memicu kirim). Perlu dicek isinya cocok / candidate for audit records; needs a check that the content fits |
| `Notification`, `FcmToken`, `EmailHistory`, `EmailTemplate` | Notifikasi aplikasi, token push, riwayat email, templat email **[Repo]** | **Bukan untuk ini / not for this.** Saluran lain; boleh ditiru polanya (riwayat kirim, templat) / other channels; the pattern (send history, template) can be copied |

---

## 3. Tabel baru yang diusulkan / Proposed new tables **[Asumsi]**

Semua mengikuti kebiasaan ERP / all follow the ERP convention: nama berakhiran `DbSet`, kelas dasar `Entity` (sudah memberi `Id`, `IsDeleted`, `CreatedBy`, `CreatedOn`, `ModifiedBy`, `ModifiedOn`, `DeletedBy`, `DeletedOn`), kelas konfigurasi di `Persistence/Configurations`, migration EF di `Infrastructure/Migrations` pada `ApplicationDbContext` **[Repo]**.

| # | Tabel / Table | Untuk apa / Purpose | Kolom utama (di luar kolom dasar) / main columns |
|---|---|---|---|
| A | `WhatsAppSessionDbSet` | Nomor yang ditautkan dan sesinya / the linked number and its session | `PhoneNumber`, `Status` (belum ditautkan, tertaut, terputus / not linked, linked, disconnected), `SessionData` (**dienkripsi / encrypted**), `LinkedOn`, `LastSeenOn`, `LibraryVersion` |
| B | `WhatsAppGroupDbSet` | Grup yang diikuti nomor itu, hasil sinkron / groups the number has joined, synced | `SessionId`, `GroupJid`, `Name`, `ParticipantCount`, `LastSyncedOn`, `IsActive` |
| C | `WhatsAppOutboundMessageDbSet` | **Antrean kirim** (outbox): satu baris = satu pesan / one row = one message | `GroupJid`, `Body`, `ScheduledFor`, `Status` (menunggu, terkirim, gagal / pending, sent, failed), `Attempts`, `SentOn`, `ErrorMessage`, `Source` (Api, SqlTrigger, Scheduled, Attendance), `SourceReference`, `CorrelationId` |
| D | `WhatsAppInboundMessageDbSet` | Pesan grup yang diterima / received group messages | `GroupJid`, `SenderJid`, `SenderName`, `Body`, `MessageType`, `ReceivedOn`, `ProcessedOn`, `ProcessStatus` |
| E | `WhatsAppSettingDbSet` | Pengaturan satu baris, **Off secara bawaan** seperti `FaceVerificationSettingDbSet` / one-row settings, **Off by default** like `FaceVerificationSettingDbSet` | `IsEnabled`, `MaxMessagesPerDay`, `MinSecondsBetweenMessages`, `ReceiveEnabled`, `InboundRetentionDays` |
| F | `WhatsAppScheduledAnnouncementDbSet` (opsional / optional) | Pengumuman berulang: grup, isi, jam, aktif atau tidak / recurring announcements: group, body, time, enabled | `GroupJid`, `Body`, `ScheduleTime`, `Recurrence`, `IsEnabled`, `LastRunOn` |

---

## 4. Pemetaan ke permintaan tiket / Mapping to the ticket

| Permintaan tiket / Ticket requirement | Tabel / Tables |
|---|---|
| Kirim ke grup tertentu yang sudah diikuti / send to a joined group | A (sesi), B (grup), C (antrean) |
| Grup besar (contoh 30 anggota) / large group | B (`ParticipantCount` dicatat) |
| Pengumuman terjadwal / scheduled announcements | C (`ScheduledFor`) dan F (berulang / recurring); tugas Hangfire yang sudah ada / the existing Hangfire |
| Menerima pesan grup dan memprosesnya / receive and process group messages | D |
| Dipicu dari SQL Server / triggered from SQL Server | C: baris yang ditambahkan ke tabel memicu kirim / a row added to the table triggers a send |
| Dipicu dari API atau layanan latar / triggered from an API or background service | C: endpoint API atau layanan latar menulis ke C / an API endpoint or background service writes to C |
| Aman dipakai (batas kirim, bisa dimatikan) / safe to run (send limits, can be switched off) | E |

---

## 5. Tahapan / Phases

| Tahap / Phase | Tabel / Tables |
|---|---|
| POC langkah 1 sampai 1a (konsol) / POC steps 1 to 1a (console) | **Tidak ada / none.** Sesi disimpan di berkas lokal / the session is kept in a local file |
| Setelah POC berhasil dan pustaka dipilih / after the POC works and the library is chosen | A, B, C, E (inti / core) dan kolom `WhatsAppGroupJid` di `NotificationSetupForWork` |
| Tahap lanjut / later | D (menerima / receiving), F (berulang / recurring) |

Tabel baru baru dibuat **setelah** POC membuktikan pustaka mana yang bekerja, karena kolom A (data sesi) bergantung pada pustaka yang dipilih **[Asumsi]**. / New tables are created **after** the POC shows which library works, because the columns of A (session data) depend on the chosen library.

---

## 6. Hal yang perlu diputuskan atau dicek sebelum membuat tabel / To decide or check before creating tables

| Hal / Item | Catatan / Note |
|---|---|
| Konteks database / which DbContext | Tabel baru masuk `ApplicationDbContext`: hanya itu yang punya migration EF di repo **[Repo]**. `MDDutyContext` menampung tabel lama (FM) / new tables go into `ApplicationDbContext`: only it has EF migrations in the repo; `MDDutyContext` holds the older FM tables |
| Data sesi (A) | Berisi kunci yang bisa mengendalikan nomor itu. Wajib dienkripsi, kunci di luar database (seperti rencana enkripsi template wajah di MAA-2072) **[Asumsi]** / holds keys that can control the number; must be encrypted, key outside the database |
| Pesan masuk (D) | Berisi percakapan dan nomor anggota grup (data pribadi). Simpan hanya dari grup yang diizinkan, dan beri masa simpan **[Asumsi]** / holds conversations and member numbers (personal data); store only from allowed groups, with a retention period |
| Hubungan dengan layanan AST yang ada / relation to the existing AST service | Belum diputuskan: berjalan berdampingan atau menggantikan. Itu keputusan setelah POC, bukan sekarang / not decided: side by side or replace; a decision after the POC, not now |
| Nama grup vs ID grup / group name vs group ID | Layanan AST memakai nama grup; pustaka biasanya memakai ID grup. Karena itu perlu tabel B dan kolom `WhatsAppGroupJid` **[Asumsi]** / the AST service uses a group name; libraries usually use a group ID; that is why B and `WhatsAppGroupJid` are needed |
| Batas kirim / send limits | Disimpan di E supaya bisa diubah tanpa memasang ulang; juga mengurangi risiko nomor diblokir / kept in E so it can change without redeploying; also lowers the risk of the number being blocked |

---

## 7. Yang belum diketahui / What is not known yet

- ID: Pustaka mana yang akan dipakai, jadi bentuk pasti kolom `SessionData` belum bisa ditetapkan.
- EN: Which library will be used, so the exact shape of `SessionData` cannot be set yet.
- ID: Apakah POC akhirnya berhasil mengirim ke grup. Kalau tidak, tabel ini tidak perlu dibuat.
- EN: Whether the POC succeeds in sending to a group at all. If not, these tables are not needed.
- ID: Kebutuhan sebenarnya: hanya mengirim (A, B, C, E cukup) atau juga menerima (D).
- EN: The real need: send only (A, B, C, E are enough) or also receive (D).
