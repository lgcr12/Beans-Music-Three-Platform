import SwiftUI

enum PlayerVisualMode: String, CaseIterable, Identifiable, Codable {
    case quiet
    case flow
    case stardust
    case spectrum
    case vinyl

    var id: String { rawValue }

    var title: String {
        switch self {
        case .quiet: return "静谧"
        case .flow: return "流光"
        case .stardust: return "星尘波纹"
        case .spectrum: return "频谱舞台"
        case .vinyl: return "经典黑胶"
        }
    }

    var icon: String {
        switch self {
        case .quiet: return "moon.stars"
        case .flow: return "water.waves"
        case .stardust: return "sparkles"
        case .spectrum: return "waveform.path.ecg"
        case .vinyl: return "record.circle"
        }
    }

    static var migratedDefaultRawValue: String {
        let defaults = UserDefaults.standard
        if let value = defaults.string(forKey: "beans.playerVisualMode"), rawValueIsValid(value) {
            return value
        }
        return defaults.bool(forKey: "beans.djVisual") ? spectrum.rawValue : flow.rawValue
    }

    private static func rawValueIsValid(_ value: String) -> Bool {
        PlayerVisualMode(rawValue: value) != nil
    }
}

struct PlayerVisualEffectsLayer: View {
    let mode: PlayerVisualMode
    let accent: Color
    let secondary: Color
    let isPlaying: Bool
    let reduceMotion: Bool
    let intensity: Double
    let spectrumLevels: [Double]?
    let framesPerSecond: Double

    private var animates: Bool { isPlaying && !reduceMotion }

    @ViewBuilder
    var body: some View {
        switch mode {
        case .quiet:
            Color.clear
        case .flow:
            AmbientGlowView(accent: accent, secondary: secondary, isPlaying: animates, breath: intensity)
        case .stardust:
            AmbientGlowView(accent: accent, secondary: secondary, isPlaying: animates, breath: intensity * 0.85)
            BeansParticleCanvas(
                accent: accent,
                secondary: secondary,
                isPlaying: animates,
                intensity: intensity,
                pauseWhenIdle: true,
                maxFramesPerSecond: framesPerSecond
            )
                .opacity(0.82)
            PlayerRippleCanvas(accent: accent, secondary: secondary, isPlaying: animates, intensity: intensity, framesPerSecond: framesPerSecond)
        case .spectrum:
            AmbientGlowView(accent: accent, secondary: secondary, isPlaying: animates, breath: intensity * 0.7)
            PlayerSpectrumCanvas(accent: accent, secondary: secondary, isPlaying: animates, intensity: intensity, levels: spectrumLevels, framesPerSecond: framesPerSecond)
            DJVisualView(accent: accent, secondary: secondary, isPlaying: animates, intensity: intensity * 0.7)
        case .vinyl:
            AmbientGlowView(accent: accent, secondary: secondary, isPlaying: animates, breath: intensity * 0.55)
            VinylGrooveCanvas(accent: accent, secondary: secondary, isPlaying: animates, intensity: intensity, framesPerSecond: framesPerSecond)
        }
    }
}

private struct PlayerRippleCanvas: View {
    let accent: Color
    let secondary: Color
    let isPlaying: Bool
    let intensity: Double
    let framesPerSecond: Double

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / framesPerSecond, paused: !isPlaying)) { timeline in
            Canvas { context, size in
                let t = isPlaying ? timeline.date.timeIntervalSinceReferenceDate : 0.35
                let center = CGPoint(x: size.width / 2, y: size.height * 0.42)
                let maximum = min(size.width, size.height) * 0.58
                for index in 0..<4 {
                    let phase = (t * 0.24 + Double(index) * 0.25).truncatingRemainder(dividingBy: 1)
                    let radius = maximum * (0.2 + phase * 0.8)
                    let rect = CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
                    context.stroke(
                        Path(ellipseIn: rect),
                        with: .color((index.isMultiple(of: 2) ? accent : secondary).opacity((1 - phase) * 0.22 * intensity)),
                        lineWidth: 1.2
                    )
                }
            }
        }
        .allowsHitTesting(false)
        .drawingGroup()
    }
}

private struct PlayerSpectrumCanvas: View {
    let accent: Color
    let secondary: Color
    let isPlaying: Bool
    let intensity: Double
    let levels: [Double]?
    let framesPerSecond: Double

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / framesPerSecond, paused: !isPlaying)) { timeline in
            spectrumFrame(time: isPlaying ? timeline.date.timeIntervalSinceReferenceDate : 1.2)
        }
        .allowsHitTesting(false)
        .drawingGroup()
    }

    private func spectrumFrame(time: TimeInterval) -> some View {
        Canvas { context, size in
            drawSpectrum(context: &context, size: size, time: time)
        }
    }

    private func drawSpectrum(context: inout GraphicsContext, size: CGSize, time: TimeInterval) {
        let count = max(14, min(34, Int(size.width / 22)))
        let usableWidth = size.width * 0.72
        let countValue = CGFloat(count)
        let barWidth = max(CGFloat(3), usableWidth / countValue * 0.46)
        let spacing = usableWidth / countValue
        let originX = (size.width - usableWidth) / 2
        let baseline = size.height * 0.78

        for index in 0..<count {
            let x = originX + CGFloat(index) * spacing
            let frequency = 1.7 + Double(index % 5) * 0.12
            let sampled = levels.flatMap { values -> Double? in
                guard !values.isEmpty else { return nil }
                return values[min(values.count - 1, index * values.count / count)]
            }
            let wave = sampled ?? abs(sin(time * frequency + Double(index) * 0.72))
            let envelope = sampled == nil ? 0.34 + 0.66 * sin(Double(index) / Double(count) * .pi) : 1
            let normalizedHeight = CGFloat((sampled == nil ? 0.18 : 0.04) + wave * envelope * intensity)
            let barHeight = size.height * 0.19 * normalizedHeight
            let rect = CGRect(x: x, y: baseline - barHeight, width: barWidth, height: max(3, barHeight))
            let color = index.isMultiple(of: 3) ? secondary : accent
            let shape = Path(roundedRect: rect, cornerRadius: barWidth / 2)
            context.fill(shape, with: .color(color.opacity(0.42)))
        }
    }
}

private struct VinylGrooveCanvas: View {
    let accent: Color
    let secondary: Color
    let isPlaying: Bool
    let intensity: Double
    let framesPerSecond: Double

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / framesPerSecond, paused: !isPlaying)) { timeline in
            Canvas { context, size in
                let t = isPlaying ? timeline.date.timeIntervalSinceReferenceDate : 0
                let center = CGPoint(x: size.width * 0.5, y: size.height * 0.42)
                let base = min(size.width, size.height) * 0.13
                for index in 0..<10 {
                    let radius = base + Double(index) * base * 0.19
                    let rect = CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
                    let shimmer = 0.05 + 0.08 * abs(sin(t * 0.8 + Double(index) * 0.45))
                    context.stroke(Path(ellipseIn: rect), with: .color((index.isMultiple(of: 4) ? accent : secondary).opacity(shimmer * intensity)), lineWidth: 0.8)
                }
            }
        }
        .allowsHitTesting(false)
        .drawingGroup()
    }
}
