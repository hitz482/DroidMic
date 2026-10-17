package com.droidmic.app.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.droidmic.app.ui.theme.DroidMicColors

/** Horizontal peak/RMS meter, -60..0 dBFS. */
@Composable
fun LevelMeter(peakDb: Double, rmsDb: Double, active: Boolean, modifier: Modifier = Modifier) {
    val peak = if (active) ((peakDb + 60) / 60).coerceIn(0.0, 1.0).toFloat() else 0f
    val rms = if (active) ((rmsDb + 60) / 60).coerceIn(0.0, 1.0).toFloat() else 0f
    val gradient = Brush.horizontalGradient(
        0f to DroidMicColors.Good, 0.7f to DroidMicColors.Good, 0.88f to DroidMicColors.Warn, 1f to DroidMicColors.Bad,
    )
    val label = if (active && peakDb > -89) "Input level %.0f dBFS".format(peakDb) else "No input"
    Column(modifier.semantics { contentDescription = label }) {
        Canvas(Modifier.fillMaxWidth().height(16.dp)) {
            val r = CornerRadius(6.dp.toPx())
            drawRoundRect(DroidMicColors.SurfaceHigh, cornerRadius = r)
            if (peak > 0f) drawRoundRect(gradient, size = Size(size.width * peak, size.height), cornerRadius = r)
        }
        Spacer(Modifier.height(4.dp))
        Canvas(Modifier.fillMaxWidth().height(5.dp)) {
            val r = CornerRadius(3.dp.toPx())
            drawRoundRect(DroidMicColors.SurfaceHigh, cornerRadius = r)
            if (rms > 0f) drawRoundRect(gradient, size = Size(size.width * rms, size.height), cornerRadius = r, alpha = 0.7f)
        }
    }
}

@Composable
fun StatusDot(color: Color, size: Dp = 10.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(color))
}

@Composable
fun SectionCard(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
    ) {
        Column(Modifier.padding(18.dp)) { content() }
    }
}

@Composable
fun StatRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(12.dp))
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
fun Hint(text: String, color: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    Text(text, style = MaterialTheme.typography.bodyMedium, color = color)
}
