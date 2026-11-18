# Panduan kirim media dan anti-spam (lab WhatsApp MAA-2102)

Untuk: Damar. Berlaku untuk lab di `aifm-whatsapp-lab`, cabang `feature/multi-number`, per 18 Nov 2026.

## 1. Status per jenis pesan

| Jenis | Cara kirim di WhatsApp Web | Hasil uji sungguhan |
|---|---|---|
| Teks | Ketik di kotak chat, Enter | Terbukti sejak 14 Nov |
| Audio | Lampiran, Audio | Terbukti 18 Nov (mp3, 81 KB). Satu kali gagal karena menu lampiran belum terbuka; sekarang sistem menunggu item menu dan membuka ulang bila perlu |
| Audio dengan teks | Audio dulu, lalu teks sebagai pesan berikutnya (pratinjau audio tidak punya kotak keterangan) | Terbukti 18 Nov: audio 0:05 lalu teks "Audio test with text" tepat di bawahnya |
| Gambar dengan keterangan | Lampiran, Photos & videos, keterangan di pratinjau | Terbukti 18 Nov (jpeg, 136 KB) dengan keterangan, percobaan pertama |
| Video dengan keterangan | Lampiran, Photos & videos, keterangan di pratinjau; tunggu video diproses | Terbukti 18 Nov (mp4, 17,7 MB, 0:10) dengan keterangan, percobaan pertama; WhatsApp menampilkannya sebagai HD |
| Dokumen (pdf dan lainnya) | Lampiran, Document | Belum diuji; lab hanya menerima audio, video, gambar |
| Voice note (rekaman suara) | Tidak didukung; audio dikirim sebagai file audio | - |
| Menerima file dari orang lain | Belum dibuat | - |

Kiriman file dianggap berhasil hanya jika pratinjau di WhatsApp Web sudah tertutup setelah tombol kirim ditekan. Jika pratinjau masih terbuka, status menjadi Failed, tidak Sent. Pada 18 Nov pagi, sebelum pengecekan ini ada, satu audio dengan teks tercatat Sent padahal hanya teksnya yang terkirim.

## 2. Cara mengirim

### Dari dashboard (http://localhost:5300/)
1. Pilih nomor di bagian atas. Status harus Linked.
2. Langkah 3: pilih grup (hanya grup yang dicentang Allowed yang muncul).
3. Tulis teks atau pilih template. Untuk file, teks menjadi keterangan (audio: teks dikirim sebagai pesan berikutnya).
4. Choose File: pilih audio, gambar, atau video dari komputer.
5. Queue message. Tombol terkunci sampai pesan masuk antrean (mencegah klik ganda).
6. Lihat outbox: Pending, lalu Sent atau Failed dengan alasan.

### Lewat API
```text
1) Unggah file (hasil: id, tipe, ukuran, SHA-256):
curl -F "file=@C:\path\clip.mp4;type=video/mp4" "http://localhost:5300/api/whatsapp/media?sessionId=<id nomor>"

2) Antre pesan yang menunjuk file itu:
POST http://localhost:5300/api/whatsapp/messages
{ "sessionId": "<id nomor>", "groupName": "Advancer Wa test", "body": "Video test {time}", "mediaId": "<id file>" }
```
Contoh lengkap ada di `lab-api.http`.

### Format dan batas
| Jenis | Ekstensi yang diterima |
|---|---|
| Audio | mp3, m4a, aac, ogg, opus, wav, amr |
| Video | mp4, mov, webm, 3gp, mkv |
| Gambar | jpg, jpeg, png, webp, gif |

Batas 25 MB per file (`Media:MaxMegabytes`). Itu batas lab, bukan batas WhatsApp; batas WhatsApp belum diverifikasi. Video 28,9 MB dari Downloads ditolak oleh batas ini.

## 3. Lapisan anti-spam

| Lapisan | Apa yang dilakukan | Di mana diatur |
|---|---|---|
| Off bawaan | Nomor baru tidak mengirim apa pun sampai Enabled dicentang | Dashboard langkah 2 |
| Grup yang diizinkan | Hanya grup yang dicentang Allowed bisa dikirimi dan dibaca | Tabel grup, kolom Allowed |
| Jam kirim | Di luar jam ini pesan menunggu (jam komputer) | Send from / until hour |
| Batas harian | Jumlah pesan per hari per nomor | Max messages per day |
| Gelombang | Paling banyak N pesan dalam M menit | Wave: at most / per minutes |
| Jeda dan jitter | Jeda minimum ditambah jeda acak antar pesan | Min seconds / Random extra gap |
| Tolak duplikat | Teks yang persis sama (atau file yang sama) ke grup yang sama dalam 2 menit ditolak (HTTP 409) | Kode, `DuplicateWindow` |
| Variasi teks | Token `{time}`, `{date}`, `{datetime}`, `{rand}`, `{a\|b\|c}` membuat tiap pesan berbeda | Template di dashboard |
| Tombol terkunci | Queue message tidak bisa diklik dua kali selama proses | Dashboard |
| Satu pengirim | Hanya dispatcher yang mengirim, satu per satu, sehingga semua aturan di atas selalu berlaku | Kode |

