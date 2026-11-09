# Starlight Launcher → Android / iOS

Статус: **каркас порта готов, не проверен компиляцией** (в среде подготовки не было .NET SDK). Запуск игры на Android требует нативного
ядра, которого в репозитории ещё нет (см. «Что не сделано»). iOS — только режим менеджера сборок.

## 1. Анализ текущей архитектуры

| Слой | Что внутри | Переносимость |
|---|---|---|
| `Starlight.Domain` | модели, обновления, ключ релиза | ✅ как есть |
| `Starlight.Infrastructure` | CmlLib.Core 4.1, Modrinth/CurseForge, сборки `.mrpack`/CurseForge, `ModService` (транзакционная установка с зависимостями), `Downloads` (потоковая загрузка + хеши), `AccountVault`, Microsoft device-code вход | ✅ почти целиком (.NET 8) |
| `Starlight.Desktop` | Avalonia 11.3 + ReactiveUI, `UsePlatformDetect`, `StartWithClassicDesktopLifetime` | ♻️ XAML/VM переписаны под touch |
| Запуск игры | `Process.Start(java)` + Adoptium JRE для linux/win/mac | ❌ на мобильных не работает |
| Сервер, голосовой чат, апдейтер | `Process`, SDL-natives, самообновление | ⛔ только десктоп, на мобильных отключены |

**Стек: Avalonia 11 (Android + iOS) поверх того же .NET 8 ядра.** Вы уже на Avalonia, так что `Domain` и `Infrastructure` переиспользуются
без изменений, а UI — один общий проект `Starlight.Mobile` для обеих платформ. MAUI/Flutter/Kotlin потребовали бы переписать ядро.

## 2. Реалистичные границы платформ

* **Android — запуск возможен**, но не через `Process.Start`: Android запрещает `exec` бинарников из хранилища приложения. Путь —
  как в PojavLauncher: JVM загружается **внутри процесса** (`dlopen libjvm.so` + JNI), LWJGL заменяется на Android-версию, OpenGL
  транслируется в GLES/Vulkan. Это нативный код на C/C++ (`native/android/README.md`), а не часть .NET.
* **iOS — запуск Minecraft: Java Edition невозможен**: у сторонних приложений нет JIT, нет `fork/exec`, нет JVM под платформу, App Store это запрещает.
  iOS-приложение — компаньон: сборки, моды/текстурпаки/шейдеры, импорт сборок, настройки.
* Температуры: Android 10+ часто закрывает `/sys/class/thermal` для приложений (используется датчик батареи как запасной вариант);
  iOS отдаёт только грубое тепловое состояние. FPS — только от нативного ядра (оно считает `eglSwapBuffers`).

## 3. Что сделано

**Изменения ядра (обратно совместимы, десктоп не затронут):**
* `Instance` получил `JvmArgs` и `JavaMajor` (необязательные поля; старые `instance.json` читаются).
* `IJavaProvider` — `JavaService` его реализует; `GameService` принимает интерфейс, поэтому на Android подставляется встроенная JRE.
* `GameService.PrepareLaunchAsync` — устанавливает всё и возвращает `LaunchPlan` (java + аргументы) **без запуска процесса**;
  `LaunchAsync` для десктопа работает как раньше. Пользовательские JVM-аргументы применяются в обоих путях.
* `GameService.Tuning` — параллелизм установщика (было жёстко 8×6×256). `MobileTuning.For(ОЗУ)` даёт 2×2×64 / 3×4×128 / 4×6×192.
* `ResourceCatalog` — поиск/установка текстурпаков и шейдеров с Modrinth (моды — прежний `ModService`).

**Новые проекты:**
* `Starlight.Platform` — `IPlatformServices` (пути, устройство, мониторинг, JRE, запуск), `BundledJava` (потоковая распаковка JRE из APK, 64 КБ буфер,
  защита от path traversal, атомарная подмена каталога), `MobileTuning` (лимиты куч и параллелизма).
* `Starlight.Mobile` — общий UI: нижняя навигация на телефоне (< 720 dp) и боковая панель на планшете, списки сборок, слайдер RAM (шаг 256 МБ, потолок 55% ОЗУ
  и предупреждение при >45%), выбор Java, аргументы JVM, менеджер модов/текстурпаков/шейдеров, импорт `.mrpack`/CurseForge через системный выбор файла
  (SAF на Android, UIDocumentPicker на iOS), вход Microsoft по коду, оверлей FPS/температур/RAM, журнал игры с ограничением размера.
