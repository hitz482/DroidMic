plugins {
    kotlin("jvm") version "2.1.21"
    kotlin("plugin.serialization") version "2.1.21"
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

java {
    // The Android app consumes this module; keep bytecode at Java 17.
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
    testImplementation(kotlin("test"))
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
}

tasks.test {
    useJUnit()
    // Shared protocol test vectors (also used by windows/tests).
    systemProperty("droidmic.vectors", rootProject.file("../shared/protocol-spec/test-vectors.json").absolutePath)
    testLogging { events("failed"); exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL }
}

// `gradle :core:simulatePhone --args="--host 127.0.0.1 --pin 123456"` runs a JVM phone
// simulator against a real receiver (used by scripts/interop-test.sh).
tasks.register<JavaExec>("simulatePhone") {
    group = "verification"
    classpath = sourceSets["main"].runtimeClasspath
    mainClass.set("com.droidmic.core.tools.SimulatedPhoneKt")
}
