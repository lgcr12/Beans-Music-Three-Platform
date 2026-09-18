import SwiftUI
import Combine

/// 用户选择需要显示的平台。只控制入口可见性，不删除平台能力。
final class PlatformPreferenceStore: ObservableObject {
    static let shared = PlatformPreferenceStore()

    private static let key = "beans.enabledPlatforms.v1"

    @Published private(set) var selectedRaw: Set<String>

    var changes: AnyPublisher<Set<String>, Never> { $selectedRaw.eraseToAnyPublisher() }

    private init() {
        let saved = UserDefaults.standard.stringArray(forKey: Self.key) ?? []
        if saved.isEmpty {
            selectedRaw = Set(SearchProvider.allCases.map(\.rawValue))
        } else {
            selectedRaw = Set(saved)
        }
        normalize()
    }

    var enabledSearchProviders: [SearchProvider] {
        let list = SearchProvider.allCases.filter { selectedRaw.contains($0.rawValue) }
        return list.isEmpty ? [.netease] : list
    }

    var enabledLibraryProviders: [LibraryProvider] {
        LibraryProvider.allCases.filter { selectedRaw.contains($0.searchProvider.rawValue) }
    }

    var summaryText: String {
        enabledSearchProviders.map(\.rawValue).joined(separator: " / ")
    }

    func isEnabled(_ provider: SearchProvider) -> Bool {
        selectedRaw.contains(provider.rawValue)
    }

    func isEnabled(_ provider: LibraryProvider) -> Bool {
        isEnabled(provider.searchProvider)
    }

    func set(_ provider: SearchProvider, enabled: Bool) {
        if enabled {
            selectedRaw.insert(provider.rawValue)
        } else if selectedRaw.count > 1 {
            selectedRaw.remove(provider.rawValue)
        }
        normalize()
        save()
    }

    func ensureVisible(_ provider: SearchProvider) -> SearchProvider {
        isEnabled(provider) ? provider : enabledSearchProviders.first ?? .netease
    }

    func ensureVisible(_ provider: LibraryProvider) -> LibraryProvider {
        isEnabled(provider) ? provider : enabledLibraryProviders.first ?? .netease
    }

    func resetToDefault() {
        selectedRaw = Set(SearchProvider.allCases.map(\.rawValue))
        save()
    }

    var syncPayload: BeansPreferenceSyncPayload {
        let defaults = UserDefaults.standard
        return BeansPreferenceSyncPayload(
            enabledPlatforms: selectedRaw.sorted(),
            playerEffectMode: defaults.string(forKey: "beans.playerVisualMode") ?? PlayerVisualMode.migratedDefaultRawValue,
            playerEffectIntensity: defaults.object(forKey: "beans.djVisualIntensity") as? Double ?? 0.8,
            lyricStylePreset: defaults.string(forKey: "beans.lyricStylePreset") ?? LyricStylePreset.flow.rawValue,
            lyricFontSize: defaults.object(forKey: "beans.lyricFontSize") as? Int ?? 17,
            lyricLineSpacing: defaults.object(forKey: "beans.lyricSpacing") as? Int ?? 24,
            lyricTranslation: defaults.object(forKey: "beans.lyricTranslation") as? Bool ?? true,
            lyricAlignment: defaults.string(forKey: "beans.lyricAlignRaw") ?? "center"
        )
    }

    func applyRemote(_ value: BeansPreferenceSyncPayload) {
        selectedRaw = Set(value.enabledPlatforms)
        normalize()
        let defaults = UserDefaults.standard
        defaults.set(Array(selectedRaw), forKey: Self.key)
        if let mode = value.playerEffectMode, PlayerVisualMode(rawValue: mode) != nil { defaults.set(mode, forKey: "beans.playerVisualMode") }
        if let intensity = value.playerEffectIntensity { defaults.set(min(max(intensity, 0.2), 1), forKey: "beans.djVisualIntensity") }
        if let preset = value.lyricStylePreset, LyricStylePreset(rawValue: preset) != nil { defaults.set(preset, forKey: "beans.lyricStylePreset") }
        if let size = value.lyricFontSize { defaults.set(min(max(size, 12), 40), forKey: "beans.lyricFontSize") }
        if let spacing = value.lyricLineSpacing { defaults.set(min(max(spacing, 14), 40), forKey: "beans.lyricSpacing") }
        if let translation = value.lyricTranslation { defaults.set(translation, forKey: "beans.lyricTranslation") }
        if let alignment = value.lyricAlignment, alignment == "center" || alignment == "left" { defaults.set(alignment, forKey: "beans.lyricAlignRaw") }
    }

    func syncPlayerPreferences() {
        let payload = syncPayload
        Task { @MainActor in
            BeansAccountStore.shared.queueSync(entityType: "preference", entityID: "platforms", payload: payload)
        }
    }

    private func normalize() {
        let allowed = Set(SearchProvider.allCases.map(\.rawValue))
        selectedRaw = selectedRaw.intersection(allowed)
        if selectedRaw.isEmpty {
            selectedRaw = [SearchProvider.netease.rawValue]
        }
    }

    private func save() {
        UserDefaults.standard.set(Array(selectedRaw), forKey: Self.key)
        let payload = syncPayload
        Task { @MainActor in
            BeansAccountStore.shared.queueSync(entityType: "preference", entityID: "platforms", payload: payload)
        }
    }
}

extension LibraryProvider {
    var searchProvider: SearchProvider {
        switch self {
        case .netease: return .netease
        case .qq: return .qq
        case .kugou: return .kugou
        }
    }
}

struct PlatformPreferencePicker: View {
    @ObservedObject private var store = PlatformPreferenceStore.shared

    var body: some View {
        VStack(spacing: 10) {
            ForEach(SearchProvider.allCases) { provider in
                Button {
                    BeansHaptics.select()
                    store.set(provider, enabled: !store.isEnabled(provider))
                } label: {
                    HStack(spacing: 12) {
                        if let imageName = provider.brandImageName {
                            Image(imageName)
                                .resizable()
                                .scaledToFit()
                                .frame(width: 28, height: 28)
                        } else {
                            Image(systemName: provider.icon)
                                .font(.system(size: 18, weight: .semibold))
                                .frame(width: 28, height: 28)
                        }
                        VStack(alignment: .leading, spacing: 2) {
                            Text(provider.rawValue)
                                .font(BeansFont.appFont(14, .semibold))
                                .foregroundStyle(Color.beansLabel)
                            Text(store.isEnabled(provider) ? "已显示" : "已隐藏")
                                .font(BeansFont.appFont(11))
                                .foregroundStyle(Color.beansComment)
                        }
                        Spacer()
                        Image(systemName: store.isEnabled(provider) ? "checkmark.circle.fill" : "circle")
                            .font(.system(size: 20, weight: .semibold))
                            .foregroundStyle(store.isEnabled(provider) ? Color.beansAmber : Color.beansComment)
                    }
                    .padding(12)
                    .background {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .fill(store.isEnabled(provider) ? Color.beansAmber.opacity(0.12) : Color.primary.opacity(0.04))
                    }
                    .overlay {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .strokeBorder(store.isEnabled(provider) ? Color.beansAmber.opacity(0.35) : Color.beansComment.opacity(0.12), lineWidth: 0.8)
                    }
                }
                .buttonStyle(.plain)
            }
        }
    }
}
