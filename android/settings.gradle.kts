pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "DroidMic"

// `:core` is plain Kotlin/JVM (protocol, crypto, session logic) and builds without the
// Android SDK. Pass -PcoreOnly=true to build/test it on machines without an SDK.
include(":core")
if (providers.gradleProperty("coreOnly").orNull != "true") {
    include(":app")
}
