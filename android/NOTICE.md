# Происхождение

Эта папка — производная работа от **PojavLauncher** (https://github.com/PojavLauncherTeam/PojavLauncher),
коммит `b12ad04` (23.09.2025, проект помечен как «Discontinued»), лицензия **GNU LGPL v3.0**.
Авторские права на исходный код принадлежат авторам PojavLauncher; полный текст лицензии — в файле `LICENSE`.

## Что изменено в Starlight

* идентификатор приложения `company.starlight.launcher`, название «Starlight», иконка;
* цветовая схема из десктопного Starlight;
* только архитектура `arm64-v8a` (удалены библиотеки и сборка для armeabi-v7a, x86, x86_64);
* удалены ключ подписи Google Play и сборка `gplay`, принадлежавшие авторам исходного проекта;
* удалены файлы политики конфиденциальности и конфигурация Crowdin оригинала.

Готовые нативные библиотеки (`app_pojavlauncher/src/main/jniLibs/arm64-v8a`), JAR-компоненты LWJGL/Cacio и GLFW-заглушка
взяты из того же коммита PojavLauncher без изменений. Java-окружение (OpenJDK 8/17/21 под Android) собирается в CI из
https://github.com/PojavLauncherTeam/android-openjdk-build-multiarch (см. `.github/workflows/build-android-jre.yml`).

Minecraft — торговая марка Mojang/Microsoft. Starlight — независимый проект, не связан с Mojang, Microsoft или авторами PojavLauncher.