Pengaturan yang disarankan untuk pemakaian biasa (tombol Fill the safe preset): 15 pesan per hari, jeda 30 detik ditambah acak sampai 60 detik, jam 08-18, paling banyak 3 pesan per 90 menit. Angka ini saran tim (Tegar), bukan aturan resmi WhatsApp.

Per 18 Nov sore, nomor Default sudah memakai preset aman ini, dan hanya Advancer Wa test serta IT Team Batam yang dicentang Allowed. Kalau perlu uji lebih cepat, longgarkan sementara lalu kembalikan.

Kebiasaan yang mengurangi risiko blokir (tidak menghilangkannya):
- Pakai nomor khusus uji, bukan nomor pribadi.
- Kirim hanya ke grup sendiri dan orang yang memang menunggu pesan.
- Jangan mengirim teks yang sama berulang-ulang; pakai token variasi untuk pesan uji.
- Jangan sering memutus dan menyambung ulang; matikan lab dengan skrip restart (bagian 5).

## 4. Mengecek hasil

| Cara | Kegunaan |
|---|---|
| Outbox di dashboard | Status tiap pesan dan alasan gagal |
| `GET /api/whatsapp/real/chat-probe?groupName=...` | Baca saja: 10 pesan terakhir di chat (audio/gambar/video/teks) dan apakah pratinjau file masih terbuka |
| `GET /api/whatsapp/real/attach-probe?groupName=...` | Baca saja: isi menu lampiran |
| `GET /api/whatsapp/real/preview-probe?groupName=...&mediaId=...` | Memasang file di pratinjau, membaca tombolnya, lalu menghapusnya; tidak mengirim |
| `GET /api/whatsapp/real/debug` | Tangkapan layar ke `%LOCALAPPDATA%\aifm-whatsapp-lab\debug.png` |
| Log API | `%TEMP%\lab-run.txt` (baris `[REAL WhatsApp] file sent to ...` atau `Send file failed`) |

## 5. Menyalakan ulang dengan aman

```text
powershell -ExecutionPolicy Bypass -File scripts\restart-lab.ps1
```
Skrip memutus setiap nomor dulu (login tetap tersimpan), menghentikan proses, build, lalu menjalankan lagi. Jangan mematikan proses dengan paksa: pada 16 Nov login hilang karena itu.

## 6. Masalah umum

| Gejala | Arti | Yang dilakukan |
|---|---|---|
| Failed: `waiting for Locator("#pane-side span[title]")...` | Grup tidak ketemu di daftar chat | Sync chats / groups; cek nama grup persis sama |
| Failed: `The file preview did not close...` | Tombol kirim ditekan tetapi file tidak terkirim (upload lambat atau tampilan berubah) | Cek dengan chat-probe; pratinjau sisa dihapus otomatis pada percobaan berikutnya |
| Failed: `waiting for Locator("button[aria-label='Attach']")` | Chat tidak terbuka saat lampiran diklik | Coba lagi; jika terus terjadi, jalankan attach-probe |
| 409: `The same message was queued...` | Anti-spam duplikat | Tunggu 2 menit atau ubah teks |
| 400: `not on the allowed list` | Grup tidak dicentang Allowed | Centang grupnya, atau pilih grup lain |
| 400: `File is larger than the limit` | Lebih dari 25 MB | Pakai file lebih kecil atau ubah `Media:MaxMegabytes` |
| Pending lama | Ditahan aturan: di luar jam, batas harian, gelombang penuh, atau jeda | Lihat pengaturan nomor |
| Minta QR setelah restart | Login hilang | Pindai QR lagi; selanjutnya pakai skrip restart |

## 7. Belum selesai

- Menerima audio, gambar, dan video dari orang lain (baru teks yang bisa diterima).
- Audio yang dikirim nomor sendiri terbaca sebagai pesan masuk oleh pembaca pesan; perbaikan untuk teks sudah ada, untuk media belum.
- Dokumen (pdf) belum diuji.
- Belum jalan di server; belum ada API key per sistem pemanggil.
