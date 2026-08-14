# تحویل فاز ۳ — PGN Studio و Translation

این شاخه نامزد پذیرش فاز ۳ است. Viewer 1 فاز ۲ حفظ شده و Studio Native از دکمهٔ **PGN Studio** در Header باز می‌شود.

## اجرای یک‌مرحله‌ای

در PowerShell داخل ریشهٔ پروژه اجرا کنید:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\verify-and-run-phase3.ps1
```

اسکریپت Restore، Build و Test را اجرا می‌کند و فقط پس از عبور همهٔ Gateها برنامه را باز می‌کند. لاگ‌ها اینجا هستند:

```text
artifacts\verification\phase3
```

برای تست دستی Studio فایل زیر را باز کنید:

```text
samples\phase3-studio-translation-smoke.pgn
```

شناسهٔ Draft و Course منتشرشده جدا نگه‌داری می‌شوند و Audioهای آفلاین پس از حذف یا جابه‌جایی بازی‌ها با `gameId` پایدار به index درست متصل می‌شوند.

اگر Build یا Test خطا داد، فایل `build.log` یا `test.log` همان پوشه را ارسال کنید.
