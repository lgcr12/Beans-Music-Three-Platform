import SwiftUI
import CoreText
import Observation
import UIKit

@Observable
final class TextScaleStore {
    static let shared = TextScaleStore()

    static let defaultsKey = "beans.textScalePercent"
    static let minimumPercent = 85.0
    static let maximumPercent = 140.0

    private(set) var percent: Double
    private(set) var contentSizeRevision = 0

    private init() {
        let stored = UserDefaults.standard.double(forKey: Self.defaultsKey)
        percent = Self.clamped(stored == 0 ? 100 : stored)
        NotificationCenter.default.addObserver(
            forName: UIContentSizeCategory.didChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.contentSizeRevision += 1
        }
    }

    var factor: CGFloat {
        _ = contentSizeRevision
        return CGFloat(percent / 100)
    }

    func setPercent(_ value: Double, sync: Bool = true) {
        let next = Self.clamped((value / 5).rounded() * 5)
        guard next != percent else { return }
        percent = next
        UserDefaults.standard.set(next, forKey: Self.defaultsKey)
        if sync {
            Task { @MainActor in
                BeansAccountStore.shared.queueSync(
                    entityType: "theme",
                    entityID: "current",
                    payload: ThemeStore.shared.syncPayload
                )
            }
        }
    }

    func reset() {
        setPercent(100)
    }

    private static func clamped(_ value: Double) -> Double {
        min(max(value, minimumPercent), maximumPercent)
    }
}

/// 全局字体管理：把用户上传的 ttf/otf 复制到 Documents/Fonts 并动态注册，App 重启后自动重新注册
enum FontManager {
    static let storedFontNameKey = "beans.globalFont"

    private static var cachedInstalledFontName: String? = UserDefaults.standard.string(forKey: storedFontNameKey)

    static var installedFontName: String? {
        get { cachedInstalledFontName }
        set {
            cachedInstalledFontName = newValue
            UserDefaults.standard.set(newValue, forKey: storedFontNameKey)
        }
    }

