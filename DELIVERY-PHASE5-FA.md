# تحویل آزمایشی فاز ۵ — MoveTrainer Native

این شاخه شامل MoveTrainer بومی WPF است و Viewer 2 طبق تصمیم صریح محصول کنار گذاشته شده است.

## اجرا

در PowerShell و از ریشه پروژه اجرا کنید:

```powershell
powershell -ExecutionPolicy Bypass -File .\verify-and-run-phase5.ps1
```

در صورت موفقیت Restore، Build و Test، برنامه اجرا می‌شود. از نوار Viewer دکمه **MoveTrainer** را بزنید.

## مسیر تست سریع

1. در MoveTrainer دکمه **باز کردن PGN…** را بزنید.
2. فایل `samples\phase5-movetrainer-smoke.pgn` را انتخاب کنید.
3. عنوان، سؤال، پاسخ‌ها و راهنماها را ویرایش و دوره را ذخیره کنید.
4. سمت تمرین، نوع زمان‌بندی و محدودیت‌های روزانه را تعیین کنید.
5. تمرین روزانه را شروع کنید؛ یک حرکت اشتباه و سپس پاسخ درست وارد کنید.
6. در پایان **Retry Mistakes** را بزنید.
7. برنامه را ببندید و دوباره باز کنید تا ماندگاری دوره، آمار و FSRS بررسی شود.

## گزارش خطا

اگر اسکریپت متوقف شد، فایل متناظر را ارسال کنید:

- `artifacts\verification\phase5\build.log`
- `artifacts\verification\phase5\test.log`

در این محیط Linux، .NET 10 SDK و WPF build در دسترس نبود؛ بنابراین موفقیت build نهایی باید با همین اسکریپت روی Windows تأیید شود. بررسی نحوی XAML و اجرای واقعی migration SQLite نسخه‌های ۱ تا ۴ انجام شده است.
