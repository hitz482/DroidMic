package com.droidmic.app.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

object DroidMicColors {
    val Background = Color(0xFF0F1318)
    val Surface = Color(0xFF171C24)
    val SurfaceHigh = Color(0xFF1F2630)
    val Outline = Color(0xFF2A3340)
    val Text = Color(0xFFE6EAF0)
    val Muted = Color(0xFF8A94A6)
    val Accent = Color(0xFF4CC2FF)
    val Good = Color(0xFF3DD68C)
    val Warn = Color(0xFFF5B841)
    val Bad = Color(0xFFFF6B6B)
}

private val Scheme = darkColorScheme(
    primary = DroidMicColors.Accent,
    onPrimary = Color(0xFF00121D),
    primaryContainer = Color(0xFF0E3A52),
    onPrimaryContainer = Color(0xFFCDEBFF),
    secondary = DroidMicColors.Good,
    background = DroidMicColors.Background,
    onBackground = DroidMicColors.Text,
    surface = DroidMicColors.Surface,
    onSurface = DroidMicColors.Text,
    surfaceVariant = DroidMicColors.SurfaceHigh,
    onSurfaceVariant = DroidMicColors.Muted,
    surfaceContainer = DroidMicColors.Surface,
    surfaceContainerHigh = DroidMicColors.SurfaceHigh,
    outline = DroidMicColors.Outline,
    error = DroidMicColors.Bad,
)

private val AppTypography = Typography(
    headlineMedium = TextStyle(fontSize = 26.sp, fontWeight = FontWeight.SemiBold),
    titleLarge = TextStyle(fontSize = 20.sp, fontWeight = FontWeight.SemiBold),
    titleMedium = TextStyle(fontSize = 16.sp, fontWeight = FontWeight.SemiBold),
    bodyLarge = TextStyle(fontSize = 16.sp),
    bodyMedium = TextStyle(fontSize = 14.sp),
    labelMedium = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Medium),
)

@Composable
fun DroidMicTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = Scheme, typography = AppTypography, content = content)
}
