# MAA-2102 fase lanjut: studi implementasi dan keamanan

## Permintaan

Sistem .NET mandiri dengan database SQL Server sendiri yang mengelola pesan masuk dan keluar (teks, audio, video) untuk beberapa nomor WhatsApp, dengan metode API supaya sistem web lain bisa mengirim pesan keluar dan membaca pesan masuk.

## Yang sudah dicakup POC

| Bagian | Status |
|---|---|
| Database SQL Server sendiri dengan tabel outbox dan inbound | Selesai, hanya teks |
| API untuk mengantre pesan dan membaca pesan masuk | Selesai di lab, belum ada autentikasi |
| Pacing: jam kirim, batas harian, batas gelombang, jeda, jitter acak | Selesai, untuk satu nomor |
| Konektor WhatsApp yang bisa diganti (interface dengan versi fake dan real) | Selesai |
| Beberapa nomor sekaligus | Belum: satu nomor = satu profil browser = satu worker |
| Audio dan video | Belum |
| Autentikasi API, akses per sistem, status pengiriman | Belum |
| Jalan di server | Belum diuji |

## Desain target

![fig:1]

ERP menjadi satu klien dari API. Tiap nomor punya worker, login, dan pacing sendiri. Pesan dan hasilnya lewat database, jadi worker yang restart tidak menghilangkan pesan yang sudah diantre.

## Pilihan konektor

| Pilihan | Apa itu | Media | Catatan |
|---|---|---|---|
| Otomasi browser (sekarang: Playwright dan Edge) | Mengendalikan WhatsApp Web seperti orang | Teks terbukti; audio dan video belum diuji | Satu browser per nomor. Memori per sesi belum diukur; tidak ada angka publik yang andal. Rusak saat tampilan halaman berubah (4 perbaikan dalam sehari di POC) |
| Baileys (TypeScript) | Bicara ke WhatsApp Web lewat WebSocket; login dengan QR atau kode pairing | README pustaka menjelaskan kirim audio, video, voice note, dan unduh media | Bukan .NET, jadi jalan sebagai layanan terpisah. Pengelola menyatakan tidak mendukung pemakaian yang melanggar ketentuan WhatsApp. Versi 7.0.0 punya perubahan yang memutus kompatibilitas |
| whatsmeow (Go) | Klien Go untuk API multi-device | README menyebut media didukung | Bukan .NET, jadi layanan terpisah |
| Port .NET dari Baileys: Baileys.NET 1.9.1, Dawa.WhatsApp | Port C# | Belum diperiksa | Baileys.NET: .NET 10, MIT, diperbarui 25 Apr 2026, total 2,2 ribu unduhan. README Dawa.WhatsApp menyebut tahap awal. Kelengkapan dan perawatannya belum diverifikasi |
| Port siap pakai whatsapp-web.js di NuGet (WhatsApp.Client) | Sudah dicoba di POC | Tidak berlaku | Gagal: kebijakan keamanan konten WhatsApp Web memblokir skripnya |

Rekomendasi: pertahankan konektor sekarang untuk teks (sudah terbukti) dan pertahankan interface-nya, sehingga konektor kedua bisa ditambahkan. Di Phase 0, uji satu konektor tingkat protokol untuk media dan beberapa nomor, lalu pilih berdasarkan hasil uji, bukan dokumentasi. Kedua jenis tidak resmi.

## Catatan implementasi (usulan, belum dibangun)

| Kebutuhan | Cara |
|---|---|
| Beberapa nomor | Tabel sesi (nomor, status, lokasi login, pengaturan). Satu worker per sesi dengan folder login atau auth state sendiri, pacing dan batas harian sendiri. Login QR per nomor dari dashboard atau API. Nomor yang sama tidak pernah dipakai di dua tempat |
| Kirim media | Baris outbox menunjuk ke catatan media. Worker mengunggah file lewat konektor. Ada batas ukuran dan tipe |
| Terima media | Worker mengunduh file dan menyimpannya di luar database (disk atau blob). Database menyimpan tipe, ukuran, hash, dan path. Unduhan gagal dicoba ulang dan ditandai |
| Kirim pesan keluar | POST dengan nomor, tujuan grup atau kontak, teks atau id media, dan kunci idempotensi opsional. Mengembalikan id. GET status: Pending, Sent, Failed |
| Baca pesan masuk | GET pesan masuk setelah kursor, disaring per nomor. Klien mengonfirmasi yang sudah diproses, jadi tidak ada yang terbaca dua kali atau hilang. Webhook sebagai opsi nanti |
| Tata letak proses | Worker jalan sebagai layanan sendiri, bukan di dalam ERP. API tetap hidup saat worker restart |

## Keamanan: akun (risiko blokir)

