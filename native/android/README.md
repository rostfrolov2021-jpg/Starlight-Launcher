# libstarlight_jvm.so — нативное ядро запуска (Android, arm64-v8a)

Это единственная часть мобильного порта, которой пока **нет в репозитории**. C# отдаёт ей готовую командную строку игры
(`LaunchPlan`, собранную CmlLib.Core), а она запускает JVM **внутри процесса приложения**.

## Контракт (см. `src/Starlight.Android/NativeJvmRuntime.cs`)

```c
// Блокирует до завершения JVM, возвращает код выхода. log вызывается построчно (stdout/stderr игры).
int  starlight_run(const char* java_home, const char* work_dir, int argc, const char** argv, void (*log)(const char*));
void starlight_stop(void);   // попросить игру закрыться (System.exit через JNI / закрытие окна)
```

## Что должно быть внутри

1. `dlopen("<java_home>/lib/server/libjvm.so")` → `JNI_CreateJavaVM` с аргументами из `argv` (`-Xmx…`, `-cp …`, главный класс и т.д.);
   затем `chdir(work_dir)` и вызов `main` главного класса через JNI.
2. **LWJGL для Android/arm64.** Natives, которые CmlLib скачивает по правилам Mojang, — десктопные (Linux/glibc x64) и на Android не загрузятся.
   Нужны LWJGL 3.x для Bionic/arm64 и подмена `-Dorg.lwjgl.librarypath=…`; для старых версий (<1.17) — LWJGL 2 → GLFW-обёртка.
3. **Рендер OpenGL ES.** Minecraft Java использует настольный OpenGL: нужен слой трансляции (gl4es / MobileGlues / ANGLE / Zink+Vulkan)
   и EGL-поверхность из `SurfaceView`/`NativeActivity`. Это основная сложность и источник проблем с производительностью.
4. **Ввод.** Сенсорные экранные кнопки/джойстик и клавиатура → события GLFW.
5. **FPS.** Считать вызовы `eglSwapBuffers` и раз в секунду писать число в `<DataRoot>/cache/fps` — оверлей лаунчера читает этот файл.
6. Сборка: NDK r26+, CMake, `-DANDROID_ABI=arm64-v8a`; готовый `.so` положить в `src/Starlight.Android/Native/arm64-v8a/`.

## JRE

Нужны сборки OpenJDK под Android/Bionic arm64 (JRE 8, 17, 21) в виде `jre-8-arm64.zip`, `jre-17-arm64.zip`, `jre-21-arm64.zip`
в `src/Starlight.Android/Assets/jre/`. Дистрибутивы Adoptium — это glibc-сборки для обычного Linux; на Android они не запустятся.
Если ваш источник отдаёт `.tar.xz`: `mkdir t && tar -xJf jre.tar.xz -C t && (cd t && zip -r ../jre-17-arm64.zip .)`.

## Готовая основа

Задача решена в проекте **PojavLauncher** (LGPL-3.0) и его форках — это ближайший образец архитектуры и источник готовых JRE/LWJGL/рендереров.
Перед использованием их кода или бинарников проверьте условия лицензии: LGPL накладывает требования на распространение.
