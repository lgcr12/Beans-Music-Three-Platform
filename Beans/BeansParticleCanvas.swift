import SwiftUI

/// 轻量 Canvas 粒子层：只负责氛围和播放反馈，不参与布局，也不拦截触控。
struct BeansParticleCanvas: View {
    let accent: Color
    let secondary: Color
    var isPlaying: Bool = false
    var intensity: Double = 0.8

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @AppStorage("beans.particles.enabled") private var isEnabled = true

    private struct Particle {
        let x: Double
        let y: Double
        let size: Double
        let speed: Double
        let drift: Double
        let phase: Double
        let usesSecondary: Bool
    }

    private static let particles: [Particle] = (0..<34).map { index in
        let seed = Double((index * 7919 + 104729) % 1000) / 1000
        let seed2 = Double((index * 3571 + 7919) % 1000) / 1000
        let seed3 = Double((index * 6151 + 3571) % 1000) / 1000
        return Particle(
            x: 0.04 + seed * 0.92,
            y: 0.04 + seed2 * 0.92,
            size: 1.5 + seed3 * 4.5,
            speed: 0.12 + seed * 0.22,
            drift: 8 + seed2 * 18,
            phase: seed3 * .pi * 2,
            usesSecondary: index.isMultiple(of: 3)
        )
    }

    var body: some View {
        if !isEnabled {
            Color.clear
        } else {
            TimelineView(.animation(minimumInterval: reduceMotion ? 1.0 : 1.0 / 30.0, paused: reduceMotion)) { timeline in
                Canvas(opaque: false, colorMode: .extendedLinear, rendersAsynchronously: true) { context, size in
                    drawParticles(context: &context, size: size, time: timeline.date.timeIntervalSinceReferenceDate)
                }
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }

    private func drawParticles(context: inout GraphicsContext, size: CGSize, time: TimeInterval) {
        let motion = reduceMotion ? 0.0 : (isPlaying ? 1.0 : 0.18)
        let alpha = max(0.04, min(intensity, 1.0)) * (isPlaying ? 0.72 : 0.34)

        for particle in Self.particles {
            let angle = time * particle.speed + particle.phase
            let x = particle.x * size.width + cos(angle) * particle.drift * motion
            let y = particle.y * size.height + sin(angle * 0.83) * particle.drift * motion
            let pulse = 0.72 + 0.28 * sin(angle * 1.4)
            let radius = particle.size * (0.82 + pulse * 0.24)
            let rect = CGRect(x: x - radius, y: y - radius, width: radius * 2, height: radius * 2)
            let color = particle.usesSecondary ? secondary : accent
            context.fill(Path(ellipseIn: rect), with: .color(color.opacity(alpha * pulse)))
        }
    }
}