* `Starlight.Android` — APK (arm64), приватное хранилище `FilesDir`, датчики через `/proc` и батарею, мост `NativeJvmRuntime` к `libstarlight_jvm.so`.
* `Starlight.iOS` — приложение-компаньон, `Application Support`, тепловое состояние.
* `scripts/build-android.sh`, `scripts/build-ios.sh`, `.github/workflows/mobile.yml`.

**Файловая система и память (OOM):** все данные в приватном каталоге приложения (быстро, без разрешений, единственное место, где Android допускает исполняемый код).
Загрузка и распаковка — потоком с буфером 64–80 КБ; конкурентность установщика зависит от ОЗУ; лог ограничен 20 КБ; `LargeHeap` включён.
Выгрузка/загрузка для пользователя — только через системный выбор файла, без `MANAGE_EXTERNAL_STORAGE`.

## 4. Что не сделано / риски

1. **`libstarlight_jvm.so`** (JNI-запуск, LWJGL arm64, GLES-трансляция, ввод) — главная работа, недели, не .NET. Без него кнопка «Играть» объясняет причину.
2. **JRE под Android/Bionic** (8/17/21) в `Assets/jre/*.zip`. Adoptium-сборки для Linux на Android не подходят.
3. **Forge/NeoForge**: их установщики запускают `java -jar` через `Process.Start` — на Android нужно прогонять их через то же нативное ядро. **Fabric и Vanilla** — чистый JSON, без этой проблемы.
4. **Не компилировалось.** Ожидайте мелких правок после первого `dotnet build`; в частности проверьте `MArgument.FromCommandLine` (CmlLib 4.1) и API `Avalonia.Android/iOS 11.3.8`.
   Мобильные проекты собраны с `TreatWarningsAsErrors=false`; верните `true` после первой чистой сборки.
5. **Trimming/AOT на iOS**: `MtouchLink=None` пока включён; для публикации в Store добавьте `JsonSerializerContext` для моделей.
6. Обновления (`UpdateService`), голос (`SDL`), локальные серверы и сокет-вход Microsoft (`HttpListener`) на мобильных не используются.
7. Лицензии: ядро PojavLauncher — LGPL-3.0; Minecraft требует лицензии, офлайн-профиль её не заменяет (как и в README).
8. Каналы: Play Store / App Store отдельно проверяют лаунчеры чужой игры — публикация там не гарантирована; APK-распространение проще.

## 5. Сборка

```bash
# Android (нужны .NET 8, JDK 17)
./scripts/build-android.sh                       # debug-подпись, artifacts/android/*.apk
KEYSTORE=release.jks KEYSTORE_PASS=… KEY_ALIAS=starlight KEY_PASS=… ./scripts/build-android.sh   # релиз

# iOS (только macOS + Xcode)
./scripts/build-ios.sh sim                       # симулятор
CODESIGN_KEY="Apple Distribution: …" PROVISION="Starlight AdHoc" ./scripts/build-ios.sh device   # .ipa
```

Для мобильных проектов нужен **.NET SDK 8** (на SDK 10 они не собираются: `net8.0-android` помечен устаревшим, `Avalonia.Android 11.3.8` с ним несовместим); скрипты сами закрепляют SDK 8 через временный `global.json`.

Мобильные проекты намеренно **не добавлены в `Starlight.sln`**: иначе CI на Windows/Linux потребует мобильные workload’ы. Собираются по `.csproj` и workflow `mobile.yml`.

## 6. Порядок дальнейших работ

1. `dotnet build src/Starlight.Mobile` → поправить ошибки компиляции, затем Android-голову на эмуляторе/устройстве (менеджер сборок, моды, настройки).
2. Добавить JRE-архивы и собрать минимальный `libstarlight_jvm.so`, сначала запуск `java -version` и консольной сборки Fabric.
3. LWJGL arm64 + рендер → меню Minecraft → ввод → оверлей FPS.
4. Forge/NeoForge через нативное ядро; сохранение аккаунтов через `AccountVault` с паролем.
