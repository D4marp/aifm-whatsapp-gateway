# Laporan POC MAA-2102: pesan grup WhatsApp dari .NET


## Ringkasan

Yang dibuat: layanan .NET yang memakai satu nomor WhatsApp lewat WhatsApp Web. Nomor ditautkan sekali dengan scan QR di halaman dashboard. Tidak memakai Business API resmi.

Yang jalan, diuji dengan WhatsApp sungguhan: kirim ke grup 4, 5, 29, dan 36 anggota, kirim terjadwal, antre pesan dari panggilan API, dari insert SQL Server, dan dari job cron Hangfire, serta membaca pesan grup yang baru masuk ke SQL Server. Selama uji terkirim 47 pesan, dan Hangfire menjalankan 26 job dengan 0 gagal.

Yang masih terbuka: risiko nomor diblokir dalam jangka panjang, jalan di server, dan satu bug di penerimaan (lihat Temuan).

## Pendekatan

| Hal | Pilihan |
|---|---|
| Koneksi | Playwright mengendalikan Edge di WhatsApp Web; QR dipindai sekali dari dashboard, login disimpan di profil lokal |
| Pengiriman | Semua pemicu menulis satu baris ke tabel outbox di SQL Server; satu dispatcher yang mengirim |
| Pacing | Jam kirim 08-18, batas harian, batas gelombang, jeda minimum dengan jitter acak |
| Penerimaan | Loop membaca daftar chat, membuka grup yang punya pesan baru, menyimpannya ke tabel Inbound |
| Mode | Berjalan tanpa jendela browser yang terlihat |

## Istilah penting

| Istilah | Arti |
|---|---|
| Playwright | Pustaka yang mengendalikan browser dari kode; di sini mengendalikan Edge di WhatsApp Web seperti orang di depan layar |
| Outbox | Tabel SQL Server yang menampung setiap pesan yang akan dikirim beserta statusnya |
| Dispatcher | Layanan latar belakang yang mengirim baris outbox yang sudah waktunya dan menerapkan aturan pacing; satu-satunya pengirim |
| Hangfire | Pustaka job latar belakang yang menyimpan job di SQL Server; di sini menjalankan pengumuman berulang (cron) dan hanya menulis baris outbox |

## Desain

![fig:1]
![fig:2]

## Screenshot

Sebelum login: dashboard menampilkan QR; pindai dari WhatsApp, Perangkat tertaut. Tampilan dashboard berbahasa Inggris.

![img:poc/fig-qr.png]

Sesudah login: status Linked; setelah restart normal tidak perlu QR baru.

![img:poc/fig-linked.png]

Outbox: setiap pesan dengan status dan sumbernya (Api, SqlTrigger, Scheduled).

![img:poc/fig-outbox.png]

Hangfire, job berulang: cron `0 2 * * *` (UTC) menjalankan `AnnouncementJob.Run`, yang hanya menulis satu baris outbox.

![img:poc/fig-hf-recurring.png]

Hangfire, job yang sudah jalan: 26 berhasil, 0 gagal.

![img:poc/fig-hf-jobs.png]

Data SQL Server: jumlah outbox, pesan masuk, dan jumlah anggota grup yang tersimpan.

![img:poc/fig-sql.png]

Log konsol API (cuplikan): jumlah anggota terbaca dan pesan terkirim.

![img:poc/fig-log.png]

## Hasil per butir tiket

| Butir tiket | Hasil | Bukti |
|---|---|---|
| Kirim ke grup yang diikuti | Terbukti | Pesan terkirim ke grup uji "Advancer Wa test"; terlihat di HP |
| Grup 30 anggota | Terbukti | Pesan terkirim ke grup 36 anggota (1 pesan) dan grup 29 anggota (3 pesan); jumlah anggota dibaca dari panel info grup dan disimpan |
| Pengumuman terjadwal | Terbukti | Pesan sekali kirim terkirim setelah waktunya; job cron berulang Hangfire menulis baris outbox dan pesan terkirim |
| Terima dan proses pesan grup | Terbukti, dengan batas | Tiga pesan dari nomor kedua tersimpan masing-masing sekali. Bug yang diketahui: 14 pesan yang dikirim akun lab sendiri ikut tersimpan sebagai pesan masuk |
| Pemicu dari SQL Server | Terbukti | Insert langsung ke tabel outbox, terkirim sekitar 5 detik kemudian |
| Pemicu dari API | Terbukti | `POST /api/whatsapp/messages` |
| Pemicu dari background service | Terbukti | Job Hangfire ditambah layanan dispatcher |

## Temuan

| Temuan | Dampak |
|---|---|
| Otomasi tidak resmi melanggar ketentuan WhatsApp | Nomor bisa diblokir; pakai nomor yang rela hilang |
| Bergantung pada tampilan halaman WhatsApp Web | Pembaca pesan masuk perlu empat perbaikan dalam sehari karena halaman berbeda dari dugaan; siap-siap perawatan |
| Satu nomor butuh satu worker | Di ERP sebaiknya jadi proses worker terpisah |
| Membuka grup untuk dibaca menandainya terbaca di HP | Efek samping dari penerimaan |
| Penerimaan membaca semua grup hasil sync, bukan hanya grup uji | 4 pesan dari grup lain tersimpan sebelum penerimaan dimatikan; perlu daftar grup yang diizinkan |
| Sync juga memasukkan beberapa chat pribadi sebagai grup (0 anggota) | Pesan uji bisa tak sengaja diantrekan ke seseorang; daftar perlu difilter |
| Login hilang sekali setelah proses dihentikan paksa | Perlu scan QR baru setelah restart; penyebab belum dipastikan; worker harus berhenti secara normal |
| Daftar grup baru berubah saat sync dijalankan | Setelah ganti akun WhatsApp, jalankan sync; 18 grup akun baru lalu muncul |
| Satu pengiriman timeout (kotak pesan tidak ditemukan dalam 8 detik) | Coba ulang berhasil; retry dispatcher menanganinya |
| Pacing menahan pesan sesuai rancangan | Pesan tetap Pending di luar jam kirim atau saat gelombang penuh |

## Belum terbukti

| Hal | Yang dibutuhkan |
|---|---|
| Banyak pesan masuk sekaligus | Uji dengan banyak pesan beruntun dari nomor lain |
| QR baru saat mode tersembunyi | Uji di mode tersembunyi |
| Jalan di server | Hosting tanpa jendela dan sesi stabil di server |
| Jalan lama dengan pacing | Beberapa hari operasi |
| Integrasi ERP | Eksplorasi ERP (background job, penempatan tabel) |
