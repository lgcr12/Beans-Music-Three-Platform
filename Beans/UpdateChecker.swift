import Foundation

// MARK: - 更新检测（GitHub Releases API）

/// 检测当前平台的 GitHub Release 通道并与当前版本比较，发现新版时用于弹窗提示。
/// 自动检查每次启动一次，手动检查随时可用。
struct UpdateChecker {
    static let repoPath = "lgcr12/Beans-Music-Three-Platform"
    static let releasePageURL = URL(string: "https://github.com/\(repoPath)/releases")!
    private static let releasesAPI = URL(string: "https://api.github.com/repos/\(repoPath)/releases?per_page=30")!
#if targetEnvironment(macCatalyst)
    private static let channel = "mac"
#else
    private static let channel = "ios"
#endif
    private static let suppressedVersionKey = "beans.updateCheck.suppressedVersion"
    static let automaticEnabledKey = "beans.updateCheck.automaticEnabled"
    private static let lastAutomaticCheckKey = "beans.updateCheck.lastAutomaticCheck"
    private static var etagKey: String { "beans.updateCheck.\(channel).etag" }
    private static var cachedReleaseKey: String { "beans.updateCheck.\(channel).cachedRelease" }
    private static let lastPromptedVersionKey = "beans.updateCheck.lastPromptedVersion"
    private static let remindLaterVersionKey = "beans.updateCheck.remindLaterVersion"
    private static let automaticInterval: TimeInterval = 24 * 60 * 60
    private static var automaticRequestInFlight = false

    /// 最新 Release 信息
    struct ReleaseInfo: Codable {
        let version: String
        let name: String
        let body: String
        let htmlURL: URL
        let publishedAt: Date?
        let assets: [String: URL]
        /// 当前 Apple 目标对应的安装包直链。
        let assetURL: URL?
    }

    private struct GitHubAsset: Decodable {
        let name: String
        let browserDownloadURL: URL

        enum CodingKeys: String, CodingKey {
            case name
            case browserDownloadURL = "browser_download_url"
        }
    }

    private struct GitHubRelease: Decodable {
        let tagName: String
        let name: String?
        let body: String?
        let htmlURL: URL
        let publishedAt: Date?
        let draft: Bool
        let prerelease: Bool
        let assets: [GitHubAsset]

        enum CodingKeys: String, CodingKey {
            case tagName = "tag_name"
            case name, body, draft, prerelease, assets
            case htmlURL = "html_url"
            case publishedAt = "published_at"
        }
    }

    enum CheckResult {
        case update(ReleaseInfo)
        case upToDate
        case failed
    }

