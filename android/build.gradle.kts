// All Gradle plugins are put on the root build classpath so Kotlin and the Android
// Gradle Plugin share one classloader. AGP is only added when the :app module is
// part of the build; `-PcoreOnly=true` builds (no Android SDK) never resolve it.
buildscript {
    val kotlinVersion = "2.1.21"
    val agpVersion = "8.7.3"
    val coreOnly = providers.gradleProperty("coreOnly").orNull == "true"
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
    dependencies {
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:$kotlinVersion")
        classpath("org.jetbrains.kotlin:kotlin-serialization:$kotlinVersion")
        if (!coreOnly) {
            classpath("org.jetbrains.kotlin:compose-compiler-gradle-plugin:$kotlinVersion")
            classpath("com.android.tools.build:gradle:$agpVersion")
        }
    }
}