Isi sumber: Ketentuan Layanan WhatsApp melarang pesan massal, pesan otomatis, dan pembuatan akun otomatis. Pedoman Pesan (diperbarui 23 Sep 2026) mencantumkan klien tidak resmi, pesan massal, dan otomasi di bawah perilaku adversarial, dengan batasan "to harm WhatsApp or our users", dan menyebut akun bisa ditangguhkan sementara atau permanen. Kedua jenis konektor tidak resmi, jadi tidak ada yang menghilangkan risiko. Artikel vendor menyebut blokir sering mendadak tanpa peringatan; sumbernya perusahaan yang menjual API resmi, jadi hanya sebagai petunjuk. Tidak ditemukan aturan publik tentang laju kirim yang aman. Pengaturan POC (10 sampai 15 per hari, gelombang) berasal dari saran tim dan belum diverifikasi.

| Langkah | Alasan |
|---|---|
| Pakai nomor khusus, bukan nomor pribadi | Blokir hanya mengorbankan nomor itu (pelajaran POC: nomor yang tertaut adalah nomor pribadi) |
| Kirim hanya ke grup dan orang yang memang menunggu pesan | Pedoman menyebut kontak berulang yang tidak diinginkan sebagai spam |
| Pacing per nomor: jam, batas harian, gelombang, jeda, jitter | Menghindari lonjakan seperti mesin (sudah diterapkan) |
| Satu worker per nomor, tidak ada login kedua untuk nomor yang sama | Menghindari sesi yang bentrok |
| Jangan membuka dan menutup koneksi berulang cepat | Satu artikel vendor mencantumkannya sebagai pola mencurigakan (satu sumber) |
| Tombol darurat per nomor; berhenti otomatis saat logout atau gagal berulang | Menghentikan sebelum kerusakan membesar |
| Pantau pengiriman, kegagalan, dan logout per nomor | Masalah ketahuan lebih awal |

## Keamanan: sistem

| Area | Langkah |
|---|---|
| Akses API | API key per sistem pemanggil, disimpan ter-hash, dibatasi ke nomor yang diizinkan. HTTPS. Pembatasan laju per key |
| Bahan login | Profil browser atau auth state adalah kunci akun: disk terenkripsi, akses terbatas, tidak pernah di git, zip, atau cadangan yang dibagikan |
| Rahasia | Connection string dan key di luar repositori (vault atau environment) |
| Data pribadi | Pesan grup bersifat pribadi. Daftar grup yang diizinkan dibaca per nomor (pelajaran POC: penerimaan menyimpan pesan dari grup yang tidak terkait), masa simpan, log akses |
| File media | Batas ukuran dan tipe, pindai malware, tidak pernah dieksekusi, disimpan di luar web root |
| Audit | Catat siapa mengirim apa, ke nomor mana, kapan. Isi pesan tidak ditulis ke log aplikasi |
| Ketahanan | Outbox ada di database; worker crash tidak menghilangkan apa pun; coba ulang sampai 3 kali (sudah diterapkan) |

## Rencana pembuktian Phase 0

| # | Uji | Selesai bila |
|---|---|---|
| 1 | Beberapa nomor paralel | 3 nomor tertaut dan mengirim; memori dan CPU per sesi terukur |
| 2 | Kirim audio dan video | Satu audio dan satu video terkirim ke grup uji |
| 3 | Terima audio dan video | Media dari nomor lain tersimpan dengan tipe, ukuran, dan hash benar |
| 4 | Jalan di server | Worker jalan tanpa jendela selama beberapa hari; login bertahan setelah restart |
| 5 | Konektor kedua | Uji yang sama dengan satu pustaka tingkat protokol, dibandingkan dengan konektor browser |
| 6 | API dan keamanan | Dua klien dengan key terpisah; masing-masing hanya melihat nomornya sendiri |

Setiap uji dicatat di file hasil. Estimasi baru diberikan setelah semua ini selesai.

## Sumber

| Sumber | Tautan |
|---|---|
| Ketentuan Layanan WhatsApp (berlaku 4 Jan 2021) | https://www.whatsapp.com/legal/terms-of-service |
| Pedoman Pesan WhatsApp (diperbarui 23 Sep 2026) | https://www.whatsapp.com/legal/messaging-guidelines |
| Baileys | https://github.com/WhiskeySockets/Baileys |
| whatsmeow | https://github.com/tulir/whatsmeow |
| Baileys.NET (NuGet) | https://www.nuget.org/packages/Baileys.NET |
| Dawa.WhatsApp (NuGet) | https://www.nuget.org/packages/Dawa.WhatsApp |
| Artikel vendor tentang risiko API tidak resmi (hanya petunjuk) | https://www.bot.space/blog/whatsapp-api-vs-unofficial-tools-a-complete-risk-reward-analysis-for-2025 |