    /// 当前 App 版本号（CFBundleShortVersionString）
    static var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
    }

    static var automaticEnabled: Bool {
        get {
            UserDefaults.standard.register(defaults: [automaticEnabledKey: true])
            return UserDefaults.standard.bool(forKey: automaticEnabledKey)
        }
        set { UserDefaults.standard.set(newValue, forKey: automaticEnabledKey) }
    }

    /// 自动检查：启动、回到前台或持续运行到期时调用；24 小时内不重复请求。
    static func checkIfNeeded() async -> ReleaseInfo? {
        guard automaticEnabled else { return nil }
        guard !automaticRequestInFlight else { return nil }
        let defaults = UserDefaults.standard
        if let last = defaults.object(forKey: lastAutomaticCheckKey) as? Date,
           Date().timeIntervalSince(last) < automaticInterval {
            return nil
        }
        automaticRequestInFlight = true
        defer { automaticRequestInFlight = false }
        guard let info = try? await fetchLatest() else { return nil }
        defaults.set(Date(), forKey: lastAutomaticCheckKey)
        guard isNewer(info.version, than: currentVersion) else { return nil }
        if defaults.string(forKey: suppressedVersionKey) == info.version { return nil }
        let reminded = defaults.string(forKey: lastPromptedVersionKey) == info.version
        let requestedReminder = defaults.string(forKey: remindLaterVersionKey) == info.version
        guard !reminded || requestedReminder else { return nil }
        defaults.set(info.version, forKey: lastPromptedVersionKey)
        defaults.removeObject(forKey: remindLaterVersionKey)
        return info
    }

    /// 手动检查：总是请求，返回明确结果（有新版本 / 已是最新 / 检查失败）
    static func checkNow() async -> CheckResult {
        do {
            let info = try await fetchLatest()
            return isNewer(info.version, than: currentVersion) ? .update(info) : .upToDate
        } catch {
            return .failed
        }
    }

    static func suppress(version: String) {
        UserDefaults.standard.set(version, forKey: suppressedVersionKey)
        UserDefaults.standard.removeObject(forKey: remindLaterVersionKey)
    }

    static func remindLater(version: String) {
        UserDefaults.standard.set(version, forKey: remindLaterVersionKey)
    }

    /// 拉取最新 Release（公开仓库无需 Token）
    static func fetchLatest() async throws -> ReleaseInfo {
        var request = URLRequest(url: releasesAPI)
        request.setValue("Beans-Music/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        if let etag = UserDefaults.standard.string(forKey: etagKey), !etag.isEmpty {
            request.setValue(etag, forHTTPHeaderField: "If-None-Match")
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw URLError(.badServerResponse)
        }
        if http.statusCode == 304,
           let cached = UserDefaults.standard.data(forKey: cachedReleaseKey),
           let info = try? JSONDecoder().decode(ReleaseInfo.self, from: cached) {
            return info
        }
        guard http.statusCode == 200 else {
            throw URLError(.cannotParseResponse)
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let releases = try decoder.decode([GitHubRelease].self, from: data)
        let release = releases.first { isChannelRelease($0, legacy: false) }
            ?? releases.first { isChannelRelease($0, legacy: true) }
        guard let release,
              let version = version(from: release.tagName),
              let asset = release.assets.first(where: isPlatformAsset) else {
            throw URLError(.resourceUnavailable)
        }
        let assets = Dictionary(uniqueKeysWithValues: release.assets.map { ($0.name, $0.browserDownloadURL) })
        let assetURL = asset.browserDownloadURL
        let info = ReleaseInfo(
            version: version,
            name: release.name ?? release.tagName,
            body: release.body ?? "",
            htmlURL: release.htmlURL,
            publishedAt: release.publishedAt,
            assets: assets,
            assetURL: assetURL
        )
        if let etag = http.value(forHTTPHeaderField: "ETag") {
            UserDefaults.standard.set(etag, forKey: etagKey)
        }
        if let cached = try? JSONEncoder().encode(info) {
            UserDefaults.standard.set(cached, forKey: cachedReleaseKey)
        }
        return info
    }

    private static func isChannelRelease(_ release: GitHubRelease, legacy: Bool) -> Bool {
        guard !release.draft, !release.prerelease,
              release.assets.contains(where: isPlatformAsset) else { return false }
        let tag = release.tagName.lowercased()
        return legacy ? tag.first == "v" : tag.hasPrefix("\(channel)-v")
    }

    private static func isPlatformAsset(_ asset: GitHubAsset) -> Bool {
        let name = asset.name.lowercased()
#if targetEnvironment(macCatalyst)
        return name.hasSuffix(".zip") && (name.contains("catalyst") || name.contains("macos"))
#else
        return name.hasSuffix(".ipa") && name.contains("ios")
#endif
    }

    private static func version(from tag: String) -> String? {
        let lower = tag.lowercased()
        let raw: Substring
        if lower.hasPrefix("\(channel)-v") {
            raw = tag.dropFirst(channel.count + 2)
        } else if lower.first == "v" {
            raw = tag.dropFirst()
        } else {
            return nil
        }
        let version = raw.split(separator: "-", maxSplits: 1).first.map(String.init) ?? ""
        return !version.isEmpty && version.split(separator: ".").allSatisfy { Int($0) != nil } ? version : nil
    }

    /// 三段式版本号比较：remote 大于 current 返回 true
    static func isNewer(_ remote: String, than current: String) -> Bool {
        func parts(_ v: String) -> [Int] {
            v.split(separator: ".").compactMap { Int($0) }
        }
        let r = parts(remote)
        let c = parts(current)
        let count = max(r.count, c.count)
        for i in 0..<count {
            let a = i < r.count ? r[i] : 0
            let b = i < c.count ? c[i] : 0
            if a != b { return a > b }
        }
        return false
    }

}
