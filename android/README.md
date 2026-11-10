# Starlight для Android

Android-приложение Starlight — это форк **PojavLauncher** (Minecraft: Java Edition для Android) с дизайном и брендом Starlight.
Запуск игры, Java-окружение, рендереры, ввод и управление профилями — архитектура PojavLauncher; оформление — из десктопного Starlight.

## Сборка

```bash
# нужны JDK 17 и Android SDK (platform 34, build-tools 34.0.0, NDK 25.2.9519653)
bash scripts/languagelist_updater.sh
./gradlew :app_pojavlauncher:assembleDebug        # APK: app_pojavlauncher/build/outputs/apk/debug/
```

* Только `arm64-v8a`.
* Ключ CurseForge: переменная окружения `CURSEFORGE_API_KEY` (без неё поиск CurseForge отключён, Modrinth работает).
* Java (JRE 8/17/21) собирается отдельным workflow `build-android-jre.yml` и кладётся в `app_pojavlauncher/src/main/assets/components/` (см. `../docs/ANDROID_FORK.md`).

## Происхождение и лицензия

См. [NOTICE.md](NOTICE.md). Исходный README проекта — [README.upstream.md](README.upstream.md).
Код этой папки распространяется на условиях **LGPL-3.0** (файл [LICENSE](LICENSE)).
