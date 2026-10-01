# libv2ray exposes the Go core through JNI. Keep the Java bridge and callback
# signatures stable for the native layer when R8 is enabled.
-keep class libv2ray.** { *; }

# Kotlin Serialization generates serializer entry points that are discovered
# from serializable models and should remain available in release builds.
-keepclassmembers,allowoptimization class ** {
    kotlinx.serialization.KSerializer serializer(...);
}

# Completely strip Verbose and Debug logs from the release binary to reduce
# size and eliminate per-second traffic-stat I/O overhead.
-assumenosideeffects class android.util.Log {
    public static int v(...);
    public static int d(...);
}
