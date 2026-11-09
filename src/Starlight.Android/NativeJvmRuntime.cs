using System.Runtime.InteropServices;
using Starlight.Domain;
using Starlight.Platform;

namespace Starlight.Droid;

/// <summary>
/// Bridge to libstarlight_jvm.so — the native shim that boots the JVM *inside this process* (dlopen libjvm.so + JNI_CreateJavaVM), points
/// LWJGL at Android-compatible natives, and wires an OpenGL ES renderer + touch/keyboard input. Android forbids exec() of binaries from
/// app storage, which is why <c>Process.Start(java)</c> (what the desktop launcher does) cannot be used here.
/// The shim is NOT part of this repository yet: <see cref="TryCreate"/> returns null until the .so is bundled, and the UI then explains it.
/// Contract (see native/android/README.md):
/// <code>int starlight_run(const char* java_home, const char* work_dir, int argc, const char** argv, void (*log)(const char*));</code>
/// Blocks until the JVM exits and returns its exit code. <c>starlight_stop()</c> asks the game to close.
/// </summary>
internal sealed class NativeJvmRuntime : IGameRuntime
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback([MarshalAs(UnmanagedType.LPUTF8Str)] string line);

    [DllImport("starlight_jvm", CallingConvention = CallingConvention.Cdecl)]
    private static extern int starlight_run([MarshalAs(UnmanagedType.LPUTF8Str)] string javaHome, [MarshalAs(UnmanagedType.LPUTF8Str)] string workDir,
        int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] argv, LogCallback log);

    [DllImport("starlight_jvm", CallingConvention = CallingConvention.Cdecl)]
    private static extern void starlight_stop();

    public static NativeJvmRuntime? TryCreate()
        => NativeLibrary.TryLoad("starlight_jvm", typeof(NativeJvmRuntime).Assembly, null, out var handle) && handle != IntPtr.Zero
            ? new NativeJvmRuntime() : null;

    public async Task<int> RunAsync(LaunchPlan plan, Action<string> log, CancellationToken ct)
    {
        // plan.JavaPath is <jre>/bin/java; the shim wants the JRE home.
        var javaHome = Path.GetDirectoryName(Path.GetDirectoryName(plan.JavaPath))!;
        var args = plan.Arguments.ToArray();
        LogCallback callback = line => log(line);        // kept in a local so the GC cannot collect it during the native call
        using var registration = ct.Register(() => starlight_stop());
        var code = await Task.Run(() => starlight_run(javaHome, plan.WorkingDirectory, args.Length, args, callback), CancellationToken.None);
        GC.KeepAlive(callback);
        return code;
    }
}