    private static var fontsDirectory: URL {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let dir = docs.appendingPathComponent("Fonts", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// 启动时重新注册已安装的字体（覆盖安装后 Documents 保留，字体继续生效）
    static func reinstallIfNeeded() {
        guard installedFontName != nil else { return }
        guard let files = try? FileManager.default.contentsOfDirectory(at: fontsDirectory, includingPropertiesForKeys: nil) else { return }
        for file in files where ["ttf", "otf", "ttc"].contains(file.pathExtension.lowercased()) {
            register(file)
        }
    }

    /// 安装用户选择的字体文件（先清空旧字体再复制注册），返回注册成功后的字体名
    @discardableResult
    static func install(from sourceURL: URL) -> String? {
        let file = fontsDirectory.appendingPathComponent(sourceURL.lastPathComponent)
        if let files = try? FileManager.default.contentsOfDirectory(at: fontsDirectory, includingPropertiesForKeys: nil) {
            for f in files { try? FileManager.default.removeItem(at: f) }
        }
        if FileManager.default.fileExists(atPath: file.path) { try? FileManager.default.removeItem(at: file) }
        guard (try? FileManager.default.copyItem(at: sourceURL, to: file)) != nil else { return nil }
        guard let name = register(file) else { return nil }
        installedFontName = name
        return name
    }

    /// 导出字体信息（字体名 + 字体文件数据），供配置备份使用
    static func exportFontData() -> (name: String, data: Data)? {
        guard installedFontName != nil else { return nil }
        guard let files = try? FileManager.default.contentsOfDirectory(at: fontsDirectory, includingPropertiesForKeys: nil) else { return nil }
        for file in files where ["ttf", "otf", "ttc"].contains(file.pathExtension.lowercased()) {
            if let data = try? Data(contentsOf: file) { return (installedFontName ?? "", data) }
        }
        return nil
    }

    /// 从备份恢复字体：清空旧字体 → 写回字体文件 → 重新注册
    @discardableResult
    static func restoreFont(name: String, data: Data) -> Bool {
        clear()
        let file = fontsDirectory.appendingPathComponent("beans-restored-font.ttf")
        do {
            try data.write(to: file, options: .atomic)
        } catch {
            return false
        }
        guard let registered = register(file) else { return false }
        installedFontName = registered
        return true
    }

    static func clear() {
        installedFontName = nil
        if let files = try? FileManager.default.contentsOfDirectory(at: fontsDirectory, includingPropertiesForKeys: nil) {
            for f in files { try? FileManager.default.removeItem(at: f) }
        }
    }

    @discardableResult
    private static func register(_ url: URL) -> String? {
        // 用 Data 读取避免安全作用域/CGDataProvider(url:) 兼容问题，iOS 上更稳
        guard let data = try? Data(contentsOf: url) else { return nil }
        guard let provider = CGDataProvider(data: data as CFData), let font = CGFont(provider) else { return nil }
        var error: Unmanaged<CFError>?
        guard CTFontManagerRegisterGraphicsFont(font, &error) else { return nil }
        return font.postScriptName as String?
    }
}

/// 全局字体快捷入口：已上传字体时全局使用自定义字体，否则回退系统字体（支持 design 回退）
enum BeansFont {
    static func appFont(_ size: CGFloat, _ weight: Font.Weight = .regular, _ design: Font.Design = .default) -> Font {
        let scaledBase = size * TextScaleStore.shared.factor
        let textStyle = uiTextStyle(for: size)
        let metrics = UIFontMetrics(forTextStyle: textStyle)
        let uiWeight = uiFontWeight(weight)
        let baseFont: UIFont
        if let name = FontManager.installedFontName {
            baseFont = UIFont(name: name, size: scaledBase) ?? .systemFont(ofSize: scaledBase, weight: uiWeight)
        } else {
            let descriptor = UIFont.systemFont(ofSize: scaledBase, weight: uiWeight).fontDescriptor
            let designed = descriptor.withDesign(uiDesign(design)) ?? descriptor
            baseFont = UIFont(descriptor: designed, size: scaledBase)
        }
        return Font(metrics.scaledFont(for: baseFont))
    }

    /// UIKit 版字体（UITextField 等 SwiftUI 不覆盖的控件用）
    static func appUIFont(_ size: CGFloat, _ weight: UIFont.Weight = .regular) -> UIFont {
        let scaledBase = size * TextScaleStore.shared.factor
        let baseFont: UIFont
        if let name = FontManager.installedFontName {
            baseFont = UIFont(name: name, size: scaledBase) ?? .systemFont(ofSize: scaledBase, weight: weight)
        } else {
            baseFont = .systemFont(ofSize: scaledBase, weight: weight)
        }
        return UIFontMetrics(forTextStyle: uiTextStyle(for: size)).scaledFont(for: baseFont)
    }

    /// 歌词有独立字号滑杆，只叠加系统辅助字号，不再叠加全局 UI 比例。
    static func lyricFont(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        let uiWeight = uiFontWeight(weight)
        let baseFont: UIFont
        if let name = FontManager.installedFontName {
            baseFont = UIFont(name: name, size: size) ?? .systemFont(ofSize: size, weight: uiWeight)
        } else {
            baseFont = .systemFont(ofSize: size, weight: uiWeight)
        }
        return Font(UIFontMetrics(forTextStyle: uiTextStyle(for: size)).scaledFont(for: baseFont))
    }

    private static func uiTextStyle(for size: CGFloat) -> UIFont.TextStyle {
        switch size {
        case 24...: return .largeTitle
        case 20..<24: return .title2
        case 17..<20: return .headline
        case 15..<17: return .body
        case 13..<15: return .subheadline
        case 11..<13: return .footnote
        default: return .caption2
        }
    }

    private static func uiFontWeight(_ weight: Font.Weight) -> UIFont.Weight {
        switch weight {
        case .ultraLight: return .ultraLight
        case .thin: return .thin
        case .light: return .light
        case .medium: return .medium
        case .semibold: return .semibold
        case .bold: return .bold
        case .heavy: return .heavy
        case .black: return .black
        default: return .regular
        }
    }

    private static func uiDesign(_ design: Font.Design) -> UIFontDescriptor.SystemDesign {
        switch design {
        case .rounded: return .rounded
        case .serif: return .serif
        case .monospaced: return .monospaced
        default: return .default
        }
    }
}
