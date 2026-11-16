# Hasil POC nyata / Real POC results (14 Nov 2026)

| # | Ujian / Test | Hasil / Result |
|---|---|---|
| 1 | Pustaka NuGet WhatsApp.Client 1.0.7, tautkan QR | GAGAL: CSP WhatsApp Web memblokir skripnya (`unsafe-eval`); tanpa opsi bypass. Chromium unduhan Playwright 120 juga tidak mau jalan (side-by-side), Edge terpasang dipakai. |
| 2 | Playwright langsung + Edge, `BypassCSP`, argumen `--disable-quic` | BERHASIL membuka WhatsApp Web. Tanpa `--disable-quic`: `ERR_QUIC_PROTOCOL_ERROR` di jaringan ini. |
| 3 | QR tampil di dashboard, dipindai dengan HP | BERHASIL: Linked = Yes. |
| 4 | Baca daftar chat | BERHASIL: 4 entri terbaca (campur chat biasa dan grup; belum bisa memilah grup). |
| 5 | Kirim satu pesan ke grup "Advancer Wa test" lewat antrean (Enabled, batas 5/hari) | BERHASIL: status Sent; tangkapan layar WhatsApp Web menunjukkan "You: Tes dari ERP lab" dengan centang dua (14:14). |
| 6 | Restart program, apakah login bertahan | BERHASIL: Linked = Yes tanpa QR baru (profil Edge). |
| 7 | Pesan masuk (butir 4 tiket) | BELUM dibuat. |
| 8 | Jumlah anggota grup, grup "IT Batam" (butir 2) | BELUM: nomor ini tidak berada di IT Batam; jumlah anggota belum dibaca. |

| 9 | Login dari dashboard: tombol Connect / Disconnect / Forget login | BERHASIL: setelah restart status "Not connected", tekan Connect, langsung Linked tanpa QR baru. Forget login belum dicoba (menghapus login). |
| 10 | Pacing (saran tim: 10-15 per hari, gelombang tidak teratur): jam kirim, gelombang maks N per M menit, jeda acak | BERHASIL di klien Fake: smoke test 16/16 (13 lama + 3 baru). Dengan WhatsApp sungguhan belum diuji dalam waktu lama. |
| 11 | Pengguna mengirim 6 pesan uji lewat dashboard ke grup "Advancer Wa test" | Terkirim (status Sent, 7 terkirim total). |

| 12 | 15 Nov: pesan pending terus | Dua sebab dan diperbaiki: (a) balon petunjuk WhatsApp menutupi kotak pencarian, klik timeout; sekarang Escape + fokus. (b) pesan galat panjang melebihi kolom 1000 karakter sehingga simpan gagal dan pesan tetap Pending; sekarang dipotong. Setelah itu 3 pesan terkirim. |
| 13 | Pacing nyata | 3 pesan keluar lalu gelombang penuh (3 per 90 menit) dan jam kirim 08-18 menahan 4 sisanya sampai besok 08.00: sesuai rancangan. |
| 14 | Daftar grup asli dan jumlah anggota (butir 2) | BERHASIL: filter "Groups" bekerja; "Advancer Wa test" = 3 anggota (dibaca dari panel info grup). Grup 30 anggota belum diuji. |
| 15 | Menerima pesan grup (butir 4) | DIBANGUN, BELUM TERBUKTI: membaca grup berlencana belum dibaca lalu membuka chat dan mengambil pesan masuk. Perlu orang lain menulis di grup. Efek samping: membuka chat menandainya terbaca di HP. |
| 16 | Klien Fake dipisahkan | Bawaan aplikasi sekarang Real + database WhatsAppLabReal, tanpa data/alat Fake di dashboard. Smoke test (16/16) memakai Fake sendiri di port 5310 dan database WhatsAppLab, tidak menyentuh WhatsApp. |

| 17 | Browser tersembunyi (headless), bawaan; `WhatsApp:ShowBrowser=true` untuk menampilkan | BERHASIL: tertaut dengan login lama, daftar grup dan jumlah anggota (3) terbaca tanpa jendela. Temuan: dialog "WhatsApp is open in another window" menahan halaman sampai ditekan "Use here" (sekarang otomatis); jumlah anggota di mode tersembunyi lebih lambat muncul (sekarang menunggu sampai 8 detik). |
| 18 | 16 Nov, antrean dibersihkan | 6 pesan Pending duplikat ditandai Failed ("Dibatalkan operator"), tidak dihapus. Cadangan: `backups/outbox-sebelum-poc-16nov.csv`. Pengaturan aman disimpan di `backups/settings-preset-aman.json`; gelombang dan jeda dilonggarkan sementara untuk uji (jam kirim 8-18 tetap). |
| 19 | 16 Nov, pemicu SQL Server (butir 3) dengan WhatsApp sungguhan | BERHASIL: insert langsung ke tabel outbox (Source=SqlTrigger) 01:37:38 UTC, terkirim 01:37:43 UTC (5 detik), Attempts 1. |
| 20 | 16 Nov, pesan terjadwal | BERHASIL: dijadwalkan 01:40:19 UTC lewat API, terkirim 01:40:26 UTC. |
| 21 | 16 Nov, pengumuman berulang Hangfire (cron) | BERHASIL: cron `41 1 * * *` menulis baris outbox (Source=Scheduled) pukul 01:41 UTC, terkirim 01:41:09 UTC. Job dihapus setelah uji. |
| 22 | 16 Nov, terima pesan grup (butir 4) | BERHASIL setelah empat perbaikan, dari WhatsApp kedua milik pengguna: "Masuk" (02:18 UTC) dan "Pesan masuk" (02:22 UTC) tercatat di tabel Inbound, satu kali tiap pesan, pesan lab sendiri tidak ikut tercatat. Temuan: (a) kotak pencarian yang terisi membuat daftar chat tanpa lencana belum dibaca; (b) lencana hilang kalau HP sudah menampilkan grup, jadi grup juga dibuka saat barisnya berubah; (c) id pesan tidak lagi berawalan false_/true_ dan kelas message-in/out sudah tidak ada: arah dikenali dari ikon `tail-out` dan label "You:/Delivered/Read/Sent"; (d) galat null saat baris berubah tanpa lencana. Batas: kunjungan pertama ke sebuah grup setelah program menyala hanya mengambil sejumlah lencana (minimal 1) pesan terakhir; kunjungan berikutnya mengambil semua pesan yang belum terlihat. Banyak pesan beruntun sekaligus belum diuji. Membuka grup menandainya terbaca di HP. |
| 23 | 16 Nov, kirim di mode tersembunyi (headless) | BERHASIL: semua kiriman 16 Nov (3 pesan pukul 08.34 WIB dan uji 19-21) keluar saat program berjalan tanpa jendela. BELUM diuji di mode tersembunyi: QR baru dan pesan masuk. |

Catatan: selektor kotak pencarian pertama meleset (ternyata `<input>`, bukan contenteditable) dan diperbaiki setelah melihat halaman asli.
Risiko: otomasi tidak resmi melanggar ketentuan WhatsApp; nomor bisa diblokir. Folder profil Edge adalah kunci akun: jangan di-commit atau di-zip.
