# kotlinx.serialization: keep generated serializers for protocol messages.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class com.droidmic.core.**$$serializer { *; }
-keepclassmembers class com.droidmic.core.** { *** Companion; }
-keepclasseswithmembers class com.droidmic.core.** { kotlinx.serialization.KSerializer serializer(...); }
-keep class com.droidmic.app.data.StoredPairingJson { *; }
