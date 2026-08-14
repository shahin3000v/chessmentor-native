# تحویل فاز ۲ — Viewer 1

این بسته جایگزین کامل پوشهٔ فاز ۱ است؛ آن را روی نسخهٔ قبلی کپی نکنید. ZIP را در یک پوشهٔ تازه Extract کنید.

## اجرای یک‌مرحله‌ای

در PowerShell داخل پوشهٔ `ChessMentor.NativeDesktop` اجرا کنید:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\verify-and-run-phase2.ps1
```

اسکریپت به‌ترتیب Restore، Build و Test را انجام می‌دهد. برنامه فقط وقتی اجرا می‌شود که هر سه مرحله بدون خطا تمام شوند. لاگ‌ها در مسیر زیر ساخته می‌شوند:

```text
artifacts\verification\phase2
```

اگر خطایی رخ داد، همان فایل `build.log` یا `test.log` را برای اصلاح بعدی ارسال کنید.

## آزمون دستی Viewer 1

فایل زیر را با دکمهٔ «باز کردن PGN» انتخاب کنید:

```text
samples\phase2-viewer-smoke.pgn
```

این فایل برای بررسی چندبازی، variation تو‌در‌تو، comment، NAG، شروع از FEN و شماره‌گذاری حرکت سیاه ساخته شده است.

## وضعیت پذیرش

- پیاده‌سازی سورس Viewer 1: موجود در بسته
- اعتبارسنجی XML، XAML handlerها، project referenceها و ZIP: انجام‌شده
- Build و تست روی Windows/.NET 10: گیت الزامی اسکریپت بالا؛ تا عبور آن، بسته «نامزد تحویل» است
- خروجی Self-contained: پس از پذیرش با `scripts\publish-windows-x64.ps1` ساخته می‌شود
