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

Catatan: selektor kotak pencarian pertama meleset (ternyata `<input>`, bukan contenteditable) dan diperbaiki setelah melihat halaman asli.
Risiko: otomasi tidak resmi melanggar ketentuan WhatsApp; nomor bisa diblokir. Folder profil Edge adalah kunci akun: jangan di-commit atau di-zip.
